using System.Net;
using System.Net.Sockets;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class ProxyManagerTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [TestMethod]
    public void LegacyProxyKeysMigrateToEnabledOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-px-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, "{\"proxyKeys\":[\"127.0.0.1:8081\"]}");
            var s = AppSettings.Load(file);
            Assert.IsTrue(s.ProxyEnabled);
            Assert.AreEqual(0, s.ProxyKeys.Count);
            StringAssert.Contains(File.ReadAllText(file), "\"ProxyEnabled\": true");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void BlockedPortIsReportedAndNotRetriedEveryTick_ButAfterTheInterval()
    {
        int port = FreePort();
        using var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        var settings = new AppSettings { ProxyPort = port, ProxyEnabled = true };
        var now = DateTime.Now;
        using var pm = new ProxyManager(() => Array.Empty<ServerWatcher>(), settings, _ => { }, () => now);
        for (int i = 0; i < 10; i++) { now = now.AddSeconds(1); pm.Maintain(); }   // zehn Sekunden-Takte
        Assert.AreEqual(1, pm.StartAttempts, "retried every tick");
        Assert.IsNotNull(pm.FailureFor("stykker", out int secs));
        Assert.IsTrue(secs is > 0 and <= 30);
        now = now.AddSeconds(25); pm.Maintain();
        Assert.AreEqual(2, pm.StartAttempts);   // nach der Wartezeit ein neuer Versuch
        blocker.Stop();
        now = now.AddSeconds(31); pm.Maintain();
        Assert.IsTrue(pm.Running);
        Assert.IsNull(pm.FailureFor("stykker", out _));
    }

    [TestMethod]
    public void ToggleTurnsTheProxyOnAndOffAndPersists()
    {
        var settings = new AppSettings { ProxyPort = FreePort() };
        using var pm = new ProxyManager(() => Array.Empty<ServerWatcher>(), settings, _ => { });
        Assert.IsTrue(pm.Toggle(out var error), error);
        Assert.IsTrue(pm.Running);
        Assert.IsTrue(settings.ProxyEnabled);
        Assert.IsFalse(pm.Toggle(out _));
        Assert.IsFalse(pm.Running);
        Assert.IsFalse(settings.ProxyEnabled);
    }
}

