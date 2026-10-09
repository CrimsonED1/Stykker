using System.Net;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// vLLM als Backend (nur Linux) und die Linux-Fähigkeit des Servers, soweit sie ohne Linux-Rechner prüfbar ist:
// der Parser von vLLMs Prometheus-Text, die Probe, die Ausnahme in der Erkennung (vLLM läuft als „python -m vllm…“)
// und der Schlüssel des Servers ohne Benutzerbindung. Eine laufende vLLM stand nicht zur Verfügung – geprüft
// sind die Antworten, die sie laut Dokumentation liefert.
[TestClass]
public class VllmTests
{
    // Auszug aus einem echten /metrics-Text: Kommentarzeilen, Labels am Zähler, Histogram, unbekannte Namen
    private const string Metrics = """
        # HELP vllm:num_requests_running Number of requests currently running on GPU.
        # TYPE vllm:num_requests_running gauge
        vllm:num_requests_running{model_name="Qwen/Qwen2.5-0.5B"} 2.0
        # TYPE vllm:num_requests_waiting gauge
        vllm:num_requests_waiting{model_name="Qwen/Qwen2.5-0.5B"} 3.0
        # TYPE vllm:avg_generation_throughput_toks_per_s gauge
        vllm:avg_generation_throughput_toks_per_s{model_name="Qwen/Qwen2.5-0.5B"} 137.42
        vllm:gpu_cache_usage_perc{model_name="Qwen/Qwen2.5-0.5B"} 0.042
        vllm:request_success_total{finished_reason="stop",model_name="Qwen/Qwen2.5-0.5B"} 12.0
        vllm:e2e_request_latency_seconds_bucket{le="1.0"} 4.0
        vllm:prompt_tokens_total 1000.0
        """;

    [TestMethod]
    public void Vllm_MetricsAreReadDespiteCommentsAndLabels()
    {
        var m = VllmApi.ParseMetrics(Metrics);
        Assert.AreEqual(137.42, m.Tps!.Value, 0.001, "Erzeugungs-Tokens/s");
        Assert.AreEqual(2, m.Running);
        Assert.AreEqual(3, m.Waiting, "wartende Anfragen");
        Assert.AreEqual(0.042, m.GpuCachePct!.Value, 0.0001);
    }

    [TestMethod]
    public void Vllm_MetricsTolerateEmptyAndBrokenText()
    {
        foreach (var text in new[] { "", "# HELP x y\n# TYPE x gauge\n", "vllm:num_requests_running 1.0\n", "unfug = 3\n" })
        {
            var m = VllmApi.ParseMetrics(text);
            Assert.IsNull(m.Tps, text);
        }
        // Ohne die Beschriftung am Zähler (andere vLLM-Version) muss es trotzdem gehen
        var plain = VllmApi.ParseMetrics("vllm:avg_generation_throughput_toks_per_s 88.5\nvllm:num_requests_waiting 1\n");
        Assert.AreEqual(88.5, plain.Tps!.Value, 0.001);
        Assert.AreEqual(1, plain.Waiting);
    }

    [TestMethod]
    public void Vllm_ModelsAndVersionAreRead()
    {
        Assert.AreEqual("0.11.0", VllmApi.ParseVersion("""{"version": "0.11.0"}"""));
        Assert.IsNull(VllmApi.ParseVersion("{}"));
        Assert.IsNull(VllmApi.ParseVersion("kein json"));

        var models = VllmApi.ParseModels("""{"object":"list","data":[{"id":"Qwen/Qwen2.5-0.5B","object":"model"},{"id":"meta-llama/Llama-3.2-1B"}]}""");
        Assert.AreEqual(2, models!.Count);
        Assert.AreEqual("Qwen/Qwen2.5-0.5B", models[0]);
        Assert.AreEqual(0, VllmApi.ParseModels("""{"object":"list","data":[]}""")!.Count);
        Assert.IsNull(VllmApi.ParseModels("[]"), "keine gültige Antwort");
    }

