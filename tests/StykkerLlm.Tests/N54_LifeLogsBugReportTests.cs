using System.IO.Compression;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Server an Fenster/TUI gebunden (ServerHolds), ein Protokoll je Programm (AppLog) und der Fehlerbericht (BugReport).
[TestClass]
public class N54_LifeLogsBugReportTests
{
    private static readonly DateTime T0 = new(2026, 10, 7, 1, 0, 0);

    private static AppPaths TempPaths(string tag)
    {
        var p = new AppPaths(Path.Combine(Path.GetTempPath(), "slm-n54-" + tag + "-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(p.Root);
        return p;
    }

    // ── ServerHolds ──

    [TestMethod]
    public void Holds_FreshServer_WaitsForItsFirstClient_ThenStopsAfterGrace()
    {
        var h = new ServerHolds(T0);
        Assert.IsFalse(h.ShouldStop(T0.AddSeconds(30), busy: false), "innerhalb der Startfrist");
        Assert.IsFalse(h.ShouldStop(T0 + ServerHolds.StartGrace, busy: false), "leer seit gerade eben");
        Assert.IsTrue(h.ShouldStop(T0 + ServerHolds.StartGrace + ServerHolds.Grace, busy: false));
    }

    [TestMethod]
    public void Holds_ALeaseKeepsTheServer_UntilItExpires()
    {
        var h = new ServerHolds(T0);
        var t = T0.AddMinutes(5);
        h.Touch("tui:1", t);
        Assert.AreEqual(1, h.Count(t));
        Assert.IsFalse(h.ShouldStop(t.AddSeconds(10), false));
        // TUI abgestürzt: keine Meldung mehr – nach Ablauf der Miete und der Frist ist Schluss
        var after = t + ServerHolds.Lease;
        Assert.AreEqual(0, h.Count(after));
        Assert.IsFalse(h.ShouldStop(after, false));
        Assert.IsTrue(h.ShouldStop(after + ServerHolds.Grace, false));
    }

    [TestMethod]
    public void Holds_LastClientLeaves_StopsQuickly_EvenInsideTheStartGrace()
    {
        var h = new ServerHolds(T0);
        h.Touch("ui:7", T0.AddSeconds(5));
        h.Release("ui:7");
        var t = T0.AddSeconds(6);
        Assert.IsFalse(h.ShouldStop(t, false));
        Assert.IsTrue(h.ShouldStop(t + ServerHolds.QuickGrace, false));
    }

    [TestMethod]
    public void Holds_WebPageOrRunningWork_KeepTheServer()
    {
        var h = new ServerHolds(T0);
        var t = T0.AddMinutes(5);
        h.CircuitUp();
        Assert.IsFalse(h.ShouldStop(t, false));
        Assert.IsFalse(h.ShouldStop(t.AddMinutes(10), false));
        Assert.AreEqual("web 1", h.Describe(t));
        h.CircuitDown();
        Assert.IsFalse(h.ShouldStop(t.AddMinutes(11), busy: true), "Modelltest läuft");
        Assert.IsFalse(h.ShouldStop(t.AddMinutes(12), busy: true));
        Assert.IsFalse(h.ShouldStop(t.AddMinutes(13), false), "Arbeit gerade fertig: Frist beginnt");
        Assert.IsTrue(h.ShouldStop(t.AddMinutes(13) + ServerHolds.Grace, false));
    }

    [TestMethod]
    public void Holds_DescribeNamesTheKinds()
    {
        var h = new ServerHolds(T0);
        h.Touch("ui:1", T0);
        h.Touch("tui:2", T0);
        h.Touch("tui:3", T0);
        Assert.AreEqual("ui 1, tui 2", h.Describe(T0));
        Assert.AreEqual("none", new ServerHolds(T0).Describe(T0));
    }

    [TestMethod]
    public void Holds_WindowOrTuiThatEnded_LetsGoAtOnce()
    {
        // ui:7 läuft noch, tui:8 endete ohne Abmelden (Absturz, geschlossene Konsole)
        var h = new ServerHolds(T0, pid => pid == 7);
        var t = T0.AddMinutes(5);
        h.Touch("tui:8", t);
        Assert.AreEqual(0, h.Count(t), "die Miete des beendeten Prozesses zählt nicht mehr");
        Assert.IsFalse(h.ShouldStop(t, false), "die kurze Frist beginnt");
        Assert.IsTrue(h.ShouldStop(t + ServerHolds.QuickGrace, false), "nach QuickGrace, nicht erst nach Miete und Grace");
    }

    [TestMethod]
    public void Holds_LiveWindowKeepsItsLease_WhileTheDeadOneIsDropped()
    {
        var h = new ServerHolds(T0, pid => pid == 7);
        var t = T0.AddMinutes(5);
        h.Touch("ui:7", t);
        h.Touch("tui:8", t);
        Assert.AreEqual(1, h.Count(t), "ui:7 läuft, tui:8 ist weg");
        Assert.IsFalse(h.ShouldStop(t, false));
    }

    [TestMethod]
    public void Holds_LeaseWithoutProcessNumber_OnlyExpires()
    {
        var h = new ServerHolds(T0, _ => false);
        var t = T0.AddMinutes(5);
        h.Touch("probe", t);
        Assert.AreEqual(1, h.Count(t), "ohne Prozessnummer zählt nur die Miete");
        Assert.AreEqual(0, h.Count(t + ServerHolds.Lease));
    }

    [TestMethod]
    public void Holds_ProcessRunning_SeesItselfAndNotUnknownIds()
    {
        Assert.IsTrue(ServerHolds.ProcessRunning(Environment.ProcessId));
        Assert.IsFalse(ServerHolds.ProcessRunning(int.MaxValue));
    }

    // ── AppLog ──

    [TestMethod]
    public void AppLog_PrefersTheAppFolder_FallsBackToTheDataFolder()
    {
        var paths = TempPaths("log");
        var appDir = Path.Combine(paths.Root, "app");
        Directory.CreateDirectory(appDir);
        Assert.AreEqual(Path.Combine(appDir, "logs", "stykker.log"), AppLog.PickFile("stykker", paths, appDir));

        // "logs" bei der App ist eine Datei, kein Ordner: dort kann nichts hin – also der Datenordner
        var blocked = Path.Combine(paths.Root, "blocked");
        Directory.CreateDirectory(blocked);
        File.WriteAllText(Path.Combine(blocked, "logs"), "x");
        Assert.AreEqual(Path.Combine(paths.LogsDir, "StykkerUI.log"), AppLog.PickFile("StykkerUI", paths, blocked));
    }

    // ── BugReport ──

    [TestMethod]
    public void BugReport_ContainsLogsSettingsState_NeverSecrets_AndBlacksOutKeys()
    {
        var paths = TempPaths("bug");
        var appDir = Path.Combine(paths.Root, "app");
        Directory.CreateDirectory(Path.Combine(appDir, "logs"));
        File.WriteAllText(Path.Combine(appDir, "logs", "StykkerLLM-Server.log"),
            $"start llama-server -m x.gguf --api-key sk-geheim123 --port 8081\nuser {Environment.UserName} on {Environment.MachineName}\n");
        Directory.CreateDirectory(paths.LogsDir);
        File.WriteAllText(Path.Combine(paths.LogsDir, "stykker.log"), "pair http://pc:8078/pair?code=123456 ok\nAuthorization: Bearer abc.def-ghi\n");
        File.WriteAllText(paths.SettingsFile, "{\"Theme\":\"Deep Sea\",\"ProviderKey\":\"sk-provider\"}");
        foreach (var secret in new[] { "access.dat", "server.key", "nodes.dat", "confirm.key", "web-shell.dat" })
            File.WriteAllText(Path.Combine(paths.Root, secret), "SECRET-" + secret);

        var r = BugReport.Create(paths, "Crash when I click Stop\nsteps …", "{\"accessCode\":\"654321\",\"servers\":[]}", T0, appDir, gpu: "RTX test");

        Assert.IsTrue(File.Exists(r.ZipPath));
        using var zip = ZipFile.OpenRead(r.ZipPath);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        CollectionAssert.Contains(names, "report.txt");
        CollectionAssert.Contains(names, "logs/app/StykkerLLM-Server.log");
        CollectionAssert.Contains(names, "logs/data/stykker.log");
        CollectionAssert.Contains(names, "settings.json");
        CollectionAssert.Contains(names, "state.json");
        var all = string.Concat(zip.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));
        foreach (var bad in new[] { "SECRET-", "sk-geheim123", "123456", "abc.def-ghi", "sk-provider", "654321" })
            Assert.IsFalse(all.Contains(bad, StringComparison.Ordinal), bad);
        if (Environment.UserName.Length >= 3) Assert.IsFalse(all.Contains(" " + Environment.UserName + " ", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(all, "Crash when I click Stop");
        StringAssert.Contains(all, "RTX test");

        StringAssert.StartsWith(r.IssueUrl, BugReport.IssueBase + "?title=Crash%20when%20I%20click%20Stop&body=");
        Assert.IsFalse(r.IssueUrl.Contains("sk-", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BugReport_Action_NeedsADescription_AndIsNotForHubsOrViewers()
    {
        var paths = TempPaths("act");
        using var engine = new MonitorEngine(new FakePlatform(), paths, AppSettings.Load(paths.SettingsFile), new HttpClient(new FakeHandler()));
        var ctx = new ActionContext { Engine = engine, Launcher = null! };
        var empty = ActionApi.ExecuteAsync(new ActionRequest { Action = "bugreport.create", Arg = " " }, ctx, null!).GetAwaiter().GetResult();
        Assert.IsFalse(empty.Ok);
        Assert.AreEqual(Strings.BugReportEmpty, empty.Message);

        var hub = ActionApi.ExecuteAsync(new ActionRequest { Action = "bugreport.create", Arg = "x" }, ctx, null!, role: AccessRole.LegacyHub).GetAwaiter().GetResult();
        Assert.IsFalse(hub.Ok);
        var viewer = ActionApi.ExecuteAsync(new ActionRequest { Action = "bugreport.create", Arg = "x" }, ctx, null!, role: AccessRole.Viewer).GetAwaiter().GetResult();
        Assert.IsFalse(viewer.Ok);

        var ok = ActionApi.ExecuteAsync(new ActionRequest { Action = "bugreport.create", Arg = "Something broke" }, ctx, null!).GetAwaiter().GetResult();
        Assert.IsTrue(ok.Ok, ok.Message);
        var parts = ok.Data!.Split('\n');
        Assert.IsTrue(File.Exists(parts[0]));
        StringAssert.StartsWith(parts[1], BugReport.IssueBase);
    }

    [TestMethod]
    public void KeepServer_IsASetting()
    {
        var paths = TempPaths("keep");
        using var engine = new MonitorEngine(new FakePlatform(), paths, AppSettings.Load(paths.SettingsFile), new HttpClient(new FakeHandler()));
        var ctx = new ActionContext { Engine = engine, Launcher = null! };
        var r = ActionApi.ExecuteAsync(new ActionRequest { Action = "settings.set", Values = { ["keepServer"] = "True" } }, ctx, null!).GetAwaiter().GetResult();
        Assert.IsTrue(r.Ok);
        Assert.IsTrue(engine.Settings.KeepServerRunning);
        Assert.IsTrue(AppSettings.Load(paths.SettingsFile).KeepServerRunning, "gespeichert");
    }
}
