using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Kleine S-Features (2026-10-02): wartende Anfragen (llama-server /metrics), Shared-Memory-Hinweis, Entlade-Countdown
[TestClass]
public class QueueSpillTests
{
    // ── llama-server /metrics (Prometheus) ──
    [TestMethod]
    public void Metrics_ParsesRequestCounters()
    {
        const string text =
            "# HELP llamacpp:requests_processing Number of requests processing.\n" +
            "# TYPE llamacpp:requests_processing gauge\n" +
            "llamacpp:requests_processing 2\n" +
            "# TYPE llamacpp:requests_deferred gauge\n" +
            "llamacpp:requests_deferred 5\n" +
            "llamacpp:kv_cache_tokens 1234\n";
        var r = LlamaMetricsApi.Parse(text);
        Assert.AreEqual(2, r.Processing);
        Assert.AreEqual(5, r.Deferred);
    }

    [TestMethod]
    public void Metrics_ToleratesMissingAndOddLines()
    {
        Assert.IsNull(LlamaMetricsApi.Parse("").Processing);
        Assert.IsNull(LlamaMetricsApi.Parse("no metrics here").Deferred);
        Assert.IsNull(LlamaMetricsApi.Parse("llamacpp:requests_deferred{slot=\"0\"} 3").Deferred);   // Labels: nicht unser Fall
        Assert.AreEqual(0, LlamaMetricsApi.Parse("llamacpp:requests_deferred 0").Deferred);
        Assert.IsNull(LlamaMetricsApi.Parse("llamacpp:requests_deferred abc").Deferred);
    }

    // ── Shared Memory (liegt im RAM statt im Karten-Speicher) ──
    [TestMethod]
    public void Spill_OllamaModelPartlyInRam()
    {
        var m = new LoadedModel(Name: "llama3.2:3b", SizeBytes: 4_000_000_000, VramBytes: 1_000_000_000,
            ExpiresAt: null, ContextLength: null, Detail: "", State: "loaded");
        Assert.AreEqual(3_000_000_000 / 1073741824.0, ServerWatcher.SpillGb(new[] { m }, null)!.Value, 1e-6);
    }

    [TestMethod]
    public void Spill_UsesMeasuredShared_AndIgnoresNoise()
    {
        Assert.IsNull(ServerWatcher.SpillGb(Array.Empty<LoadedModel>(), null));
        Assert.IsNull(ServerWatcher.SpillGb(Array.Empty<LoadedModel>(), 0.1));       // kleine Anzeigen sind normal
        Assert.AreEqual(1.5, ServerWatcher.SpillGb(Array.Empty<LoadedModel>(), 1.5)!.Value, 1e-6);
        // Vollständig auf der Karte: keine Meldung
        var ok = new LoadedModel(Name: "m", SizeBytes: 3_000_000_000, VramBytes: 3_000_000_000,
            ExpiresAt: null, ContextLength: null, Detail: "", State: "loaded");
        Assert.IsNull(ServerWatcher.SpillGb(new[] { ok }, 0.0));
        // Ollama-Meldung hat Vorrang vor der gemessenen Zahl
        var m = new LoadedModel(Name: "m", SizeBytes: 3_000_000_000, VramBytes: 1_000_000_000,
            ExpiresAt: null, ContextLength: null, Detail: "", State: "loaded");
        Assert.AreEqual(2_000_000_000 / 1073741824.0, ServerWatcher.SpillGb(new[] { m }, 9.0)!.Value, 1e-6);
    }

    // ── Entlade-Countdown (ein Text für Fenster, Web und TUI) ──
    [TestMethod]
    public void Until_FormatsCountdown()
    {
        Assert.AreEqual("", Strings.Until(null));
        Assert.AreEqual(Strings.StaysLoaded, Strings.Until(DateTime.Now.AddDays(400)));
        Assert.AreEqual(Strings.Unloading, Strings.Until(DateTime.Now.AddSeconds(-5)));
        StringAssert.StartsWith(Strings.Until(DateTime.Now.AddSeconds(30)), "unloads in ");
        Assert.AreEqual(Strings.UnloadsInMinutes(10), Strings.Until(DateTime.Now.AddMinutes(10)));
    }

    // ── Simulator: /metrics antwortet wie ein echter llama-server ──
    [TestMethod]
    public async Task Sim_MetricsEndpointAnswersPrometheus()
    {
        var w = new SimWorld(3);
        var llama = w.Add(new SimServerSpec
        {
            Kind = BackendKind.LlamaCpp, Name = "sim-a", Model = "model-a", Context = 8192, Slots = 2,
            TpsMin = 200, TpsMax = 300, RequestsPerMinute = 300, ModelGb = 4,
        });
        using var http = new HttpClient(new SimHandler(w));
        var r = LlamaMetricsApi.Parse(await http.GetStringAsync($"http://127.0.0.1:{llama.Port}/metrics"));
        Assert.IsTrue(r.Processing is >= 0, "/metrics liefert requests_processing");
        Assert.IsTrue(r.Deferred is >= 0, "/metrics liefert requests_deferred");
    }
}