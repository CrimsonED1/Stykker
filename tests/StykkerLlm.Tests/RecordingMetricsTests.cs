using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class RecordingMetricsTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    private static RecordingData Data(Action<RecordingData>? tweak = null)
    {
        var d = new RecordingData { Meta = new RecMeta("id", T0, "k:1", new[] { "k:1" }, new[] { "srv" }, "m", "gpu", true) };
        for (int i = 0; i <= 10; i++)
            d.Samples.Add(new RecSample(i, new GpuSampleRec(40 + i, 5 + i * 0.1, 16, 100 + i, 50, 2500, 13000), new SysSampleRec(10, 20),
                new[] { new ServerSampleRec("k:1", 0, 4.0, 3.0, 1.0, new[] { new SlotSampleRec(0, false, 0, 100 * i, 0) }, Array.Empty<string>(), 100 * i, 4096) }));
        d.Requests.Add(new RequestEvent
        {
            ServerKey = "k:1", ServerName = "srv", Task = 5, Start = T0.AddSeconds(2), End = T0.AddSeconds(4), PromptSec = 0.5, AnswerSec = 1.5,
            PromptTokens = 100, CachedTokens = 50, GenTokens = 150, GenTps = 100, PromptTps = 200, Status = "done",
        });
        tweak?.Invoke(d);
        return d;
    }

    [TestMethod]
    public void Series_PerMetric()
    {
        var d = Data();
        Assert.AreEqual(11, RecordingMetrics.Series(d, "gpu_power")[0].Y.Length);
        Assert.AreEqual(105.0, RecordingMetrics.Series(d, "gpu_power")[0].Y[5]);
        Assert.AreEqual(500, RecordingMetrics.Series(d, "ctx")[0].Y[5]);
        Assert.AreEqual(4.0, RecordingMetrics.Series(d, "srv_vram")[0].Y[0]);
        Assert.AreEqual(0, RecordingMetrics.Series(d, "unknown").Count);
        Assert.AreEqual(12, RecordingMetrics.All.Select(m => m.Id).Distinct().Count());
    }

    [TestMethod]
    public void Series_TimeToFirstToken_PerRequest()
    {
        var d = Data(x =>
        {
            x.Requests.Clear();
            x.Requests.Add(new RequestEvent { ServerKey = "k:1", Start = T0.AddSeconds(1), End = T0.AddSeconds(2), TtftSec = 0.4 });
            x.Requests.Add(new RequestEvent { ServerKey = "k:1", Start = T0.AddSeconds(3), End = T0.AddSeconds(4), TtftSec = 0.9 });
            x.Requests.Add(new RequestEvent { ServerKey = "k:1", Start = T0.AddSeconds(5), End = T0.AddSeconds(6) });   // ohne TTFT: zählt nicht
        });
        var s = RecordingMetrics.Series(d, "ttft").Single();
        Assert.AreEqual(2, s.X.Length);
        Assert.AreEqual(0.4, s.Y[0], 1e-9);
        Assert.AreEqual(0.9, s.Y[1], 1e-9);
        Assert.AreEqual(3.0, s.X[1], 1e-9);
    }

    [TestMethod]
    public void Summary_TimeToFirstTokenPercentiles()
    {
        var d = Data(x =>
        {
            x.Requests.Clear();
            for (int i = 1; i <= 3; i++)
                x.Requests.Add(new RequestEvent { ServerKey = "k:1", Start = T0.AddSeconds(i), End = T0.AddSeconds(i + 1), TtftSec = i });
        });
        var s = RecordingStore.Summarize(d);
        Assert.AreEqual(3, s.TtftSamples);
        Assert.AreEqual(2.0, s.TtftP50, 1e-9);
        Assert.AreEqual(3.0, s.TtftP95, 1e-9);
        Assert.AreEqual(3.0, s.TtftP99, 1e-9);
    }

    [TestMethod]
    public void TokensPerSecond_FallsBackToRequestSteps_WhenSamplesAreZero()
    {
        var d = Data();
        var s = RecordingMetrics.Series(d, "tps").Single();
        Assert.AreEqual(100, s.Y.Max());                         // aus den Anfragen
        Assert.IsTrue(s.X.Length == 4);                          // Stufe: 0, tps, tps, 0
        Assert.AreEqual(2.5, s.X[1], 1e-9);                      // Erzeugung beginnt nach dem Prompt (2 s + 0,5 s)
        Assert.AreEqual(4.0, s.X[2], 1e-9);
        // gemessene Ausschläge haben Vorrang
        var d2 = Data(x => x.Samples[3] = x.Samples[3] with { Servers = new[] { x.Samples[3].Servers[0] with { Tps = 55 } } });
        Assert.AreEqual(55, RecordingMetrics.Series(d2, "tps").Single().Y.Max());
        Assert.AreEqual(100, RecordingMetrics.TpsCombined(d, "x").Y.Max());
    }

    [TestMethod]
    public void Downsample_KeepsPeaks()
    {
        var x = Enumerable.Range(0, 1000).Select(i => (double)i).ToArray();
        var y = x.Select(v => v == 777 ? 99.0 : 1.0).ToArray();
        var s = RecordingMetrics.Downsample(new MetricSeries("a", x, y), 50);
        Assert.AreEqual(50, s.X.Length);
        Assert.AreEqual(99.0, s.Y.Max());
        Assert.AreSame(s, RecordingMetrics.Downsample(s, 100));
    }

    [TestMethod]
    public void Summary_UsesRequestSpeedsWhenSamplesMissThem()
    {
        var s = RecordingStore.Summarize(Data());
        Assert.AreEqual(100, s.AvgTps, 1e-9);
        Assert.AreEqual(100, s.PeakTps, 1e-9);
        Assert.AreEqual(150, s.TokensIn);
        Assert.AreEqual(150, s.TokensOut);
        Assert.AreEqual(100.0 * 50 / 150, s.CachePct, 1e-9);
        Assert.AreEqual(2.0 / 10 * 100, s.BusyPct, 1e-9);
        Assert.AreEqual(8.0, s.WaitSec, 1e-9);
        Assert.AreEqual(0.5, s.PromptSec, 1e-9);
        Assert.AreEqual(4.0, s.MaxVramGb, 1e-9);
        Assert.AreEqual((10 * 100 + 55) / 3600.0, s.EnergyWh, 1e-9);   // Leistung 101..110 W je Sekunde
    }

    [TestMethod]
    public void Summary_ParallelRequestsCountBusyTimeOnce()
    {
        var d = Data(x =>
        {
            x.Requests.Clear();
            x.Requests.Add(new RequestEvent { Start = T0.AddSeconds(1), End = T0.AddSeconds(5) });
            x.Requests.Add(new RequestEvent { Start = T0.AddSeconds(3), End = T0.AddSeconds(6) });
            x.Requests.Add(new RequestEvent { Start = T0.AddSeconds(8), End = T0.AddSeconds(9) });
        });
        Assert.AreEqual(60.0, RecordingStore.Summarize(d).BusyPct, 1e-9);   // 1..6 und 8..9 = 6 s von 10 s
    }

    [TestMethod]
    public void Merge_NonStreamSplitsGenerationTimeByTokenShare()
    {
        var log = RequestEvent.FromLog(new FinishedRequest(1, "s", "m", 1, 0, 100, 100, 50, 100, 5, T0.AddSeconds(5), ReqStatus.Done, "", 100, "k"), "k", "m");
        var px = new ProxyRecord("k", "/v1/chat/completions", T0, T0.AddSeconds(5), null, null, 75, 25, Array.Empty<string>(), "stop", "ua", false, 200, false);
        var e = RequestEvent.Merge(log, px, "k", "s", "m");
        Assert.AreEqual(4.0, e.ThinkSec + e.AnswerSec, 1e-9);
        Assert.AreEqual(3.0, e.ThinkSec, 1e-9);
        Assert.AreEqual(1.0, e.AnswerSec, 1e-9);
    }

    [TestMethod]
    public void Compare_MarksBetterSideAndDeltas()
    {
        var a = new RecordingSummary { Model = "A", AvgTps = 50, PeakTps = 60, MaxVramGb = 6, EnergyWh = 1, TokensOut = 1000, Started = T0 };
        var b = new RecordingSummary { Model = "B", AvgTps = 100, PeakTps = 90, MaxVramGb = 5, EnergyWh = 2, TokensOut = 1000, Started = T0 };
        var rows = RecordingMetrics.Compare(a, b);
        var avg = rows.Single(r => r.Label == "Average t/s");
        Assert.AreEqual("+100 %", avg.Delta);
        Assert.AreEqual(1, avg.Better);                                       // B schneller
        Assert.AreEqual(1, rows.Single(r => r.Label == "Max VRAM").Better);   // B braucht weniger VRAM
        Assert.AreEqual(0, rows.Single(r => r.Label == "Energy").Better);     // Energie allein wird nicht bewertet
        Assert.AreEqual(-1, rows.Single(r => r.Label == "Energy per 1k tokens out").Better);
    }

    [TestMethod]
    public void Describe_ListsTheFacts()
    {
        var r = new RequestEvent
        {
            Task = 12, Start = T0, End = T0.AddSeconds(3), PromptSec = 0.5, ThinkSec = 1, AnswerSec = 1.5, PromptTokens = 200, CachedTokens = 800, GenTokens = 120,
            ReasoningTokens = 80, ContentTokens = 40, GenTps = 48, TtftSec = 0.6, Tools = new[] { "read_file" }, FinishReason = "tool_calls", Client = "x", FromProxy = true,
        };
        var t = RecordingMetrics.Describe(r);
        foreach (var part in new[] { "#12", "first token after 0.6 s", "prompt 1,000 tok (800 cached)", "thinking 80 tok / 1.0 s", "answer 40 tok / 1.5 s", "tools: read_file", "finish: tool_calls", "client: x" })
            StringAssert.Contains(t, part);
        Assert.IsFalse(RecordingMetrics.Describe(new RequestEvent { Task = 1, Start = T0, End = T0.AddSeconds(1), GenTokens = 10 }).Contains("thinking"));
    }
}
