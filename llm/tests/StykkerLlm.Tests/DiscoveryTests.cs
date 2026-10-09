using System.Net;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class DiscoveryTests
{
    private const string Llama = "C:\\ai\\bonsai\\llama\\llama-server.exe";

    private static (FakePlatform P, FakeHandler H, ServerDiscovery D) Make()
    {
        var p = new FakePlatform();
        var h = new FakeHandler();
        var d = new ServerDiscovery(p, null, new HttpClient(h), selfPid: 1);
        return (p, h, d);
    }

    [TestMethod]
    public async Task DetectsByProgramName_ParsesEverything_NoProbe()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows path semantics (drive letters, Windows folder)");
        var (p, h, d) = Make();
        p.AddServer(30, Llama, Samples.BonsaiCmd, 8081, "127.0.0.1", cwd: "C:\\work");
        var r = await d.RunAsync();
        var s = r.Servers.Single();
        Assert.AreEqual("127.0.0.1:8081", s.Key);
        Assert.AreEqual("http://127.0.0.1:8081", s.Url);
        Assert.AreEqual("name", s.DetectedBy);
        Assert.AreEqual("bonsai-27b-1bit", s.Name);
        Assert.AreEqual(65536, s.Params!.Ctx);
        Assert.AreEqual(Llama, s.Program);
        Assert.AreEqual("C:\\work", s.WorkingDir);
        Assert.AreEqual("C:\\ai\\bonsai\\server-1bit.log", s.LogFile);
        Assert.IsTrue(s.CanSave);
        Assert.IsFalse(s.HasSecrets);
        Assert.AreEqual(0, h.Requests.Count);   // Name genügt, keine Probe
        Assert.AreEqual(1, r.Net.Listeners.Count);
    }

    [TestMethod]
    public async Task SecretsAreRedactedEverywhere()
    {
        var (p, _, d) = Make();
        var env = new Dictionary<string, string> { ["LLAMA_ARG_API_KEY"] = "ENVSECRET", ["PATH"] = "x" };
        p.AddServer(30, Llama, "\"" + Llama + "\" -m m.gguf --port 8090 --api-key TOPSECRET --hf-token=HFSECRET", 8090, env: env);
        var s = (await d.RunAsync()).Servers.Single();
        Assert.IsTrue(s.HasSecrets);
        var all = s.CommandLine + " " + string.Join(' ', s.Args) + " " + string.Join(' ', s.Env.Values);
        Assert.IsFalse(all.Contains("TOPSECRET") || all.Contains("HFSECRET") || all.Contains("ENVSECRET"), all);
        Assert.IsTrue(s.CommandLine!.Contains("--api-key ***"));
        Assert.AreEqual("***", s.Env["LLAMA_ARG_API_KEY"]);
        Assert.AreEqual(8090, s.Params!.Port);
    }

    [TestMethod]
    public async Task UnknownProgramIsDetectedByProps_AndCached()
    {
        var (p, h, d) = Make();
        p.AddServer(40, "C:\\tools\\myserver.exe", "myserver.exe -m x.gguf", 7000);
        h.Routes["127.0.0.1:7000/props"] = (HttpStatusCode.OK, Samples.PropsJson);
        var r1 = await d.RunAsync();
        Assert.AreEqual(1, r1.Servers.Count);
        Assert.AreEqual("probe", r1.Servers[0].DetectedBy);
        Assert.AreEqual("x", r1.Servers[0].Name);           // Alias aus /props, Befehlszeile hat keinen
        Assert.AreEqual(1, h.Requests.Count);
        await d.RunAsync(); await d.RunAsync();
        Assert.AreEqual(1, h.Requests.Count);               // Ergebnis je PID + Startzeit gemerkt
        Assert.AreEqual(1, d.ProbesSent);
    }

    [TestMethod]
    public async Task NonLlamaListenerIsProbedOnceAndRemembered()
    {
        var (p, h, d) = Make();
        p.AddServer(41, "C:\\apps\\web.exe", "web.exe", 5000);
        h.Routes["127.0.0.1:5000/props"] = (HttpStatusCode.OK, "{\"hello\":1}");
        Assert.AreEqual(0, (await d.RunAsync()).Servers.Count);
        Assert.AreEqual(BackendProbes.Default().Count, h.Requests.Count);   // je ein Versuch je Probe (llama.cpp, Ollama, LM Studio, vLLM)
        Assert.AreEqual(0, (await d.RunAsync()).Servers.Count);
        Assert.AreEqual(BackendProbes.Default().Count, h.Requests.Count);   // danach gemerkt
        Assert.AreEqual(1, d.ProbesSent);
    }

    [TestMethod]
    public async Task ExcludesPythonDashM_OllamaAndWindowsPrograms()
    {
        var (p, h, d) = Make();
        h.Routes["127.0.0.1:7001/props"] = (HttpStatusCode.OK, Samples.PropsJson);
        h.Routes["127.0.0.1:7002/props"] = (HttpStatusCode.OK, Samples.PropsJson);
        h.Routes["127.0.0.1:7003/props"] = (HttpStatusCode.OK, Samples.PropsJson);
        p.AddServer(50, "C:\\Python\\python.exe", "python.exe -m llama_cpp.server --port 7001", 7001);
        p.AddServer(51, "C:\\Ollama\\ollama.exe", "ollama.exe runner --port 7002", 7002, parent: 52);
        p.Processes[52] = new ProcessDetails(52, 1, 1, "C:\\Ollama\\ollama.exe", "ollama serve", null, new Dictionary<string, string>());
        p.AddServer(53, "C:\\tools\\runner.exe", "runner.exe --port 7003", 7003, parent: 52);   // Kind von ollama
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(winDir))
        {
            h.Routes["127.0.0.1:7004/props"] = (HttpStatusCode.OK, Samples.PropsJson);
            p.AddServer(54, Path.Combine(winDir, "System32", "svchost.exe"), "svchost.exe", 7004);
        }
        var r = await d.RunAsync();
        Assert.AreEqual(0, r.Servers.Count);
        Assert.AreEqual(0, h.Requests.Count);
    }

    // Ein Server in WSL2 oder Docker ist auf der Windows-Seite nur über den Port-Relay erreichbar,
    // und der Relay ist ein Programm aus dem Windows-Ordner. Nur die Relay-Namen kommen an die Probes –
    // svchost.exe und die anderen Systemprogramme bleiben weiterhin unangetastet.
    [TestMethod]
    public async Task WslRelayListenerIsProbed_OtherWindowsProgramsStayExcluded()
    {
        var (p, h, d) = Make();
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.IsFalse(string.IsNullOrEmpty(winDir), "ohne Windows-Ordner lässt sich der Fall nicht nachstellen");

        h.Routes["127.0.0.1:7010/props"] = (HttpStatusCode.OK, Samples.PropsJson);
        h.Routes["127.0.0.1:7011/props"] = (HttpStatusCode.OK, Samples.PropsJson);
        h.Routes["127.0.0.1:7012/props"] = (HttpStatusCode.OK, Samples.PropsJson);
        p.AddServer(60, Path.Combine(winDir, "System32", "wslrelay.exe"), "wslrelay.exe --skip-stdin", 7010);
        p.AddServer(61, Path.Combine(winDir, "System32", "wslhost.exe"), "wslhost.exe", 7011);
        p.AddServer(62, Path.Combine(winDir, "System32", "svchost.exe"), "svchost.exe", 7012);

        var r = await d.RunAsync();
        Assert.AreEqual(2, r.Servers.Count, "die beiden Relays antworten, svchost nicht");
        CollectionAssert.AreEquivalent(new[] { 7010, 7011 }, r.Servers.Select(s => s.Port).ToList());
        Assert.IsTrue(r.Servers.All(s => s.Backend == BackendKind.LlamaCpp));
        Assert.IsFalse(h.Requests.Any(k => k.Contains(":7012")), "svchost wird nicht angefasst");
    }

    // Live gefunden (2026-10-05): „python -m modul" ist ein Dienst und bleibt außen vor, „python … -m datei.gguf"
    // ist ein Server, den der Monitor selbst gestartet hat – der muss geprüft werden, sonst bleibt der Startvorgang
    // für immer auf „starting", obwohl der Prozess läuft und antwortet.
    [TestMethod]
    public void PythonIsProbedWhenDashMIsAModelFile_NotWhenItIsAModule()
    {
        static ServerDiscovery.ProcEntry Py(string cmdline) => new()
        {
            ExeName = "python", Deep = true,
            Details = new ProcessDetails(1, 1, 0, @"C:\Python\python.exe", cmdline, null, new Dictionary<string, string>()),
        };

        Assert.IsFalse(ServerDiscovery.IsExcludedFromProbe(Py(@"C:\Python\python.exe server.py -m C:\models\a.gguf --port 8081")),
            "Modelldatei hinter -m: wird geprüft (das war der Fehler)");
        Assert.IsTrue(ServerDiscovery.IsExcludedFromProbe(Py("python -m strata.server --port 8082")), "Strata als Modul bleibt außen vor");
        Assert.IsTrue(ServerDiscovery.IsExcludedFromProbe(Py("python -m http.server 8000")), "sonstige Module auch");
        Assert.IsFalse(ServerDiscovery.IsExcludedFromProbe(Py("python -m vllm.entrypoints.openai.api_server --model Qwen")),
            "vLLM kommt durch");
        Assert.IsFalse(ServerDiscovery.IsExcludedFromProbe(Py(@"C:\Python\python.exe server.py --port 8081")),
            "ohne -m wird wie bisher geprüft");
    }

    [TestMethod]
    public async Task LoadingServerIsRetriedLater_NotCachedAsNegative()
    {
        var now = DateTime.UtcNow;
        var p = new FakePlatform();
        var h = new FakeHandler();
        var d = new ServerDiscovery(p, null, new HttpClient(h), selfPid: 1, now: () => now) { RetryAfter = TimeSpan.FromSeconds(15) };
        p.AddServer(60, "C:\\x\\renamed.exe", "renamed.exe -m x.gguf", 7100);
        h.Routes["127.0.0.1:7100/props"] = (HttpStatusCode.ServiceUnavailable, "{\"error\":{\"code\":503}}");
        Assert.AreEqual(0, (await d.RunAsync()).Servers.Count);
        Assert.AreEqual(0, (await d.RunAsync()).Servers.Count);   // noch keine 15 s
        Assert.AreEqual(1, h.Requests.Count);
        h.Routes["127.0.0.1:7100/props"] = (HttpStatusCode.OK, Samples.PropsJson);
        now = now.AddSeconds(16);
        Assert.AreEqual(1, (await d.RunAsync()).Servers.Count);
    }

    [TestMethod]
    public async Task UnreadableCommandLine_IsShownButNotSavable()
    {
        var (p, h, d) = Make();
        p.AddServer(70, Llama, null, 8082);
        var s = (await d.RunAsync()).Servers.Single();
        Assert.IsFalse(s.CommandLineReadable);
        Assert.AreEqual(SaveBlock.CommandLineUnreadable, s.SaveBlock);
        Assert.IsFalse(s.CanSave);
        Assert.AreEqual("llama-server :8082", s.Name);
        Assert.IsNull(s.CommandLine);
    }

    [TestMethod]
    public async Task RouterAndHuggingFaceAreDisplayOnly()
    {
        var (p, _, d) = Make();
        p.AddServer(71, Llama, "llama-server --port 8083 --models-dir D:\\models", 8083);
        p.AddServer(72, Llama, "llama-server --port 8084 -hf user/repo:Q4_K_M", 8084);
        var r = await d.RunAsync();
        Assert.AreEqual(SaveBlock.RouterMode, r.Servers.Single(s => s.Port == 8083).SaveBlock);
        var hf = r.Servers.Single(s => s.Port == 8084);
        Assert.AreEqual(SaveBlock.HuggingFace, hf.SaveBlock);
        Assert.AreEqual("user/repo:Q4_K_M", hf.Name);
    }

    [TestMethod]
    public async Task HostNormalisationAndDualStackDedupe()
    {
        var (p, _, d) = Make();
        p.AddServer(80, Llama, "llama-server -m a.gguf", 9001, "0.0.0.0");
        p.Listeners.Add(new ListenerInfo("::", 9001, 80));
        p.AddServer(81, Llama, "llama-server -m b.gguf", 9002, "::");
        p.AddServer(82, Llama, "llama-server -m c.gguf", 9003, "192.168.1.20");
        var r = await d.RunAsync();
        Assert.AreEqual(3, r.Servers.Count);
        Assert.AreEqual("127.0.0.1:9001", r.Servers[0].Key);
        Assert.AreEqual("[::1]:9002", r.Servers[1].Key);
        Assert.AreEqual("http://[::1]:9002", r.Servers[1].Url);
        Assert.AreEqual("192.168.1.20:9003", r.Servers[2].Key);
    }

    [TestMethod]
    public async Task ProcessInfoIsCachedPerPidAndStartTime()
    {
        var (p, _, d) = Make();
        p.AddServer(90, Llama, "llama-server -m a.gguf --port 9100", 9100, start: 5);
        await d.RunAsync(); await d.RunAsync(); await d.RunAsync();
        Assert.AreEqual(1, p.ReadProcessCalls);
        // gleiche PID, neue Startzeit (PID wiederverwendet): neu lesen
        p.Processes[90] = p.Processes[90] with { StartTicks = 6 };
        await d.RunAsync();
        Assert.AreEqual(2, p.ReadProcessCalls);
    }

    [TestMethod]
    public async Task SelfAndSystemPidsAreIgnored()
    {
        var p = new FakePlatform();
        var d = new ServerDiscovery(p, null, new HttpClient(new FakeHandler()), selfPid: 77);
        p.AddServer(77, Llama, "llama-server -m a.gguf", 9200);
        p.Listeners.Add(new ListenerInfo("0.0.0.0", 445, 4));
        Assert.AreEqual(0, (await d.RunAsync()).Servers.Count);
    }

    [TestMethod]
    public void LlamaPropsParsing()
    {
        var p = LlamaProps.Parse("{\"is_sleeping\":true,\"total_slots\":2,\"model_path\":\"m.gguf\",\"default_generation_settings\":{\"n_ctx\":8192}}");
        Assert.IsNotNull(p);
        Assert.IsTrue(p.IsSleeping);
        Assert.AreEqual(2, p.TotalSlots);
        Assert.AreEqual(8192, p.NCtx);
        Assert.IsNull(LlamaProps.Parse("{\"x\":1}"));
        Assert.IsNull(LlamaProps.Parse("[1,2]"));
        Assert.IsNull(LlamaProps.Parse("garbage"));
    }
}

