using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// R1.2 – Simulator: simulierte Server, eigener Datenbereich, Log-Pipeline, Proxy und Aufnahme, keine echten Aktionen
[TestClass]
public class R1_SimulationTests
{
    private static List<SimServerSpec> Fast() => new()
    {
        new SimServerSpec { Kind = BackendKind.LlamaCpp, Name = "sim-a", Model = "model-a", Context = 8192, Slots = 2, TpsMin = 200, TpsMax = 300, RequestsPerMinute = 300, ThinkPercent = 50, ToolCalls = true, ModelGb = 4 },
        new SimServerSpec { Kind = BackendKind.Ollama, Name = "Ollama", Model = "llama3.2:3b", Context = 4096, Slots = 1, ModelGb = 2 },
        new SimServerSpec { Kind = BackendKind.LmStudio, Name = "LM Studio", Model = "gemma-3-4b-it", Context = 8192, Slots = 2, TpsMin = 100, TpsMax = 150, RequestsPerMinute = 120, ModelGb = 2.6 },
    };

    // Zeit künstlich weiterschalten (ohne zu warten)
    private static DateTime Advance(SimWorld w, DateTime t, double seconds)
    {
        for (double s = 0; s < seconds; s += 0.25) { t = t.AddSeconds(0.25); w.Step(t); }
        return t;
    }

    [TestMethod]
    public void World_ProducesRequestsLogAndRecords_Deterministic()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sim-test-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var w = new SimWorld(42) { LogDir = dir };
            var s = w.Add(Fast()[0]);
            var records = new List<ProxyRecord>();
            w.Observed += (_, r) => records.Add(r);
            w.Start();
            var t = DateTime.Now;
            w.Step(t);
            Advance(w, t, 60);
            Assert.IsTrue(records.Count >= 5, "bei 300 Anfragen/min müssen in 60 s viele fertig werden: " + records.Count);
            Assert.IsTrue(records.Any(r => r.ReasoningTokens > 0), "Denk-Token bei ThinkPercent 50");
            Assert.IsTrue(records.Any(r => r.Tools.Length > 0), "Werkzeugaufrufe");
            Assert.IsTrue(records.All(r => r.ContentTokens + Math.Max(0, r.ReasoningTokens) > 0));
            var log = File.ReadAllText(s.LogPath!);
            StringAssert.Contains(log, "release: id");
            StringAssert.Contains(log, "n_gen =");
            StringAssert.Contains(log, "prompt eval time");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [TestMethod]
    public void Pause_StopsNewRequests_ButLetsRunningOnesFinish()
    {
        var w = new SimWorld(1);
        w.Add(Fast()[0]);
        w.Start();
        var t = DateTime.Now;
        w.Step(t);
        t = Advance(w, t, 10);
        w.Pause();
        int doneAtPause = 0;
        w.Observed += (_, _) => doneAtPause++;
        t = Advance(w, t, 60);
        Assert.IsTrue(w.Servers.All(s => s.BusySlots == 0 || s.Queue >= 0));
        Assert.AreEqual(0, w.Servers[0].BusySlots, "nach dem Auslaufen ist alles leer");
        int before = doneAtPause;
        Advance(w, t, 30);
        Assert.AreEqual(before, doneAtPause, "während der Pause beginnt nichts Neues");
    }