[TestClass]
public class LaunchRobustnessTests
{
    private static readonly string Cmd = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
    private static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "slm-r-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    [TestMethod]
    public void StartTimeout_MarksLaunchFailedWithReason_ProcessKeepsRunning()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = TempDir();
        using var reg = new ServerRegistry(new FakePlatform(), new HttpClient(new FakeHandler())) { StartTimeout = TimeSpan.FromMinutes(10) };
        var ls = reg.Launch(new LaunchPlan("t", Cmd, new[] { "/c", "ping", "-n", "30", "127.0.0.1" }, null, 8099, "127.0.0.1", null, Path.Combine(dir, "t.log"), null));
        try
        {
            reg.Maintain(DateTime.Now);
            Assert.AreEqual(LaunchState.Starting, ls.State);
            reg.Maintain(DateTime.Now.AddMinutes(11));
            Assert.AreEqual(LaunchState.Failed, ls.State);
            Assert.IsTrue(ls.StillRunning);
            StringAssert.Contains(ls.FailureReason, "10 min");
            Assert.IsFalse(ls.Proc!.HasExited);   // nicht ungefragt beendet
        }
        finally { try { ls.Proc?.Kill(); } catch { } try { Directory.Delete(dir, true); } catch { } }
    }

    [TestMethod]
    public async Task EarlyCrash_ShowsConsoleOutputWhenThereIsNoLog()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = TempDir();
        try
        {
            var plan = new LaunchPlan("t", Cmd, new[] { "/c", "echo boom: unknown argument & exit 2" }, null, 9, "127.0.0.1", null, Path.Combine(dir, "never.log"), null);
            using var ls = ServerLauncher.Start(plan);
            var end = DateTime.Now.AddSeconds(10);
            while (ls.State != LaunchState.Failed && DateTime.Now < end) await Task.Delay(50);
            Assert.AreEqual(LaunchState.Failed, ls.State);
            Assert.AreEqual(2, ls.ExitCode);
            Assert.IsTrue(ls.LogTail.Any(l => l.Contains("boom")), string.Join("|", ls.LogTail));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void OutputRing_IsBounded()
    {
        var r = new OutputRing(maxLines: 5, maxLineLength: 10);
        for (int i = 0; i < 100; i++) r.Add("line " + i + " with a very long tail");
        r.Add(null);
        Assert.AreEqual(5, r.Count);
        var t = r.Tail(3);
        Assert.AreEqual(3, t.Length);
        Assert.IsTrue(t.All(l => l.Length <= 11));
        StringAssert.StartsWith(t[^1], "line 99");
    }

    [TestMethod]
    public void EnvSecretsAreFoundAndFingerprintReactsToChanges()
    {
        var env = new Dictionary<string, string> { ["LLAMA_ARG_API_KEY"] = "***", ["CUDA_VISIBLE_DEVICES"] = "0" };
        var slots = ServerLauncher.FindSecretSlots(new[] { "-m", "x" }, env);
        Assert.AreEqual(1, slots.Count);
        Assert.AreEqual("LLAMA_ARG_API_KEY", slots[0].EnvName);
        var applied = ServerLauncher.ApplyEnvSecrets(env, new Dictionary<string, string> { ["LLAMA_ARG_API_KEY"] = "K" });
        Assert.AreEqual("K", applied["LLAMA_ARG_API_KEY"]);
        Assert.AreEqual("0", applied["CUDA_VISIBLE_DEVICES"]);

        var a = new LaunchSpec("n", "C:\\p.exe", new[] { "-m", "x" }, "C:\\w", null, null, env);
        var fp = ServerLauncher.Fingerprint(a);
        Assert.AreEqual(fp, ServerLauncher.Fingerprint(a with { Name = "other name" }));   // der Name gehört nicht dazu
        Assert.AreNotEqual(fp, ServerLauncher.Fingerprint(a with { Args = new[] { "-m", "y" } }));
        Assert.AreNotEqual(fp, ServerLauncher.Fingerprint(a with { WorkingDir = "C:\\other" }));
        Assert.AreNotEqual(fp, ServerLauncher.Fingerprint(a with { Env = new Dictionary<string, string> { ["CUDA_VISIBLE_DEVICES"] = "1" } }));
        Assert.AreNotEqual(fp, ServerLauncher.Fingerprint(a with { Program = "C:\\q.exe" }));
        var text = ServerLauncher.DescribeCommand(a);
        StringAssert.Contains(text, "C:\\p.exe");
        Assert.IsFalse(text.Contains("LLAMA_ARG_API_KEY=K"));
    }
}

