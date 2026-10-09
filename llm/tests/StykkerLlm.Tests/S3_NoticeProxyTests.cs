using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// S3 (Fortsetzung): der anhaltende Hinweis und die Proxy-Angaben (LAN, serviertes Ziel) gehören in den Zustand, damit
// Fenster, Web und TUI dasselbe zeigen. Dazu die Aktionen, die es dafür neu gibt: notice.dismiss, proxy.set, proxy.lan
// und profile.add-history (das Fenster schickte sie, aber der Server kannte sie nicht).
[TestClass]
public class S3_NoticeProxyTests
{
    private static List<SimServerSpec> Servers() => new()
    {
        new SimServerSpec { Kind = BackendKind.LlamaCpp, Name = "sim-a", Model = "model-a", Context = 8192, Slots = 1, RequestsPerMinute = 60, ModelGb = 4 },
    };

    private static ActionContext Ctx(MonitorEngine e) => new()
    {
        Engine = e,
        Launcher = new LaunchCoordinator(e, new RemotePrompt()),
    };

    private static ServerInfo ServerInfoOf() => new()
    {
        Key = "127.0.0.1:8081", Port = 8081, Pid = 10, StartTicks = 1, Program = @"C:\x\llama-server.exe",
        Args = new List<string> { "-m", "m.gguf", "--port", "8081" }, CommandLineReadable = true,
        Params = LlamaServerArgs.Parse(new List<string> { "-m", "m.gguf", "--port", "8081" }), WorkingDir = @"C:\w",
    };

    // Der anhaltende Hinweis steht im Zustand – mit Text, Warnflag und Logdatei; ohne Hinweis fehlt das Feld ganz
    [TestMethod]
    public void State_CarriesTheStandingNotice()
    {
        using var host = new SimHost(Servers(), seed: 3, autoStep: false);
        var e = host.Engine;

        host.Engine.SetNotice(Strings.ProxyContextTooSmallGeneric, @"C:\logs\sim-a.log");
        var json = StateJson.WriteText(e, null, null, 8078, DateTimeOffset.Now);
        var state = StateSnapshot.Parse(json);
        Assert.IsNotNull(state.Notice, "der Hinweis gehört in den Zustand");
        Assert.AreEqual(Strings.ProxyContextTooSmallGeneric, state.Notice!.Text);
        Assert.IsFalse(state.Notice.Alarm, "ein Proxy-Hinweis ist keine Warnung");
        Assert.AreEqual(@"C:\logs\sim-a.log", state.Notice.LogFile);
        StringAssert.Contains(json, "\"notice\":{", "das Feld steht als Objekt im JSON");

        e.DismissNotice();
        Assert.IsNull(StateSnapshot.Parse(StateJson.WriteText(e, null, null, 8078, DateTimeOffset.Now)).Notice,
            "weggeklickter Hinweis: kein Feld mehr, damit die Oberfläche nichts anzeigt");
    }

    // Ohne Logdatei kommt ein leerer String im Zustand an (die TUI zeigt den Pfad nur, wenn es einer ist)
    [TestMethod]
    public void State_NoticeWithoutLogFile_Survives()
    {
        using var host = new SimHost(Servers(), seed: 3, autoStep: false);
        host.Engine.SetNotice("Hinweis");
        var n = StateSnapshot.Parse(StateJson.WriteText(host.Engine, null, null, 8078, DateTimeOffset.Now)).Notice;
        Assert.IsNotNull(n);
        Assert.AreEqual("", n!.LogFile);
    }

    // notice.dismiss räumt den Hinweis weg und meldet sich, wenn keiner da ist
    [TestMethod]
    public async Task NoticeDismiss_ClearsTheNotice()
    {
        using var host = new SimHost(Servers(), seed: 3, autoStep: false);
        var ctx = Ctx(host.Engine);
        host.Engine.SetNotice("Ziel-Kontext zu klein");

        var ok = await ActionApi.ExecuteAsync(new ActionRequest { Action = "notice.dismiss" }, ctx, new RemotePrompt());
        Assert.IsTrue(ok.Ok, ok.Message);
        Assert.IsNull(host.Engine.CurrentNotice);

        var again = await ActionApi.ExecuteAsync(new ActionRequest { Action = "notice.dismiss" }, ctx, new RemotePrompt());
        Assert.IsFalse(again.Ok, "ohne Hinweis gibt es nichts wegzuklicken");
    }