[TestClass]
public class RegistryTests
{
    private const string Llama = "C:\\ai\\bonsai\\llama\\llama-server.exe";

    [TestMethod]
    public async Task WatchersAreCreatedUpdatedAndRemovedAfterTwoMissingPasses()
    {
        var p = new FakePlatform();
        var reg = new ServerRegistry(p, new HttpClient(new FakeHandler()));
        int changes = 0;
        reg.ServersChanged += () => changes++;
        p.AddServer(30, Llama, Samples.BonsaiCmd, 8081, "127.0.0.1");
        Assert.IsTrue(await reg.RefreshNowAsync());
        Assert.AreEqual(1, reg.Servers.Count);
        var first = reg.Servers[0];
        Assert.AreEqual("127.0.0.1:8081", first.Key);
        Assert.AreEqual("bonsai-27b-1bit", first.Name);

        // zweites Programm kommt hinzu, die Liste ist nach Port sortiert und der erste Watcher bleibt derselbe
        p.AddServer(31, Llama, "llama-server -m x.gguf --port 8080 --alias b2", 8080, "127.0.0.1");
        Assert.IsTrue(await reg.RefreshNowAsync());
        Assert.AreEqual(2, reg.Servers.Count);
        Assert.AreEqual(8080, reg.Servers[0].Info.Port);
        Assert.AreSame(first, reg.Servers[1]);

        // Prozess verschwindet: ein Durchlauf Gnadenfrist, beim zweiten wird der Watcher entfernt
        p.Listeners.RemoveAll(l => l.Pid == 31);
        Assert.IsFalse(await reg.RefreshNowAsync());
        Assert.AreEqual(2, reg.Servers.Count);
        Assert.IsTrue(await reg.RefreshNowAsync());
        Assert.AreEqual(1, reg.Servers.Count);
        Assert.AreEqual(3, changes);
        reg.Dispose();
        Assert.AreEqual(0, reg.Servers.Count);
    }

