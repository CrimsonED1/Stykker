using System.Net;
using System.Text.Json;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Kopplung mit sechs Ziffern in beide Richtungen und die Nodes eines Hubs (docs/nodes.md, N1/N2).
// Alles ohne Netz: die Uhr ist gestellt, ein Node antwortet über den FakeHandler.
[TestClass]
public class N50_PairingAndNodesTests
{
    private sealed class Clock
    {
        public DateTime Now = new(2026, 10, 6, 12, 0, 0);
        public DateTime Get() => Now;
    }

    private static AppPaths TempPaths(string tag)
    {
        var p = new AppPaths(Path.Combine(Path.GetTempPath(), "slm-n50-" + tag + "-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(p.Root);
        return p;
    }

    // ── Der Code des PCs: sechs Ziffern, zehn Minuten, einmal ──

    [TestMethod]
    public void Code_IsSixDigits_ExpiresAfterTenMinutes_AndIsUsedOnce()
    {
        var clock = new Clock();
        var access = new AccessControl(TempPaths("code"), new FakePlatform(), clock.Get);
        var code = access.Code;
        Assert.AreEqual(6, code.Length);
        Assert.IsTrue(code.All(char.IsAsciiDigit));
        Assert.AreEqual(clock.Now + AccessControl.CodeLifetime, access.CodeExpires);
        Assert.AreEqual(code[..3] + " " + code[3..], AccessControl.Pretty(code));

        // abgetippt mit Leerzeichen gilt genauso – und danach ist der Code verbraucht
        Assert.IsNotNull(access.Pair(AccessControl.Pretty(code), "Handy", "10.0.0.5"));
        Assert.AreNotEqual(code, access.Code, "einmal gültig");
        Assert.IsNull(access.Pair(code, "Zweites Handy", "10.0.0.6"), "der alte Code gilt nicht mehr");

        // abgelaufen: kein Zugang, Maintain (Takt des Servers) ersetzt ihn
        var second = access.Code;
        clock.Now += AccessControl.CodeLifetime + TimeSpan.FromSeconds(1);
        Assert.IsNull(access.Pair(second, "Spät", null), "nach zehn Minuten abgelaufen");
        Assert.IsTrue(access.Maintain() || access.Code != second);
        Assert.AreNotEqual(second, access.Code);
        Assert.IsNotNull(access.Pair(access.Code, "Rechtzeitig", null));
    }

    [TestMethod]
    public void Code_ChangesAfterFiveWrongTries()
    {
        var access = new AccessControl(TempPaths("wrong"), new FakePlatform());
        var code = access.Code;
        var wrong = code == "000000" ? "111111" : "000000";
        for (int i = 0; i < AccessControl.MaxWrongTries - 1; i++) Assert.IsNull(access.Pair(wrong, "Rater", null));
        Assert.AreEqual(code, access.Code, "vier Fehlversuche lassen den Code stehen");
        Assert.IsNull(access.Pair(wrong, "Rater", null));
        Assert.AreNotEqual(code, access.Code, "nach dem fünften gibt es einen neuen");
        Assert.IsNull(access.Pair(code, "Rater", null), "und der alte ist tot");
    }

    [TestMethod]
    public void OldEightCharacterCode_IsReplacedOnLoad_DevicesStay()
    {
        var paths = TempPaths("old");
        File.WriteAllText(Path.Combine(paths.Root, AccessControl.FileName),
            """{ "RemoteEnabled": true, "Code": "ABCD2345", "Devices": [ { "Id": "alt1", "Name": "Handy", "Hash": "h" } ] }""");
        var access = new AccessControl(paths, new FakePlatform());
        Assert.AreEqual(6, access.Code.Length);
        Assert.IsTrue(access.Code.All(char.IsAsciiDigit));
        Assert.AreEqual(1, access.Devices.Count);
        Assert.IsTrue(access.RemoteEnabled);
        Assert.AreEqual(access.Code, new AccessControl(paths, new FakePlatform()).Code, "der neue Code wurde gespeichert");
    }

    // ── Andersherum: das neue Gerät zeigt den Code ──

    [TestMethod]
    public void Request_ApproveWithItsDigits_PollHandsOutTheTokenOnce()
    {
        var access = new AccessControl(TempPaths("req"), new FakePlatform());
        var (req, secret) = access.RequestPairing("Tablet", PairKinds.Browser, "10.0.0.9")!.Value;
        Assert.AreEqual(6, req.Code.Length);
        Assert.AreNotEqual(access.Code, req.Code);
        Assert.AreEqual(1, access.PendingRequests.Count);

        Assert.AreEqual((PairStatus.Pending, (string?)null), access.Poll(req.Id, secret));
        Assert.AreEqual(PairStatus.Unknown, access.Poll(req.Id, "falsch").Status, "ohne Secret erfährt man nichts");
        Assert.IsNull(access.Approve("999999" == req.Code ? "888888" : "999999"), "fremde Ziffern geben nichts frei");
        Assert.IsNull(access.Approve(access.Code), "der Code des PCs ist kein Anfrage-Code");

        var approved = access.Approve(AccessControl.Pretty(req.Code), AccessRole.Viewer);
        Assert.IsNotNull(approved);
        Assert.AreEqual(0, access.PendingRequests.Count);
        var (status, token) = access.Poll(req.Id, secret);
        Assert.AreEqual(PairStatus.Approved, status);
        Assert.AreEqual(AccessRole.Viewer, access.RoleOf(token));
        Assert.AreEqual(PairStatus.Unknown, access.Poll(req.Id, secret).Status, "das Token gibt es genau einmal");
        Assert.AreEqual("Tablet", access.Devices.Single().Name);
    }

    [TestMethod]
    public void Request_FromAHub_GetsTheHubRole_DenyAndExpiryAreReported()
    {
        var clock = new Clock();
        var access = new AccessControl(TempPaths("hub"), new FakePlatform(), clock.Get);
        var (hub, hubSecret) = access.RequestPairing("HAUPT-PC", PairKinds.Hub, "10.0.0.2")!.Value;
        access.Approve(hub.Code, AccessRole.Viewer);                     // die Rolle eines Hubs lässt sich nicht wählen
        var token = access.Poll(hub.Id, hubSecret).Token;
        Assert.AreEqual(AccessRole.Hub, access.RoleOf(token));
        Assert.IsTrue(AccessRole.CanWrite(AccessRole.Hub));
        Assert.IsFalse(AccessRole.IsValid(AccessRole.Hub), "von Hand vergeben lässt sich Hub nicht");

        var (denied, deniedSecret) = access.RequestPairing("Fremd", null, "10.0.0.66")!.Value;
        Assert.IsTrue(access.Deny(denied.Id));
        Assert.AreEqual(PairStatus.Denied, access.Poll(denied.Id, deniedSecret).Status);
        Assert.IsNull(access.Approve(denied.Code), "abgelehnt bleibt abgelehnt");

        var (late, lateSecret) = access.RequestPairing("Langsam", null, null)!.Value;
        clock.Now += AccessControl.CodeLifetime + TimeSpan.FromSeconds(1);
        Assert.IsNull(access.Approve(late.Code), "abgelaufen");
        Assert.AreEqual(PairStatus.Expired, access.Poll(late.Id, lateSecret).Status);
    }

    // Die Fenster-Hülle holt sich ohne Code ein Gerät (der Server gibt es nur mit dem Schlüssel des Datenordners aus)
    [TestMethod]
    public void LocalDevice_NeedsNoCode_AndLeavesTheCodeUntouched()
    {
        var access = new AccessControl(TempPaths("local"), new FakePlatform());
        var code = access.Code;
        var (token, device) = access.AddLocalDevice(Strings.ShellDeviceName, "127.0.0.1");
        Assert.AreEqual(AccessRole.Admin, access.RoleOf(token));
        Assert.AreEqual(Strings.ShellDeviceName, device.Name);
        Assert.AreEqual(code, access.Code, "der Code fürs Handy bleibt gültig");
        Assert.AreEqual(1, access.Devices.Count);
    }

    [TestMethod]
    public void Requests_AreLimited_SoNobodyCanFloodTheList()
    {
        var access = new AccessControl(TempPaths("flood"), new FakePlatform());
        for (int i = 0; i < AccessControl.MaxPendingRequests; i++) Assert.IsNotNull(access.RequestPairing("x" + i, null, null));
        Assert.IsNull(access.RequestPairing("zu viel", null, null));
    }

    [TestMethod]
    public void State_ListsPendingRequestsWithoutTheirCode_AndTheExpiry()
    {
        var paths = TempPaths("state");
        var settings = AppSettings.Load(paths.SettingsFile);
        using var engine = new MonitorEngine(new FakePlatform(), paths, settings, readOnly: false);
        var access = new AccessControl(paths, new FakePlatform());
        var (req, _) = access.RequestPairing("Tablet", PairKinds.Browser, "10.0.0.9")!.Value;

        var json = StateJson.WriteText(engine, access, null, 8078, DateTimeOffset.Now);
        Assert.IsFalse(json.Contains(req.Code, StringComparison.Ordinal), "der Code der Anfrage steht nicht im Zustand");
        var state = StateSnapshot.Parse(json);
        Assert.AreEqual("Tablet", state.Access.Requests.Single().Name);
        Assert.AreEqual(req.Id, state.Access.Requests.Single().Id);
        Assert.IsNotNull(state.Access.CodeExpires);

        var viewerJson = StateJson.WriteText(engine, access, null, 8078, DateTimeOffset.Now, withCode: false);
        Assert.AreEqual(0, StateSnapshot.Parse(viewerJson).Access.Requests.Count, "ein Viewer sieht keine Anfragen");
    }

    [TestMethod]
    public async Task Actions_ApproveAndDeny_AndAHubMayNotTouchAccess()
    {
        var paths = TempPaths("actions");
        var settings = AppSettings.Load(paths.SettingsFile);
        using var engine = new MonitorEngine(new FakePlatform(), paths, settings, readOnly: false);
        var access = new AccessControl(paths, new FakePlatform());
        var ctx = new ActionContext { Engine = engine, Launcher = new LaunchCoordinator(engine, new RemotePrompt()), Access = access, ServerPort = 8078 };

        var (req, secret) = access.RequestPairing("Tablet", null, null)!.Value;
        var ok = await ActionApi.ExecuteAsync(new ActionRequest { Action = "pair.approve", Arg = req.Code }, ctx, new RemotePrompt());
        Assert.IsTrue(ok.Ok, ok.Message);
        Assert.AreEqual(PairStatus.Approved, access.Poll(req.Id, secret).Status);
        var unknown = await ActionApi.ExecuteAsync(new ActionRequest { Action = "pair.approve", Arg = "123" }, ctx, new RemotePrompt());
        Assert.IsFalse(unknown.Ok);

        foreach (var action in new[] { "code.rotate", "device.remove", "device.role", "remote.set", "pair.approve", "pair.deny", "shutdown", "node.remove" })
        {
            var r = await ActionApi.ExecuteAsync(new ActionRequest { Action = action, Arg = "x" }, ctx, new RemotePrompt(), role: AccessRole.Hub);
            Assert.IsFalse(r.Ok, action);
            Assert.AreEqual(Strings.NodeHubNotAllowed, r.Message, action);
        }
    }

    // ── Nodes: koppeln, speichern, Zustand ──

    // Ein Node, wie ihn der Hub sieht: /api/ping, /pair/request, /pair/poll (nach dem zweiten Mal freigegeben),
    // /pair/token mit dem richtigen Code, /pair/forget, /api/state
    private static FakeHandler FakeNode(string host, string code = "482913", int approveAfter = 1)
    {
        int polls = 0;
        var h = new FakeHandler();
        h.Responder = (key, body) =>
        {
            if (!key.StartsWith(host, StringComparison.Ordinal)) return null;
            var path = key[host.Length..];
            return path switch
            {
                "/api/ping" => (HttpStatusCode.OK, """{"ok":true,"app":"StykkerLLM-Server","port":8078,"remote":true,"name":"ALEXPC"}"""),
                "/pair/request" => (HttpStatusCode.OK, """{"ok":true,"id":"r1","secret":"s1","code":"731402","pretty":"731 402","seconds":600}"""),
                "/pair/poll" => ++polls > approveAfter
                    ? (HttpStatusCode.OK, """{"ok":true,"status":"approved","token":"TOKEN-NETFLIX"}""")
                    : (HttpStatusCode.OK, """{"ok":true,"status":"pending"}"""),
                "/pair/token" => body != null && body.Contains(code, StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"ok":true,"token":"TOKEN-CODE","name":"ALEXPC"}""")
                    : (HttpStatusCode.Forbidden, """{"ok":false,"message":"wrong"}"""),
                "/pair/forget" => (HttpStatusCode.OK, """{"ok":true}"""),
                "/api/state" => (HttpStatusCode.OK, NodeStateJsonText()),
                _ => null,
            };
        };
        return h;
    }

    // Zustand eines Nodes mit GPU, einem laufenden Server und einem Modell für Tests
    private static string NodeStateJsonText() => """
        {"schema":1,"time":"2026-10-06T12:00:00+02:00","ticks":5,"intervalMs":1000,"serverPort":8078,
         "gpu":{"name":"RTX 4090","util":40,"vramUsedGb":21,"vramTotalGb":24,"vramFreeGb":3},
         "proxy":{"enabled":true,"running":true,"bindLan":true,"port":17500},
         "servers":[{"key":"k1","name":"gemma","model":"gemma-4-26b","state":"busy","online":true,"backend":"llama.cpp","tps":55.5}],
         "eval":{"running":true,"models":[{"id":"m1","name":"gemma-4-26b","modelFile":"C:\\m\\gemma-4-26b.gguf","sizeGb":15.2,"enabled":true}],
                 "jobs":[{"id":"j1","model":"gemma-4-26b","suite":"hard","state":"running","done":12,"total":27},{"id":"j2","state":"queued"}]}}
        """;

    [TestMethod]
    public void NormalizeUrl_AcceptsWhatPeopleType()
    {
        Assert.AreEqual("http://192.168.178.52:8078", NodeRegistry.NormalizeUrl("192.168.178.52"));
        Assert.AreEqual("http://alexpc:8078", NodeRegistry.NormalizeUrl("alexpc"));
        Assert.AreEqual("http://alexpc:9000", NodeRegistry.NormalizeUrl("alexpc:9000"));
        Assert.AreEqual("http://alexpc:8078", NodeRegistry.NormalizeUrl(" http://alexpc:8078/ "));
        Assert.AreEqual("", NodeRegistry.NormalizeUrl("ftp://x"));
        Assert.AreEqual("", NodeRegistry.NormalizeUrl(""));
    }

    [TestMethod]
    public async Task Pair_TheNetflixWay_HubShowsTheCode_NodeApproves_TokenIsStoredProtected()
    {
        var paths = TempPaths("netflix");
        var platform = new FakePlatform { UserProtection = true };
        var fake = FakeNode("alexpc:8078");
        using var nodes = new NodeRegistry(paths, platform, fake);

        var r = await nodes.StartPairAsync("alexpc");
        Assert.IsTrue(r.Ok, r.Message);
        Assert.AreEqual("731402", r.Data);
        Assert.AreEqual("731402", nodes.Pairing!.Code);
        Assert.AreEqual("ALEXPC", nodes.Pairing.Name);
        StringAssert.Contains(fake.Posts.First(p => p.Contains("/pair/request")), "\"kind\":\"hub\"");

        var file0 = Path.Combine(paths.Root, NodeRegistry.FileName);
        for (int i = 0; i < 50 && (nodes.Nodes.Count == 0 || !File.Exists(file0)); i++) await Task.Delay(200);
        Assert.AreEqual(1, nodes.Nodes.Count, "nach der Freigabe am Node steht er in der Liste");
        Assert.AreEqual(PairStatus.Approved, nodes.Pairing.Status);
        Assert.AreEqual("ALEXPC", nodes.Nodes[0].Entry.Name);
        Assert.AreEqual("TOKEN-NETFLIX", nodes.Nodes[0].Entry.Token);

        var file = File.ReadAllText(Path.Combine(paths.Root, NodeRegistry.FileName));
        Assert.IsFalse(file.Contains("TOKEN-NETFLIX", StringComparison.Ordinal), "das Token steht nicht im Klartext in nodes.dat");
        using var again = new NodeRegistry(paths, platform, fake);
        Assert.AreEqual("TOKEN-NETFLIX", again.Nodes.Single().Entry.Token, "nach dem Neustart wieder da");
    }

    [TestMethod]
    public async Task Pair_WithTheNodesCode_AndRemoveTellsTheNode()
    {
        var paths = TempPaths("code");
        var fake = FakeNode("192.168.178.52:8078");
        using var nodes = new NodeRegistry(paths, new FakePlatform(), fake);

        var wrong = await nodes.PairWithCodeAsync("192.168.178.52", "111 111");
        Assert.IsFalse(wrong.Ok);
        var ok = await nodes.PairWithCodeAsync("192.168.178.52", "482 913");
        Assert.IsTrue(ok.Ok, ok.Message);
        Assert.AreEqual("TOKEN-CODE", nodes.Nodes.Single().Entry.Token);

        // dieselbe Adresse noch einmal: ersetzt, nicht verdoppelt
        await nodes.PairWithCodeAsync("http://192.168.178.52:8078", "482913");
        Assert.AreEqual(1, nodes.Nodes.Count);

        Assert.IsTrue(await nodes.RemoveAsync(nodes.Nodes[0].Entry.Id));
        Assert.AreEqual(0, nodes.Nodes.Count);
        Assert.IsTrue(fake.Posts.Any(p => p.StartsWith("POST 192.168.178.52:8078/pair/forget", StringComparison.Ordinal)), "der Node streicht das Token");
    }

    [TestMethod]
    public async Task Pair_ToSomethingThatIsNoStykker_FailsClearly()
    {
        using var nodes = new NodeRegistry(TempPaths("none"), new FakePlatform(), new FakeHandler());
        var r = await nodes.StartPairAsync("10.9.9.9");
        Assert.IsFalse(r.Ok);
        Assert.AreEqual(Strings.NodeNotReachable, r.Message);
        Assert.AreEqual(0, nodes.Nodes.Count);
    }

    [TestMethod]
    public async Task HubState_SummarizesEachNode_AndClientsReadItBack()
    {
        var paths = TempPaths("hubstate");
        var fake = FakeNode("alexpc:8078");
        using var nodes = new NodeRegistry(paths, new FakePlatform(), fake);
        await nodes.PairWithCodeAsync("alexpc", "482913");
        var link = nodes.Nodes.Single();
        Assert.IsNotNull(await link.Client.GetStateAsync(), "der Node antwortet mit seinem Zustand");
        Assert.IsTrue(fake.Requests.Contains("alexpc:8078/api/state"));

        var settings = AppSettings.Load(paths.SettingsFile);
        using var engine = new MonitorEngine(new FakePlatform(), paths, settings, readOnly: false);
        var json = StateJson.WriteText(engine, null, null, 8078, DateTimeOffset.Now, nodes: nodes);
        Assert.IsFalse(json.Contains("TOKEN-CODE", StringComparison.Ordinal), "kein Token im Zustand");

        var n = StateSnapshot.Parse(json).Nodes.List.Single();
        Assert.IsTrue(n.Online);
        Assert.AreEqual("ALEXPC", n.Name);
        Assert.AreEqual("alexpc", n.Host);
        Assert.AreEqual("RTX 4090", n.GpuName);
        Assert.AreEqual(3, n.VramFreeGb, 0.01);
        Assert.AreEqual("gemma-4-26b", n.Servers.Single().Model);
        Assert.AreEqual(55.5, n.Servers.Single().Tps, 0.01);
        Assert.AreEqual("gemma-4-26b · hard 12/27", n.EvalCurrent);
        Assert.AreEqual(1, n.EvalQueued);
        Assert.AreEqual("gemma-4-26b.gguf", n.Models.Single().File);
        Assert.AreEqual("http://alexpc:17500", n.ProxyUrl);
    }

    [TestMethod]
    public void Discovery_ParsesOnlyStykkerAnswers_AndMarksItself()
    {
        var reply = NodeDiscovery.Reply(8078, true);
        var n = NodeDiscovery.Parse(reply, IPAddress.Parse("192.168.178.52"), new[] { "192.168.178.137" });
        Assert.IsNotNull(n);
        Assert.AreEqual("http://192.168.178.52:8078", n.Url);
        Assert.IsTrue(n.Remote);
        Assert.IsFalse(n.Self);
        Assert.IsTrue(NodeDiscovery.Parse(reply, IPAddress.Parse("192.168.178.137"), new[] { "192.168.178.137" })!.Self);
        Assert.IsNull(NodeDiscovery.Parse("""{"app":"something else","port":1}""", IPAddress.Loopback, Array.Empty<string>()));
        Assert.IsNull(NodeDiscovery.Parse("kein json", IPAddress.Loopback, Array.Empty<string>()));
        Assert.IsFalse(reply.Contains("code", StringComparison.OrdinalIgnoreCase), "die Antwort verrät keinen Code");
    }

    // ── N3: verteilte Tests ──

    private static RemoteNode Pc(string id, double freeVram, bool busy, int queued, params string[] files)
    {
        var n = new RemoteNode { Id = id, Name = id.ToUpperInvariant(), Online = true, VramTotalGb = 24, VramUsedGb = 24 - freeVram,
            EvalCurrent = busy ? "x · basic 1/24" : "", EvalQueued = queued };
        foreach (var f in files) n.Models.Add(new RemoteNodeModel { Id = id + ":" + f, Name = f, File = f, Enabled = true });
        return n;
    }

    [TestMethod]
    public void Scheduler_SendsEachFileWhereItLies_ThePCWithLessWorkFirst()
    {
        var self = Pc("self", 4, busy: true, queued: 0, "a.gguf", "b.gguf");
        var alex = Pc("alex", 20, busy: false, queued: 0, "a.gguf", "c.gguf");
        var lap = Pc("lap", 8, busy: false, queued: 0, "b.gguf");
        var off = Pc("off", 24, busy: false, queued: 0, "a.gguf", "d.gguf");
        off = new RemoteNode { Id = "off", Name = "OFF", Online = false };

        var plan = NodeScheduler.Assign(new[] { "a.gguf", "b.gguf", "c.gguf", "d.gguf" }, self, new[] { alex, lap, off });
        Assert.AreEqual("alex", plan.Single(p => p.File == "a.gguf").NodeId, "frei und mehr VRAM als der beschäftigte Hub");
        Assert.AreEqual("lap", plan.Single(p => p.File == "b.gguf").NodeId, "der Hub testet gerade");
        Assert.AreEqual("alex", plan.Single(p => p.File == "c.gguf").NodeId, "nur dort");
        Assert.IsFalse(plan.Any(p => p.File == "d.gguf"), "liegt nur auf einem Node, der offline ist");
        Assert.AreEqual("alex:c.gguf", plan.Single(p => p.File == "c.gguf").ModelId);

        // Gleiche Last: die Zuteilung dieser Runde zählt mit, damit nicht alles an denselben PC geht
        var x = Pc("x", 10, false, 0, "m1", "m2");
        var y = Pc("y", 10, false, 0, "m1", "m2");
        var spread = NodeScheduler.Assign(new[] { "m1", "m2" }, Pc("self", 0, false, 5), new[] { x, y });
        Assert.AreEqual(2, spread.Select(p => p.NodeId).Distinct().Count());
        CollectionAssert.AreEquivalent(new[] { "a.gguf", "b.gguf", "c.gguf" }, NodeScheduler.Files(self, new[] { alex, lap, off }));
    }

    [TestMethod]
    public async Task ResultSync_FetchesMissingRuns_IntoTheNodesFolder_AndRankingsSeeThem()
    {
        var paths = TempPaths("sync");
        var fake = FakeNode("alexpc:8078");
        var run = new StykkerLlm.Core.Eval.EvalRun { Model = "gemma", Suite = "basic", Machine = "ALEXPC", Started = DateTime.Now };
        var runJson = JsonSerializer.Serialize(run);
        var inner = fake.Responder!;
        fake.Responder = (key, body) => key switch
        {
            "alexpc:8078/api/eval/runs" => (HttpStatusCode.OK, "[\"run1.json\",\"../evil.json\",\"x.txt\"]"),
            "alexpc:8078/api/eval/runs/run1.json" => (HttpStatusCode.OK, runJson),
            _ => inner(key, body),
        };
        using var nodes = new NodeRegistry(paths, new FakePlatform(), fake);
        await nodes.PairWithCodeAsync("alexpc", "482913");
        await nodes.Nodes.Single().Client.GetStateAsync();

        Assert.AreEqual(1, await nodes.SyncResultsAsync(paths.EvalResultsDir));
        Assert.AreEqual(0, await nodes.SyncResultsAsync(paths.EvalResultsDir), "schon da: nichts doppelt");
        Assert.IsTrue(File.Exists(Path.Combine(NodeRegistry.ResultsFolder(paths.EvalResultsDir, "ALEXPC"), "run1.json")));
        Assert.IsFalse(fake.Requests.Any(r => r.Contains("evil", StringComparison.Ordinal)), "keine Pfade aus der Liste des Nodes");

        var runs = StykkerLlm.Core.Eval.EvalSuites.LoadRuns(paths.EvalResultsDir);
        Assert.AreEqual("ALEXPC", runs.Single().Run.Machine, "die Rangliste liest die Läufe der Nodes mit");
        Assert.IsFalse(NodeRegistry.IsRunFileName(@"..\x.json"));
        Assert.IsTrue(NodeRegistry.IsRunFileName("2026-10-06 gemma basic.json"));
    }
}