    // proxy.set schaltet genau auf den gewünschten Zustand (die TUI kennt nur ein/aus, keinen zweiten Knopf)
    [TestMethod]
    public async Task ProxySet_SwitchesToTheRequestedState()
    {
        using var host = new SimHost(Servers(), seed: 3, autoStep: false);
        var ctx = Ctx(host.Engine);
        Assert.IsFalse(host.Engine.Proxies.Running, "der Simulator startet ohne Proxy");

        var off = await ActionApi.ExecuteAsync(new ActionRequest { Action = "proxy.set", Flag = false }, ctx, new RemotePrompt());
        Assert.IsTrue(off.Ok, off.Message);
        Assert.IsFalse(host.Engine.Proxies.Running, "schon aus: es bleibt aus");

        var on = await ActionApi.ExecuteAsync(new ActionRequest { Action = "proxy.set", Flag = true }, ctx, new RemotePrompt());
        Assert.IsTrue(on.Ok, on.Message);
        Assert.IsTrue(host.Engine.Proxies.Running, "jetzt läuft er");
        Assert.IsTrue(host.Engine.Settings.ProxyEnabled, "und das merkt sich die Einstellung");

        var offAgain = await ActionApi.ExecuteAsync(new ActionRequest { Action = "proxy.set", Flag = false }, ctx, new RemotePrompt());
        Assert.IsTrue(offAgain.Ok);
        Assert.IsFalse(host.Engine.Proxies.Running);
        Assert.IsFalse(host.Engine.Settings.ProxyEnabled);
    }

    // proxy.lan merkt sich die Freigabe im LAN – und der Zustand sagt es allen Oberflächen
    [TestMethod]
    public async Task ProxyLan_IsStoredAndTravelsInTheState()
    {
        using var host = new SimHost(Servers(), seed: 3, autoStep: false);
        var ctx = Ctx(host.Engine);

        var on = await ActionApi.ExecuteAsync(new ActionRequest { Action = "proxy.lan", Flag = true }, ctx, new RemotePrompt());
        Assert.IsTrue(on.Ok, on.Message);
        Assert.IsTrue(host.Engine.Settings.ProxyBindLan);
        Assert.IsTrue(host.Engine.Proxies.BindLan);
        Assert.AreEqual(Strings.ProxyLanOn, on.Message);

        var state = StateSnapshot.Parse(StateJson.WriteText(host.Engine, null, null, 8078, DateTimeOffset.Now));
        Assert.IsTrue(state.Proxy.BindLan, "die TUI und das Telefon lesen das aus dem Zustand");

        var off = await ActionApi.ExecuteAsync(new ActionRequest { Action = "proxy.lan", Flag = false }, ctx, new RemotePrompt());
        Assert.IsTrue(off.Ok);
        Assert.IsFalse(host.Engine.Settings.ProxyBindLan);
        Assert.AreEqual(Strings.ProxyLanOff, off.Message);
        Assert.IsFalse(StateSnapshot.Parse(StateJson.WriteText(host.Engine, null, null, 8078, DateTimeOffset.Now)).Proxy.BindLan);
    }

    // Der Server-Key des servierten Ziels steht im Zustand (für den Proxy-Chip auf der Serverkarte); ohne laufenden Proxy ist er leer
    [TestMethod]
    public void State_CarriesTheServedServerKey()
    {
        using var host = new SimHost(Servers(), seed: 3, autoStep: false);
        var e = host.Engine;

        Assert.AreEqual("", StateSnapshot.Parse(StateJson.WriteText(e, null, null, 8078, DateTimeOffset.Now)).Proxy.ServedKey,
            "ohne laufenden Proxy dient der Chip keinem Server");

        e.Proxies.Toggle(out _);
        var state = StateSnapshot.Parse(StateJson.WriteText(e, null, null, 8078, DateTimeOffset.Now));
        Assert.IsTrue(state.Proxy.Running, "der Proxy läuft jetzt");
        Assert.AreEqual("", state.Proxy.ServedKey, "ohne gemessenen Server noch kein Ziel");
    }

    // „Aus dem Verlauf merken": das Fenster schickt profile.add-history, der Server kannte die Aktion nicht
    [TestMethod]
    public async Task ProfileAddHistory_SavesTheEntryAsProfile()
    {
        using var host = new SimHost(Servers(), seed: 3, autoStep: false);
        var e = host.Engine;
        var ctx = Ctx(e);
        var info = ServerInfoOf();
        var key = e.Library.Observe(info, new Observation(30, 4.0, 3.5), new DateTime(2026, 1, 1, 12, 0, 0));
        e.Library.Save();

        var res = await ActionApi.ExecuteAsync(new ActionRequest { Action = "profile.add-history", Arg = key!, Name = "mein Test" }, ctx, new RemotePrompt());
        Assert.IsTrue(res.Ok, res.Message);
        var profile = e.Library.Profiles.SingleOrDefault(p => p.Name == "mein Test");
        Assert.IsNotNull(profile, "das gemerkte Profil");
        Assert.IsTrue(profile!.Args.Contains("--port"), "mit derselben Befehlszeile");

        // unbekannter Eintrag
        var missing = await ActionApi.ExecuteAsync(new ActionRequest { Action = "profile.add-history", Arg = "gibtsnicht" }, ctx, new RemotePrompt());
        Assert.IsFalse(missing.Ok);
    }
}