[TestClass]
public class CoordinatorTests
{
    private static readonly string Cmd = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    private sealed class Rig : IDisposable
    {
        public string Dir = Path.Combine(Path.GetTempPath(), "slm-c-" + Guid.NewGuid().ToString("N"));
        public FakePlatform Platform = new();
        public FakePrompt Prompt = new();
        public MonitorEngine Engine;
        public LaunchCoordinator Coordinator;
        public Rig()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "m.gguf"), "x");
            Engine = new MonitorEngine(Platform, new AppPaths(Dir), new AppSettings(), new HttpClient(new FakeHandler()));
            Coordinator = new LaunchCoordinator(Engine, Prompt);
        }

        public string[] Args(int port, params string[] extra) =>
            new[] { "-m", Path.Combine(Dir, "m.gguf"), "--port", port.ToString() }.Concat(extra).Concat(new[] { "/c", "ping", "-n", "3", "127.0.0.1" }).ToArray();

        public Profile Save(string[] args, Dictionary<string, string>? env = null)
        {
            var info = new ServerInfo { Key = "k", Port = 1, Program = Cmd, CommandLineReadable = true, Args = args, Params = LlamaServerArgs.Parse(args), Env = env ?? new Dictionary<string, string>() };
            return Engine.Library.AddProfile(info, "P", DateTime.Now)!;
        }

        // gestarteten Prozess beenden und den Eintrag entfernen, damit derselbe Port wieder frei ist
        public void Reset()
        {
            foreach (var l in Engine.Registry.Launches.ToList()) { try { l.Proc?.Kill(); } catch { } Engine.Registry.DismissLaunch(l.Id); }
        }

        public void Dispose()
        {
            Reset();
            Engine.Dispose();
            try { Directory.Delete(Dir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task CommandLineIsConfirmedOnFirstStartAndAfterChanges_NotInBetween()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var rig = new Rig();
        var p = rig.Save(rig.Args(18123));
        Assert.IsTrue(await rig.Coordinator.StartProfileAsync(p));
        Assert.AreEqual(1, rig.Prompt.Confirms.Count);
        StringAssert.Contains(rig.Prompt.Confirms[0], "--port");
        rig.Reset();
        Assert.IsTrue(await rig.Coordinator.StartProfileAsync(p));   // unverändert: keine Rückfrage mehr
        Assert.AreEqual(1, rig.Prompt.Confirms.Count);
        rig.Reset();

        // geändert (Argumente bearbeitet): wieder fragen; bei "Nein" wird nichts gestartet
        rig.Engine.Library.UpdateProfile(p.Id, "P", null, Cmd, rig.Args(18123, "--alias", "changed"), null);
        rig.Prompt.ConfirmAnswer = false;
        Assert.IsFalse(await rig.Coordinator.StartProfileAsync(rig.Engine.Library.GetProfile(p.Id)!));
        Assert.AreEqual(2, rig.Prompt.Confirms.Count);
        Assert.AreEqual(0, rig.Engine.Registry.Launches.Count);
        rig.Prompt.ConfirmAnswer = true;
        Assert.IsTrue(await rig.Coordinator.StartProfileAsync(rig.Engine.Library.GetProfile(p.Id)!));
        Assert.AreEqual(3, rig.Prompt.Confirms.Count);
    }

    [TestMethod]
    public async Task RedactedEnvironmentValueIsAskedAtStart_NeverStored()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var rig = new Rig();
        var p = rig.Save(rig.Args(18124), new Dictionary<string, string> { ["LLAMA_ARG_API_KEY"] = "***" });
        Assert.IsTrue(await rig.Coordinator.StartProfileAsync(p));
        Assert.AreEqual(1, rig.Prompt.Secrets.Count);
        Assert.AreEqual("***", rig.Engine.Library.GetProfile(p.Id)!.Env["LLAMA_ARG_API_KEY"]);
        rig.Engine.Library.Save();
        Assert.IsFalse(File.ReadAllText(rig.Engine.Paths.LibraryFile).Contains("s3cret"));
        rig.Reset();
        rig.Prompt.SecretAnswer = null;   // abgebrochen: kein Start
        Assert.IsFalse(await rig.Coordinator.StartProfileAsync(p));
    }

    [TestMethod]
    public async Task RelativeProgramIsRefusedWithAMessage()
    {
        using var rig = new Rig();
        var spec = new LaunchSpec("n", "llama-server.exe", new[] { "-m", Path.Combine(rig.Dir, "m.gguf"), "--port", "18125" }, null);
        Assert.IsFalse(await rig.Coordinator.StartSpecAsync(spec));
        Assert.AreEqual(1, rig.Prompt.Infos.Count);
        StringAssert.Contains(rig.Prompt.Infos[0], "full path");
        Assert.AreEqual(0, rig.Engine.Registry.Launches.Count);
        Assert.AreEqual(0, rig.Prompt.Confirms.Count);   // fehlerhafte Angaben werden nicht erst zur Bestätigung vorgelegt
    }

    [TestMethod]
    public async Task Stop_AsksOnce_ThenTerminatesWithTheDetectedStartTime()
    {
        using var rig = new Rig();
        rig.Platform.AddServer(30, "C:\\llama\\llama-server.exe", "llama-server -m a.gguf --port 18126", 18126, "127.0.0.1", start: 777);
        await rig.Engine.TickAsync();
        var w = rig.Engine.Servers.Single();
        await rig.Coordinator.StopServerAsync(w);
        Assert.AreEqual(1, rig.Prompt.Confirms.Count);
        CollectionAssert.AreEqual(new[] { (30, 777L) }, rig.Platform.Terminated);
    }

    [TestMethod]
    public async Task Stop_WithUnreadableStartTime_NeedsASecondConfirmation()
    {
        using var rig = new Rig();
        rig.Platform.AddServer(31, "C:\\llama\\llama-server.exe", "llama-server -m a.gguf --port 18127", 18127, "127.0.0.1", start: 0);
        await rig.Engine.TickAsync();
        rig.Prompt.ConfirmAnswer = true;
        await rig.Coordinator.StopServerAsync(rig.Engine.Servers.Single());
        Assert.AreEqual(2, rig.Prompt.Confirms.Count);
        StringAssert.Contains(rig.Prompt.Confirms[1], "could have been reused");
        Assert.AreEqual(1, rig.Platform.Terminated.Count);
    }

    [TestMethod]
    public async Task Stop_ProcessChangedMeanwhile_TerminatesNothing()
    {
        using var rig = new Rig();
        rig.Platform.AddServer(32, "C:\\llama\\llama-server.exe", "llama-server -m a.gguf --port 18128", 18128, "127.0.0.1", start: 5);
        await rig.Engine.TickAsync();
        var w = rig.Engine.Servers.Single();
        rig.Platform.Processes[32] = rig.Platform.Processes[32] with { StartTicks = 6 };   // PID wurde wiederverwendet
        await rig.Coordinator.StopServerAsync(w);
        Assert.AreEqual(0, rig.Platform.Terminated.Count);
        Assert.AreEqual(1, rig.Prompt.Infos.Count);
    }
}

