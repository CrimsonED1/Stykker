using System.Diagnostics;
using System.Net;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class LauncherTests
{
    private static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "slm-l-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    [TestMethod]
    public void SecretSlotsAreFoundAndApplied()
    {
        var args = new[] { "-m", "x.gguf", "--api-key", "***", "--hf-token=***", "--api-key-file", "***", "--port", "1" };
        var slots = ServerLauncher.FindSecretSlots(args);
        Assert.AreEqual(3, slots.Count);
        var applied = ServerLauncher.ApplySecrets(args, new Dictionary<int, string> { [3] = "KEY", [4] = "TOK", [6] = "KEY2" });
        CollectionAssert.AreEqual(new[] { "-m", "x.gguf", "--api-key", "KEY", "--hf-token=TOK", "--api-key", "KEY2", "--port", "1" }, applied);
        Assert.AreEqual(0, ServerLauncher.FindSecretSlots(applied).Count);
    }

    [TestMethod]
    public void BuildPlan_AddsLogFilePerProfileAndPort()
    {
        var dir = TempDir();
        try
        {
            var plan = ServerLauncher.BuildPlan(new LaunchSpec("My Model/1", "p.exe", new[] { "-m", "m.gguf", "--port", "8123" }, null), dir);
            Assert.AreEqual(8123, plan.Port);
            Assert.AreEqual("127.0.0.1", plan.Host);
            Assert.AreEqual(Path.Combine(dir, "My_Model_1-8123.log"), plan.LogFile);
            CollectionAssert.AreEqual(new[] { "-m", "m.gguf", "--port", "8123", "--log-file", plan.LogFile }, plan.Args.ToArray());
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void BuildPlan_KeepsExistingLogFileAndDefaultsPort()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows path semantics (drive letters, Windows folder)");
        var plan = ServerLauncher.BuildPlan(new LaunchSpec("n", "p.exe", new[] { "-m", "m.gguf", "--log-file", "my.log", "--host", "0.0.0.0" }, "C:\\work"), Path.GetTempPath());
        Assert.AreEqual(8080, plan.Port);
        Assert.AreEqual("127.0.0.1", plan.Host);   // 0.0.0.0 ist nicht anrufbar
        Assert.AreEqual(Path.GetFullPath("C:\\work\\my.log"), plan.LogFile);
        Assert.AreEqual(6, plan.Args.Count);
    }

    [TestMethod]
    public void PruneLogs_RemovesOldestUntilUnderLimit_KeepsActive()
    {
        var dir = TempDir();
        try
        {
            for (int i = 0; i < 4; i++)
            {
                var f = Path.Combine(dir, $"l{i}.log");
                File.WriteAllBytes(f, new byte[1000]);
                File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-10 + i));
            }
            ServerLauncher.PruneLogs(dir, 2500, Path.Combine(dir, "l0.log"));
            var left = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(x => x).ToArray();
            CollectionAssert.AreEqual(new[] { "l0.log", "l3.log" }, left);   // l1 und l2 (älteste nach l0) sind weg, l0 ist die aktive Datei
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Check_ReportsMissingFilesAndBusyPort()
    {
        var p = new FakePlatform();
        p.Listeners.Add(new ListenerInfo("0.0.0.0", 8090, 55));
        var dir = TempDir();
        try
        {
            var model = Path.Combine(dir, "m.gguf");
            var issues = ServerLauncher.Check(new LaunchSpec("n", Path.Combine(dir, "nope.exe"), new[] { "-m", model, "--port", "8090" }, null), p, null, Array.Empty<ServerInfo>());
            var codes = issues.Where(i => i.Severity == LaunchSeverity.Error).Select(i => i.Code).OrderBy(c => c).ToArray();
            CollectionAssert.AreEqual(new[] { "model", "port", "program" }, codes);
            Assert.IsTrue(issues.Single(i => i.Code == "port").Message.Contains("55"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Check_SecretAndRouterAreErrors_SameModelAndVramAreWarnings()
    {
        var dir = TempDir();
        try
        {
            var exe = Path.Combine(dir, "llama-server.exe"); File.WriteAllText(exe, "x");
            var model = Path.Combine(dir, "m.gguf"); File.WriteAllText(model, "x");
            var p = new FakePlatform();
            var running = new ServerInfo { Port = 8000, Params = LlamaServerArgs.Parse(new[] { "-m", model }), Pid = 3 };
            var gpu = new GpuSample("g", 0, 15, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0);   // 1 GB frei
            var issues = ServerLauncher.Check(new LaunchSpec("n", exe, new[] { "-m", model, "--port", "8001", "--api-key", "***" }, null, null, 4.0), p, gpu, new[] { running });
            Assert.IsTrue(issues.Any(i => i.Code == "secret" && i.Severity == LaunchSeverity.Error));
            Assert.IsTrue(issues.Any(i => i.Code == "same-model" && i.Severity == LaunchSeverity.Warning));
            var vram = issues.Single(i => i.Code == "vram");
            Assert.AreEqual(LaunchSeverity.Warning, vram.Severity);
            Assert.IsTrue(vram.Message.Contains("measured"));
            var router = ServerLauncher.Check(new LaunchSpec("n", exe, new[] { "--models-dir", dir }, null), p, null, Array.Empty<ServerInfo>());
            Assert.IsTrue(router.Any(i => i.Code == "mode"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Check_CleanSpecHasNoIssues()
    {
        var dir = TempDir();
        try
        {
            var exe = Path.Combine(dir, "llama-server.exe"); File.WriteAllText(exe, "x");
            var model = Path.Combine(dir, "m.gguf"); File.WriteAllText(model, "x");
            Assert.AreEqual(0, ServerLauncher.Check(new LaunchSpec("n", exe, new[] { "-m", model, "--port", "8001" }, dir), new FakePlatform(), null, Array.Empty<ServerInfo>()).Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static GgufInfo GgufForRam(long size) => new()
    {
        Architecture = "a", FileSize = size,
        Meta = new Dictionary<string, object>
        {
            ["a.block_count"] = 32L, ["a.embedding_length"] = 4096L,
            ["a.attention.head_count"] = 32L, ["a.attention.head_count_kv"] = 8L,
        },
    };

    [TestMethod]
    public void RamWarning_FiresWhenCpuSideDoesNotFit()
    {
        var parsed = LlamaServerArgs.Parse(new[] { "-m", "m.gguf", "-c", "4096", "-ngl", "0" });
        var sys = new SystemSample(0, 8, 30.0, 32.0, 0, 0);   // nur 2 GB frei
        var msg = ServerLauncher.RamWarning(GgufForRam(8L << 30), parsed, sys);
        Assert.IsNotNull(msg);
        StringAssert.Contains(msg, "RAM");
    }

    [TestMethod]
    public void RamWarning_SilentWhenEverythingFitsOrDataIsMissing()
    {
        var parsed = LlamaServerArgs.Parse(new[] { "-m", "m.gguf", "-c", "4096", "-ngl", "99" });
        var sys = new SystemSample(0, 8, 10.0, 32.0, 0, 0);
        Assert.IsNull(ServerLauncher.RamWarning(GgufForRam(4L << 30), parsed, sys));   // alles auf der GPU
        Assert.IsNull(ServerLauncher.RamWarning(null, parsed, sys));                    // keine GGUF-Metadaten
        Assert.IsNull(ServerLauncher.RamWarning(GgufForRam(4L << 30), parsed, null));   // keine Systemwerte
    }

    [TestMethod]
    public void LogTail_ReturnsLastLines()
    {
        var dir = TempDir();
        try
        {
            var f = Path.Combine(dir, "a.log");
            File.WriteAllLines(f, Enumerable.Range(1, 100).Select(i => "line " + i));
            var t = LogTailReader.Tail(f, 3);
            CollectionAssert.AreEqual(new[] { "line 98", "line 99", "line 100" }, t);
            Assert.AreEqual(0, LogTailReader.Tail(Path.Combine(dir, "none.log"), 3).Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    // Start eines echten Prozesses: ein schnell endendes Programm muss als "Failed" mit Exit-Code erkannt werden
    [TestMethod]
    public async Task Start_DetectsEarlyExitWithCodeAndLogTail()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = TempDir();
        try
        {
            var log = Path.Combine(dir, "x.log");
            File.WriteAllText(log, "boot\nfailed to load model\n");
            var plan = new LaunchPlan("t", Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", new[] { "/c", "exit", "3" }, null, 9, "127.0.0.1", null, log, null);
            using var ls = ServerLauncher.Start(plan);
            var end = DateTime.Now.AddSeconds(10);
            while (ls.State != LaunchState.Failed && DateTime.Now < end) await Task.Delay(50);
            Assert.AreEqual(LaunchState.Failed, ls.State);
            Assert.AreEqual(3, ls.ExitCode);
            CollectionAssert.AreEqual(new[] { "boot", "failed to load model" }, ls.LogTail.ToArray());
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Library_UpdateProfile_RederivesPortAndRedacts()
    {
        var lib = new Library();
        var info = new ServerInfo { Key = "k", Port = 1, Program = "p.exe", CommandLineReadable = true, Args = new[] { "-m", "a.gguf" }, Params = LlamaServerArgs.Parse(new[] { "-m", "a.gguf" }) };
        var pr = lib.AddProfile(info, "A", DateTime.Now)!;
        Assert.IsTrue(lib.UpdateProfile(pr.Id, " B ", "note", "q.exe", new[] { "-m", "b.gguf", "--port", "9", "--api-key", "SECRET" }, "C:\\w"));
        var e = lib.GetProfile(pr.Id)!;
        Assert.AreEqual("B", e.Name);
        Assert.AreEqual("q.exe", e.Program);
        Assert.AreEqual(9, e.Port);
        Assert.AreEqual("b.gguf", e.ModelPath);
        Assert.IsTrue(e.HasSecrets);
        CollectionAssert.DoesNotContain(e.Args, "SECRET");
        Assert.IsFalse(lib.UpdateProfile(pr.Id, "", null, "q.exe", Array.Empty<string>(), null));
    }

    [TestMethod]
    public void Stop_ChangedProcessStartTicks_ReturnsProcessChanged()
    {
        var p = new FakePlatform();
        p.Processes[100] = new ProcessDetails(100, 9999, 1, "C:\\llama\\llama-server.exe", null, null, new Dictionary<string, string>());
        // Erwartete Startzeit war 5555, tatsächliche Startzeit ist 9999
        var res = ServerLauncher.Stop(p, 100, 5555, out var err);
        Assert.AreEqual(StopOutcome.ProcessChanged, res);
        Assert.IsNull(err);
        Assert.AreEqual(0, p.Terminated.Count);
    }

    [TestMethod]
    public void Stop_ZeroStartTicks_NeedsConfirmationBeforeTerminating()
    {
        var p = new FakePlatform();
        p.Processes[100] = new ProcessDetails(100, 0, 1, "C:\\llama\\llama-server.exe", null, null, new Dictionary<string, string>());
        // startTicks == 0 (nicht lesbar): ohne Bestätigung (allowUnverified) wird nicht beendet
        var res = ServerLauncher.Stop(p, 100, 0, out var err);
        Assert.AreEqual(StopOutcome.NeedsConfirmation, res);
        Assert.AreEqual(0, p.Terminated.Count);

        // Mit Bestätigung: wird beendet
        res = ServerLauncher.Stop(p, 100, 0, out err, allowUnverified: true);
        Assert.AreEqual(StopOutcome.Stopped, res);
        Assert.AreEqual(1, p.Terminated.Count);
    }

    [TestMethod]
    public void Check_RelativeProgramPathIsRejected()
    {
        var p = new FakePlatform();
        var spec = new LaunchSpec("test", "llama-server.exe", new[] { "-m", "C:\\models\\m.gguf", "--port", "8001" }, "C:\\llama");
        var issues = ServerLauncher.Check(spec, p, null, Array.Empty<ServerInfo>());
        var progIssue = issues.Single(i => i.Code == "program");
        Assert.AreEqual(LaunchSeverity.Error, progIssue.Severity);
        Assert.IsTrue(progIssue.Message.Contains("full path"), progIssue.Message);
    }

    [TestMethod]
    public void Start_RelativeProgramPathThrows()
    {
        var plan = new LaunchPlan("test", "llama-server.exe", new[] { "-m", "m.gguf" }, null, 8080, "127.0.0.1", null, "test.log", null);
        var ex = Assert.ThrowsException<InvalidOperationException>(() => ServerLauncher.Start(plan));
        Assert.IsTrue(ex.Message.Contains("full path"), ex.Message);
    }
}

[TestClass]
public class ApiKeyTests
{
    private const string Llama = "C:\\llama\\llama-server.exe";

    [TestMethod]
    public void ExtractApiKey_FromAllSources()
    {
        ProcessDetails D(string cmd, Dictionary<string, string>? env = null) =>
            new(1, 1, 0, Llama, cmd, null, env ?? new Dictionary<string, string>());
        Assert.AreEqual("k1", ServerDiscovery.ExtractApiKey(D(Llama + " --api-key k1"))!.Reveal());
        Assert.AreEqual("k2", ServerDiscovery.ExtractApiKey(D(Llama + " --api-key=k2,other"))!.Reveal());
        Assert.AreEqual("k3", ServerDiscovery.ExtractApiKey(D(Llama + " -m x", new Dictionary<string, string> { ["LLAMA_ARG_API_KEY"] = "k3" }))!.Reveal());
        Assert.IsNull(ServerDiscovery.ExtractApiKey(D(Llama + " -m x")));
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "\nfilekey\nsecond\n");
            Assert.AreEqual("filekey", ServerDiscovery.ExtractApiKey(D(Llama + " --api-key-file \"" + file + "\""))!.Reveal());
        }
        finally { File.Delete(file); }
        Assert.AreEqual("***", new SecretValue("zzz").ToString());
    }

    [TestMethod]
    public async Task Discovery_KeepsKeyInMemoryOnly_NotInInfoText()
    {
        var p = new FakePlatform();
        p.AddServer(30, Llama, Llama + " -m m.gguf --port 8095 --api-key TOPSECRET", 8095);
        var d = new ServerDiscovery(p, null, new HttpClient(new FakeHandler()), selfPid: 1);
        var s = (await d.RunAsync()).Servers.Single();
        Assert.AreEqual("TOPSECRET", s.ApiKey!.Reveal());
        Assert.IsFalse((s.CommandLine + string.Join(' ', s.Args)).Contains("TOPSECRET"));
        Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(s.Args).Contains("TOPSECRET"));
    }

    [TestMethod]
    public async Task Watcher_SendsBearerHeaderWithKey()
    {
        var p = new FakePlatform();
        var h = new FakeHandler();
        h.Routes["127.0.0.1:8095/slots"] = (HttpStatusCode.OK, "[]");
        h.Routes["127.0.0.1:8095/props"] = (HttpStatusCode.OK, Samples.PropsJson);
        h.Routes["127.0.0.1:8095/v1/models"] = (HttpStatusCode.OK, "{\"data\":[{\"id\":\"m\"}]}");
        p.AddServer(30, Llama, Llama + " -m m.gguf --port 8095 --api-key TOPSECRET", 8095, "127.0.0.1");
        var reg = new ServerRegistry(p, new HttpClient(h));
        await reg.RefreshNowAsync();
        await reg.Servers[0].PollAsync();
        Assert.IsTrue(h.Requests.Count >= 2);
        Assert.IsTrue(h.AuthHeaders.All(a => a == "Bearer TOPSECRET"), string.Join(",", h.AuthHeaders));
        Assert.IsTrue(reg.Servers[0].Online);
        reg.Dispose();
    }
}

[TestClass]
public class RegistryLaunchTests
{
    [TestMethod]
    public async Task LaunchEntryDisappearsWhenServerListensWithSamePid()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = new FakePlatform();
        var reg = new ServerRegistry(p, new HttpClient(new FakeHandler()));
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var log = Path.Combine(Path.GetTempPath(), "slm-launch-" + Guid.NewGuid().ToString("N") + ".log");
        var ls = reg.Launch(new LaunchPlan("t", cmd, new[] { "/c", "ping", "-n", "6", "127.0.0.1" }, null, 8099, "127.0.0.1", null, log, null));
        try
        {
            Assert.AreEqual(1, reg.Launches.Count);
            Assert.AreEqual(LaunchState.Starting, ls.State);
            // dasselbe Programm "lauscht" jetzt: die Erkennung findet es per Name oder Probe nicht, deshalb Programmname llama-server vortäuschen
            p.Processes[ls.Pid] = new ProcessDetails(ls.Pid, 1, 0, "C:\\x\\llama-server.exe", "llama-server -m a.gguf --port 8099", null, new Dictionary<string, string>());
            p.Listeners.Add(new ListenerInfo("127.0.0.1", 8099, ls.Pid));
            await reg.RefreshNowAsync();
            Assert.AreEqual(0, reg.Launches.Count);
            Assert.AreEqual(1, reg.Servers.Count);
        }
        finally { reg.Dispose(); try { Process.GetProcessById(ls.Pid).Kill(); } catch { } }
    }
}