    [TestMethod]
    public async Task Vllm_ProbeAnswersYesOnItsVersionEndpoint()
    {
        var handler = new FakeHandler();
        handler.Routes["127.0.0.1:8000/version"] = (HttpStatusCode.OK, """{"version": "0.11.0"}""");
        using var http = new HttpClient(handler);

        var found = await new VllmProbe().ProbeAsync(http, "http://127.0.0.1:8000", default);
        Assert.AreEqual(ProbeVerdict.Yes, found.Verdict);
        Assert.AreEqual(BackendKind.Vllm, found.Kind);
        Assert.AreEqual("0.11.0", found.Version);

        // Ein llama-server ohne /version darf nicht als vLLM gelten
        var llama = new FakeHandler();
        llama.Routes["127.0.0.1:8080/version"] = (HttpStatusCode.NotFound, "");
        using var http2 = new HttpClient(llama);
        Assert.AreEqual(ProbeVerdict.No, (await new VllmProbe().ProbeAsync(http2, "http://127.0.0.1:8080", default)).Verdict);
        Assert.IsFalse(new VllmProbe().AppliesTo(new ProbeTarget("ollama.exe", "", null, null)));
        Assert.IsTrue(new VllmProbe().AppliesTo(new ProbeTarget("", "", null, null)), "gilt auch für „Add server by URL“");
    }

    [TestMethod]
    public void Vllm_IsInTheProbeListAndNamed()
    {
        Assert.IsTrue(BackendProbes.Default().Any(p => p.Kind == BackendKind.Vllm), "die Probe gehört zur Liste");
        Assert.IsTrue(BackendProbes.KindFromName("vLLM") == BackendKind.Vllm, "die Art aus dem Namen (Add server by URL)");
        Assert.IsTrue(BackendProbes.KindFromName("vllm") == BackendKind.Vllm);
        Assert.AreEqual("vLLM", BackendProbes.DisplayName(BackendKind.Vllm));
        Assert.AreEqual("llama.cpp", BackendProbes.DisplayName(BackendKind.LlamaCpp), "die anderen bleiben, wie sie waren");
    }

    // Ohne diese Ausnahme bliebe jeder vLLM-Server unsichtbar: er läuft als „python -m vllm…“ und Python
    // wird grundsätzlich übersprungen (Ollamas Runner, Strata).
    [TestMethod]
    public void Vllm_PythonProcessesAreProbedOnlyWhenTheCommandLineSaysVllm()
    {
        static ServerDiscovery.ProcEntry Python(string cmdline) => new()
        {
            ExeName = "python3",
            Deep = true,
            Details = new ProcessDetails(1, 1, 0, "/usr/bin/python3", cmdline, null, new Dictionary<string, string>()),
        };

        Assert.IsFalse(ServerDiscovery.IsExcludedFromProbe(Python("/usr/bin/python3 -m vllm.entrypoints.openai.api_server --model Qwen --port 8000")),
            "vLLM darf geprüft werden");
        Assert.IsTrue(ServerDiscovery.IsExcludedFromProbe(Python("python -m strata.server --port 8082")), "andere Python-Dienste bleiben übersprungen");
        Assert.IsTrue(ServerDiscovery.IsExcludedFromProbe(Python("python -m http.server 8000")));

        Assert.IsTrue(VllmApi.MentionsVllm("/usr/bin/python3 -m vllm.entrypoints.cli.main serve Qwen"));
        Assert.IsFalse(VllmApi.MentionsVllm("/usr/bin/python3 -m http.server 8000"));
        Assert.IsFalse(VllmApi.MentionsVllm(null));
    }