[TestClass]
public class EngineTests
{
    private static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "slm-e-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    [TestMethod]
    public async Task Tick_DetectsServer_FillsLibrary_AndDoesNotRunTwiceAtOnce()
    {
        var dir = TempDir();
        var p = new FakePlatform();
        p.AddServer(30, "C:\\llama\\llama-server.exe", Samples.BonsaiCmd, 8081, "127.0.0.1");
        using var engine = new MonitorEngine(p, new AppPaths(dir), new AppSettings(), new HttpClient(new FakeHandler()));
        try
        {
            await Task.WhenAll(engine.TickAsync(), engine.TickAsync());   // der zweite Aufruf wird übersprungen
            Assert.AreEqual(1, engine.Ticks);
            Assert.AreEqual(1, engine.Servers.Count);
            await engine.TickAsync();
            Assert.AreEqual(1, engine.Library.History.Count);
            Assert.IsNull(engine.LastError);
        }
        finally { engine.Dispose(); try { Directory.Delete(dir, true); } catch { } }
    }

    [TestMethod]
    public async Task OversizedRecording_IsStoppedAndSavedInTheBackground_WithNotice()
    {
        var dir = TempDir();
        var p = new FakePlatform();
        p.AddServer(30, "C:\\llama\\llama-server.exe", "llama-server -m a.gguf --port 18130", 18130, "127.0.0.1");
        var notices = new List<string>();
        using var engine = new MonitorEngine(p, new AppPaths(dir), new AppSettings(), new HttpClient(new FakeHandler()));
        engine.Notice += n => { lock (notices) notices.Add(n); };
        try
        {
            await engine.TickAsync();
            var session = engine.StartRecording(engine.Servers[0], out var problem);
            Assert.IsNotNull(session, problem);
            engine.Recorder.MaxSessionBytes = 1;   // schon die Kopfzeile ist größer
            await engine.TickAsync();
            var end = DateTime.Now.AddSeconds(5);
            while (DateTime.Now < end) { lock (notices) if (notices.Count > 0) break; await Task.Delay(25); }
            Assert.IsFalse(engine.Recorder.Active);   // beendet und gespeichert
            lock (notices) Assert.IsTrue(notices.Any(n => n.Contains("size limit")), string.Join("|", notices));
            Assert.AreEqual(1, (await RecordingStore.ListAsync(engine.Paths.RecordingsDir)).Count);
        }
        finally { engine.Dispose(); try { Directory.Delete(dir, true); } catch { } }
    }

    [TestMethod]
    public async Task GlobalRecordingAbsorbsSingleOnes_AndSingleIsRefusedWhileGlobalRuns()
    {
        var dir = TempDir();
        var p = new FakePlatform();
        p.AddServer(30, "C:\\llama\\llama-server.exe", "llama-server -m a.gguf --port 18131", 18131, "127.0.0.1");
        using var engine = new MonitorEngine(p, new AppPaths(dir), new AppSettings(), new HttpClient(new FakeHandler()));
        try
        {
            await engine.TickAsync();
            Assert.IsNotNull(engine.StartRecording(engine.Servers[0], out _));
            var global = await engine.StartRecordingAllAsync();
            Assert.IsNotNull(global);
            Assert.AreEqual(1, engine.Recorder.Sessions.Count);   // die einzelne wurde beendet und gespeichert
            Assert.IsNull(engine.StartRecording(engine.Servers[0], out var problem));
            Assert.IsNotNull(problem);
            await engine.StopRecordingAsync(global!);
            Assert.AreEqual(2, (await RecordingStore.ListAsync(engine.Paths.RecordingsDir)).Count);
        }
        finally { engine.Dispose(); try { Directory.Delete(dir, true); } catch { } }
    }
}
