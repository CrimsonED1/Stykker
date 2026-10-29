using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Prompt-Verarbeitung und Zeit bis zum ersten Token je Server (ServerLatency) und als Spalte der Anfrageliste (RequestRows)
[TestClass]
public class N104_ServerLatencyTests
{
    private const string Key = "127.0.0.1:8081";
    private static readonly DateTime T0 = new(2026, 10, 29, 6, 0, 0, DateTimeKind.Local);

    private static FinishedRequest Done(int seq, int promptTokens, double promptTps, int secondsAfter, string key = Key) =>
        new(seq, "qwen", "qwen3-8b", seq, 0, promptTokens, promptTps, 50, 200, 5, T0.AddSeconds(secondsAfter), ReqStatus.Done, ServerKey: key);

    private static ProxyRecord Px(double ttft, int secondsAfter, string key = Key, int status = 200, bool thinkingFirst = false)
    {
        var start = T0.AddSeconds(secondsAfter);
        var first = start.AddSeconds(ttft);
        return new ProxyRecord(key, "/v1/chat/completions", start, start.AddSeconds(ttft + 4), thinkingFirst ? first : null,
            thinkingFirst ? first.AddSeconds(1) : first, 0, 100, Array.Empty<string>(), "stop", "opencode/1.0", true, status, false);
    }

    [TestMethod]
    public void Median_OddEvenEmpty()
    {
        Assert.AreEqual(2, ServerLatency.Median(new[] { 3.0, 1, 2 }));
        Assert.AreEqual(2.5, ServerLatency.Median(new[] { 4.0, 1, 2, 3 }));
        Assert.IsNull(ServerLatency.Median(Array.Empty<double>()));
    }

    [TestMethod]
    public void Of_ProxyMeasured_WinsOverLogEstimate()
    {
        var finished = new[] { Done(1, 1000, 500, 10), Done(2, 3000, 1000, 20), Done(3, 2000, 800, 30) };
        var proxy = new[] { Px(0.4, 5), Px(0.8, 15), Px(0.6, 25) };
        var s = ServerLatency.Of(finished, proxy, Key);
        Assert.AreEqual(800, s.PromptTps);
        Assert.IsTrue(s.TtftMeasured);
        Assert.AreEqual(0.6, s.TtftSec!.Value, 1e-9);
        Assert.AreEqual(3, s.Samples);
    }

    [TestMethod]
    public void Of_WithoutProxy_EstimatesFromPromptReading()
    {
        // Lesedauer: 1000/500 = 2 s, 3000/1000 = 3 s, 2000/800 = 2.5 s → Median 2.5 s
        var finished = new[] { Done(1, 1000, 500, 10), Done(2, 3000, 1000, 20), Done(3, 2000, 800, 30) };
        var s = ServerLatency.Of(finished, Array.Empty<ProxyRecord>(), Key);
        Assert.IsFalse(s.TtftMeasured);
        Assert.AreEqual(2.5, s.TtftSec!.Value, 1e-9);
    }

    [TestMethod]
    public void Of_IgnoresOtherServersAndErrors()
    {
        var proxy = new[] { Px(9, 5, key: "127.0.0.1:9999"), Px(7, 6, status: 500), Px(0.3, 7) };
        var s = ServerLatency.Of(Array.Empty<FinishedRequest>(), proxy, Key);
        Assert.AreEqual(0.3, s.TtftSec!.Value, 1e-9);
        Assert.AreEqual(1, s.Samples);
        Assert.IsNull(s.PromptTps);
    }

    [TestMethod]
    public void Of_NothingKnown_GivesNulls()
    {
        var s = ServerLatency.Of(Array.Empty<FinishedRequest>(), Array.Empty<ProxyRecord>(), Key);
        Assert.IsNull(s.PromptTps);
        Assert.IsNull(s.TtftSec);
        Assert.AreEqual(0, s.Samples);
    }

    [TestMethod]
    public void Ttft_CountsThinkingTokenAsFirst()
    {
        Assert.AreEqual(1.5, ServerLatency.Ttft(Px(1.5, 0, thinkingFirst: true))!.Value, 1e-9);
        Assert.AreEqual(1.5, ServerLatency.Ttft(Px(1.5, 0))!.Value, 1e-9);
        var none = Px(1, 0) with { FirstTokenAt = null, FirstContentAt = null };
        Assert.IsNull(ServerLatency.Ttft(none));
    }

    [TestMethod]
    public void Seconds_Format()
    {
        Assert.AreEqual("0.42", ServerLatency.Seconds(0.4234));
        Assert.AreEqual("9.99", ServerLatency.Seconds(9.99));
        Assert.AreEqual("12", ServerLatency.Seconds(12.4));
    }

    [TestMethod]
    public void RequestRows_CarryTtftFromProxy()
    {
        var f = Done(1, 1000, 500, 10);
        // Ende laut Proxy liegt im Abgleichfenster des Log-Endes
        var px = new ProxyRecord(Key, "/v1/chat/completions", T0.AddSeconds(4), T0.AddSeconds(10), T0.AddSeconds(4.7), null, 0, 100,
            Array.Empty<string>(), "stop", "opencode/1.0", true, 200, false);
        var rows = RequestRows.Build(new[] { (f, (string?)null) }, new[] { px });
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(0.7, rows[0].Ttft!.Value, 1e-6);

        var plain = RequestRows.Build(new[] { (f, (string?)null) }, Array.Empty<ProxyRecord>());
        Assert.IsNull(plain[0].Ttft, "ohne Proxy keine gemessene Zeit");
    }
}
