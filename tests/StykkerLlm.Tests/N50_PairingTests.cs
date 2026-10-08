using System.Net;
using System.Text.Json;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Kopplung mit sechs Ziffern in beide Richtungen und die Suche nach Servern im Netz. Alles ohne Netz, die Uhr ist gestellt.
[TestClass]
public class N50_PairingTests
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
    public void Request_OldHubRole_OnlyReads_DenyAndExpiryAreReported()
    {
        var clock = new Clock();
        var access = new AccessControl(TempPaths("hub"), new FakePlatform(), clock.Get);
        // Die frühere Rolle „hub“ (alte Node-Kopplung) darf nach dem Entfernen nur noch lesen, nie Admin werden
        Assert.AreEqual(AccessRole.Viewer, AccessRole.Normalize(AccessRole.LegacyHub));
        Assert.IsFalse(AccessRole.CanWrite(AccessRole.LegacyHub));
        Assert.IsFalse(AccessRole.IsValid(AccessRole.LegacyHub));
        var (old, oldSecret) = access.RequestPairing("HAUPT-PC", "hub", "10.0.0.2")!.Value;
        access.Approve(old.Code, AccessRole.Viewer);
        Assert.AreEqual(AccessRole.Viewer, access.RoleOf(access.Poll(old.Id, oldSecret).Token));

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
    public async Task Actions_ApproveAndDeny_AndAnOldHubMayChangeNothing()
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

        foreach (var action in new[] { "code.rotate", "device.remove", "device.role", "remote.set", "pair.approve", "pair.deny", "shutdown", "host.remove" })
        {
            var r = await ActionApi.ExecuteAsync(new ActionRequest { Action = action, Arg = "x" }, ctx, new RemotePrompt(), role: AccessRole.LegacyHub);
            Assert.IsFalse(r.Ok, action);
            Assert.AreEqual(Strings.ViewerOnly, r.Message, action);
        }
    }

    [TestMethod]
    public void Discovery_ParsesOnlyStykkerAnswers_AndMarksItself()
    {
        var reply = NodeDiscovery.Reply(8078, true);
        var n = NodeDiscovery.Parse(reply, IPAddress.Parse("192.168.1.52"), new[] { "192.168.1.137" });
        Assert.IsNotNull(n);
        Assert.AreEqual("http://192.168.1.52:8078", n.Url);
        Assert.IsTrue(n.Remote);
        Assert.IsFalse(n.Self);
        Assert.IsTrue(NodeDiscovery.Parse(reply, IPAddress.Parse("192.168.1.137"), new[] { "192.168.1.137" })!.Self);
        Assert.IsNull(NodeDiscovery.Parse("""{"app":"something else","port":1}""", IPAddress.Loopback, Array.Empty<string>()));
        Assert.IsNull(NodeDiscovery.Parse("kein json", IPAddress.Loopback, Array.Empty<string>()));
        Assert.IsFalse(reply.Contains("code", StringComparison.OrdinalIgnoreCase), "die Antwort verrät keinen Code");
    }

}