    // Ohne Benutzerbindung (alles außerhalb von Windows) legt WriteKey den Schlüssel base64-kodiert im Klartext ab.
    // Vorher gab ReadKey dann null zurück – ein Server unter Linux hätte seinen Schlüssel nie wieder gelesen und
    // Fenster und TUI wären nie hereingekommen.
    [TestMethod]
    public void ServerKey_SurvivesWithoutUserBinding()
    {
        using var tmp = new TempDir();
        var paths = new AppPaths(tmp.Path);
        var ohneBindung = new FakePlatform { UserProtection = false };
        var key = ServerClient.WriteKey(paths, ohneBindung);
        Assert.IsNotNull(ServerClient.ReadKey(paths, ohneBindung), "Klartext-Rückfall");
        Assert.AreEqual(key, ServerClient.ReadKey(paths, neuePlattformOhneBindung()));

        // Mit Bindung (Windows) bleibt es dabei, und eine fremde Bindung liefert weiterhin nichts
        var mitBindung = new FakePlatform { UserProtection = true };
        var key2 = ServerClient.WriteKey(paths, mitBindung);
        Assert.AreEqual(key2, ServerClient.ReadKey(paths, mitBindung));
        Assert.IsNull(ServerClient.ReadKey(paths, ohneBindung), "mit einer anderen Bindung nicht lesbar");

        Assert.IsNull(ServerClient.ReadKey(new AppPaths(Path.Combine(tmp.Path, "gibtsnicht")), ohneBindung));

        static FakePlatform neuePlattformOhneBindung() => new() { UserProtection = false };
    }

    // Der ganze Weg durch die Maschine: simulierter vLLM (Python-Prozess mit „-m vllm…“) → Erkennung ohne die
    // Python-Ausnahme zu umgehen → Probe auf /version → Messwerte aus /metrics. Ohne echten vLLM ist das der
    // Nachweis, dass die Kette zusammenpasst.
    [TestMethod]
    public async Task Vllm_SimulatedServerIsFoundAndMeasuredEndToEnd()
    {
        var spec = new SimServerSpec
        {
            Kind = BackendKind.Vllm, Name = "vllm", Model = "Qwen/Qwen2.5-0.5B", Context = 32768, Slots = 2,
            TpsMin = 90, TpsMax = 130, RequestsPerMinute = 240, ModelGb = 3.0,
        };
        using var host = new SimHost(new List<SimServerSpec> { spec }, seed: 7, autoStep: false);
        host.World.Start();   // ohne das läuft die Simulation nicht und es ist immer alles idle
        // Kleine Schritte: /metrics wird genau dann abgefragt, wenn im Simulator eine Anfrage läuft –
        // das ist der Punkt, an dem vLLM seine Tokens/s meldet.
        for (int i = 0; i < 40; i++)
        {
            host.World.Step(DateTime.Now.AddSeconds(2 * (i + 1)));
            await host.Engine.Registry.RefreshNowAsync();   // Erkennung
            foreach (var s in host.Engine.Servers) s.ExternalEverySeconds = 0;   // Messung ohne Sparrhythmus
            await host.Engine.TickAsync();                  // pollt die Karten
            await Task.Delay(15);
        }
        var w = host.Engine.Servers.FirstOrDefault(s => s.Kind == BackendKind.Vllm);
        Assert.IsNotNull(w, "der simulierte vLLM wurde erkannt (Python-Ausnahme, Probe auf /version)");
        Assert.IsTrue(w!.Online, "online");
        w.ExternalEverySeconds = 0;   // sonst fragt der Watcher nur alle 3 s ab – der Test läuft in Bruchteilen davon
        Assert.AreEqual("0.11.0", w.BackendVersion);
        CollectionAssert.Contains(w.Models.Select(m => m.Name).ToList(), "Qwen/Qwen2.5-0.5B", "Modell aus /v1/models");

        // t/s stammen aus /metrics; der Zustand nennt den Server als vllm
        Assert.IsTrue(w.Peak > 0, $"Erzeugungs-Tokens/s aus vllm:avg_generation_throughput_toks_per_s – current={w.Current:0.0} peak={w.Peak:0.0} hist={w.HistoryCount}");
        var json = StateJson.WriteText(host.Engine, null, null, 17400, DateTimeOffset.Now);
        StringAssert.Contains(json, "\"backend\":\"vllm\"");
        Assert.IsTrue(w.QueueCount is >= 0, "Warteschlange aus vllm:num_requests_waiting: " + w.QueueCount);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slm-vllm-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}