    [TestMethod]
    public async Task Handler_AnswersLikeRealServers()
    {
        var w = new SimWorld(3);
        foreach (var s in Fast()) w.Add(s);
        using var http = new HttpClient(new SimHandler(w));
        var llama = w.Servers.First(s => s.Spec.Kind == BackendKind.LlamaCpp);
        var lm = w.Servers.First(s => s.Spec.Kind == BackendKind.LmStudio);
        var ol = w.Servers.First(s => s.Spec.Kind == BackendKind.Ollama);

        var props = LlamaProps.Parse(await http.GetStringAsync($"http://127.0.0.1:{llama.Port}/props"));
        Assert.IsNotNull(props);
        Assert.AreEqual(2, props!.TotalSlots);
        Assert.AreEqual(4096, props.NCtx);

        using var slots = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{llama.Port}/slots"));
        Assert.AreEqual(2, slots.RootElement.GetArrayLength());

        var models = LmStudioApi.ParseModels(await http.GetStringAsync($"http://127.0.0.1:{lm.Port}/api/v0/models"))!;
        Assert.AreEqual(1, LmStudioApi.Loaded(models).Count);

        // Engine von LM Studio: ohne Schlüssel abgewiesen, mit Schlüssel erreichbar
        var noKey = await http.GetAsync($"http://127.0.0.1:{lm.EnginePort}/slots");
        Assert.AreEqual(HttpStatusCode.Unauthorized, noKey.StatusCode);
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{lm.EnginePort}/slots");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lm.ApiKey);
        Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(req)).StatusCode);

        // Ollama: Entladen wirkt auf die Simulation
        Assert.AreEqual(1, OllamaApi.ParsePs(await http.GetStringAsync($"http://127.0.0.1:{ol.Port}/api/ps"))!.Count);
        var unload = await http.PostAsync($"http://127.0.0.1:{ol.Port}/api/generate", new StringContent(OllamaApi.UnloadBody("llama3.2:3b")));
        Assert.IsTrue(unload.IsSuccessStatusCode);
        Assert.AreEqual(0, OllamaApi.ParsePs(await http.GetStringAsync($"http://127.0.0.1:{ol.Port}/api/ps"))!.Count);

        // unbekannte Adresse: wie "Verbindung abgelehnt"
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => http.GetStringAsync("http://127.0.0.1:9/props"));
    }

    [TestMethod]
    public async Task Handler_ChatAnswersForBenchmark()
    {
        var w = new SimWorld(3);
        var s = w.Add(Fast()[0]);
        using var http = new HttpClient(new SimHandler(w));
        string text = BenchmarkRunner.Filler(3000, 1);
        int? n = await BenchmarkRunner.CountTokensAsync(http, $"http://127.0.0.1:{s.Port}", null, text, CancellationToken.None);
        Assert.IsTrue(n is > 50);
        var opt = new BenchOptions { Chat = true, Repeats = 1, ContextSizes = Array.Empty<int>(), Tool = true };
        var result = new BenchResult { Server = "sim" };
        await BenchmarkRunner.RunAsync(http, $"http://127.0.0.1:{s.Port}", opt, result);
        Assert.IsTrue(result.Steps.Count >= 2, "Chat und Werkzeugtest");
        Assert.IsTrue(result.Steps.All(x => x.Ok), string.Join("; ", result.Steps.Select(x => x.Note)));
        Assert.IsTrue(result.Steps[0].GenTps > 100);
    }

    [TestMethod]
    public async Task Host_DiscoversAllKinds_LmStudioEngineIsChild_AndUsesOwnDataFolder()
    {
        using var host = new SimHost(Fast(), seed: 5, autoStep: false);
        var reg = host.Engine.Registry;
        await reg.RefreshNowAsync();
        var kinds = reg.Servers.Select(x => x.Kind).OrderBy(k => k).ToList();
        CollectionAssert.AreEqual(new[] { BackendKind.LlamaCpp, BackendKind.Ollama, BackendKind.LmStudio }, kinds, "genau eine Karte je simuliertem Server");
        var lm = reg.Servers.Single(x => x.Kind == BackendKind.LmStudio);
        Assert.AreEqual(1, lm.Children.Count, "die Engine gehört zur LM-Studio-Karte");
        StringAssert.StartsWith(host.Paths.Root, Path.GetTempPath().TrimEnd('\\'));
        Assert.AreNotEqual(AppPaths.Default().Root, host.Paths.Root, "nicht der Datenordner des Nutzers");
        Assert.IsTrue(host.Engine.IsSimulated);
    }

    [TestMethod]
    public async Task Stop_OnSimulatedServer_RemovesOnlyTheSimulation()
    {
        using var host = new SimHost(Fast(), autoStep: false);
        await host.Engine.Registry.RefreshNowAsync();
        var w = host.Engine.Servers.First(x => x.Kind == BackendKind.LlamaCpp);
        var outcome = ServerLauncher.Stop(host.Platform, w.Pid!.Value, w.Info.StartTicks, out _);
        Assert.AreEqual(StopOutcome.Stopped, outcome);
        Assert.IsFalse(host.World.Servers.Any(s => s.Spec.Kind == BackendKind.LlamaCpp));
        Assert.AreEqual(StopOutcome.ProcessChanged, ServerLauncher.Stop(host.Platform, host.World.Servers[0].Pid, 12345, out _));
    }

    [TestMethod]
    public async Task Start_OnSimulation_NeverStartsARealProgram_ButAddsASimulatedServer()
    {
        using var host = new SimHost(new[] { Fast()[1] }, autoStep: false);
        var coordinator = new LaunchCoordinator(host.Engine, new FakePrompt());
        // ein Programm, das es nicht gibt: im Betrieb ein Fehler, im Simulator ein simulierter Server
        var spec = new LaunchSpec("my-model", @"C:\does\not\exist\llama-server.exe", new[] { "-m", @"C:\m\my-model.gguf", "--port", "8123", "-c", "4096", "-np", "2" }, null);
        Assert.IsTrue(await coordinator.StartSpecAsync(spec));
        for (int i = 0; i < 80 && !host.World.Servers.Any(s => s.Spec.Model == "my-model"); i++) await Task.Delay(100);
        var added = host.World.Servers.SingleOrDefault(s => s.Spec.Model == "my-model");
        Assert.IsNotNull(added);
        Assert.AreEqual(2, added!.Spec.Slots);
        Assert.AreEqual(4096, added.Spec.Context);
        Assert.AreEqual(0, host.Engine.Registry.Launches.Count, "kein echter Startvorgang");
    }

    [TestMethod]
    public async Task Proxy_And_Recording_SeeThinkingToolsAndTtft_InRealTime()
    {
        var specs = new List<SimServerSpec> { Fast()[0] };
        specs[0].RequestsPerMinute = 600;
        using var host = new SimHost(specs, seed: 11, autoStep: true);
        host.World.Start();
        var engine = host.Engine;
        await engine.TickAsync();
        var w = engine.Servers.Single();
        engine.Proxies.SetTarget(w.Key);
        Assert.IsTrue(engine.Proxies.Toggle(out var err), err);
        Assert.IsTrue(engine.Proxies.IsRunningFor(w.Key));
        var session = engine.StartRecording(w, out var problem);
        Assert.IsNotNull(session, problem);
        for (int i = 0; i < 9; i++) { await Task.Delay(1000); await engine.TickAsync(); }
        var sum = await engine.StopRecordingAsync(session!);
        Assert.IsNotNull(sum);
        var data = RecordingStore.Load(sum!.File)!;
        Assert.IsTrue(data.Requests.Count >= 3, "Anfragen aufgenommen: " + data.Requests.Count);
        Assert.IsTrue(data.Requests.Any(r => r.FromProxy && r.TtftSec >= 0), "Zeit bis zum ersten Token vom (simulierten) Proxy");
        Assert.IsTrue(data.Requests.Any(r => r.ReasoningTokens > 0 && r.ThinkSec > 0), "Denkzeit");
        Assert.IsTrue(data.Requests.Any(r => r.Tools.Length > 0), "Werkzeugaufrufe");
        Assert.IsTrue(data.Requests.Any(r => r.Status == "done"), "normale Antworten gelten nicht als abgeschnitten");
        Assert.IsTrue(data.Samples.Count >= 5);
        Assert.IsTrue(engine.Proxies.Get()!.Requests >= 3);
        // und die Daten liegen im eigenen Bereich
        StringAssert.StartsWith(sum.File, host.Paths.RecordingsDir);
    }

    [TestMethod]
    public async Task GpuAndSystem_CanBeSwitchedOff()
    {
        using var host = new SimHost(Fast(), autoStep: false);
        Assert.IsNotNull(host.Platform.ReadGpu());
        Assert.IsNotNull(host.Platform.ReadSystem());
        host.World.ShowGpu = false; host.World.ShowSystem = false;
        Assert.IsNull(host.Platform.ReadGpu());
        Assert.IsNull(host.Platform.ReadSystem());
        await host.Engine.TickAsync();
        Assert.IsNull(host.Engine.Gpu);
    }

    [TestMethod]
    public void Spec_Normalize_ClampsValues()
    {
        var s = new SimServerSpec { Slots = 0, Context = 10, TpsMin = 50, TpsMax = 10, RequestsPerMinute = -3, ThinkPercent = 200, Model = " " };
        s.Normalize();
        Assert.AreEqual(1, s.Slots);
        Assert.IsTrue(s.Context >= 512);
        Assert.IsTrue(s.TpsMax >= s.TpsMin);
        Assert.AreEqual(0, s.RequestsPerMinute);
        Assert.AreEqual(95, s.ThinkPercent);
        Assert.AreEqual("model", s.Model);
    }

    [TestMethod]
    public void Defaults_CoverAllKinds()
    {
        var d = SimServerSpec.Defaults();
        Assert.IsTrue(d.Select(x => x.Kind).Distinct().Count() == 3);
        Assert.IsTrue(d.Any(x => x.ThinkPercent > 0) && d.Any(x => x.ToolCalls));
    }
}