    [TestMethod]
    public async Task RestartOnSamePortKeepsWatcherAndUpdatesInfo()
    {
        var p = new FakePlatform();
        var reg = new ServerRegistry(p, new HttpClient(new FakeHandler()));
        p.AddServer(30, Llama, "llama-server -m a.gguf --port 8081", 8081, start: 1);
        await reg.RefreshNowAsync();
        var w = reg.Servers[0];
        p.Listeners.Clear();
        p.AddServer(35, Llama, "llama-server -m b.gguf --port 8081 --alias neu", 8081, start: 2);
        await reg.RefreshNowAsync();
        Assert.AreSame(w, reg.Servers.Single());
        Assert.AreEqual(35, w.Pid);
        Assert.AreEqual("neu", w.Name);
        reg.Dispose();
    }

    [TestMethod]
    public async Task ManualServersAppearWithoutProcess_LoopbackDuplicatesAreIgnored()
    {
        var p = new FakePlatform();
        var manual = new[]
        {
            new ManualServer { Name = "Docker box", Url = "http://10.1.2.3:8080", Log = "x.log" },
            new ManualServer { Name = "dup", Url = "http://127.0.0.1:8081" },
        };
        var reg = new ServerRegistry(p, new HttpClient(new FakeHandler()), null, manual);
        p.AddServer(30, Llama, "llama-server -m a.gguf --port 8081 --alias real", 8081, "127.0.0.1");
        await reg.RefreshNowAsync();
        Assert.AreEqual(2, reg.Servers.Count);
        var box = reg.Servers.Single(s => s.Info.Manual);
        Assert.AreEqual("Docker box", box.Name);
        Assert.IsNull(box.Pid);
        Assert.AreEqual(SaveBlock.Manual, box.Info.SaveBlock);
        Assert.AreEqual("real", reg.Servers.Single(s => !s.Info.Manual).Name);
        reg.Dispose();
    }

    [TestMethod]
    public async Task LibraryGetsEntriesForDetectedServers()
    {
        var p = new FakePlatform();
        var lib = new Library();
        var reg = new ServerRegistry(p, new HttpClient(new FakeHandler()), lib);
        p.AddServer(30, Llama, Samples.BonsaiCmd, 8081, "127.0.0.1");
        await reg.RefreshNowAsync();
        reg.ObserveLibrary(DateTime.Now);
        reg.ObserveLibrary(DateTime.Now.AddSeconds(1));
        var h = lib.History.Single();
        Assert.AreEqual("bonsai-27b-1bit", h.Name);
        Assert.AreEqual(1, h.Runs);
        reg.Dispose();
    }
}
