using System.Net;
using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class ProxyTapTests
{
    private const string Sse =
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":null}}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"Let\"}}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\" me think\"}}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"}}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\" world\"}}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"\"}}]}}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"city\\\":\\\"Paris\\\"}\"}}]}}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"finish_reason\":\"tool_calls\",\"delta\":{}}],\"timings\":{\"prompt_n\":10,\"predicted_n\":6}}\n\n" +
        "data: [DONE]\n\n";

    [TestMethod]
    public void Stream_CountsReasoningContentToolsAndTimes()
    {
        var tap = new ProxyTap { Stream = true };
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        // in zwei ungleich große Stücke zerlegt: Zeilen können über Chunk-Grenzen gehen
        var bytes = Encoding.UTF8.GetBytes(Sse);
        tap.Feed(bytes.AsSpan(0, 130), t0.AddSeconds(1));
        tap.Feed(bytes.AsSpan(130, 200), t0.AddSeconds(2));
        tap.Feed(bytes.AsSpan(330), t0.AddSeconds(3));
        tap.Complete(t0.AddSeconds(4));
        Assert.AreEqual(2, tap.Reasoning);
        Assert.AreEqual(2, tap.Content);
        CollectionAssert.AreEqual(new[] { "get_weather" }, tap.Tools);
        Assert.AreEqual("tool_calls", tap.FinishReason);
        Assert.AreEqual(6, tap.PredictedN);
        Assert.IsNotNull(tap.FirstToken);
        Assert.IsTrue(tap.FirstReasoning <= tap.FirstContent);
    }

    [TestMethod]
    public void NonStream_EstimatesTokenSplitByCharacters()
    {
        var tap = new ProxyTap { Stream = false };
        var json = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"abcd\",\"reasoning_content\":\"abcdefghijkl\"," +
                   "\"tool_calls\":[{\"function\":{\"name\":\"f1\",\"arguments\":\"{}\"}},{\"function\":{\"name\":\"f2\",\"arguments\":\"{}\"}}]}}],\"usage\":{\"completion_tokens\":16}}";
        tap.Feed(Encoding.UTF8.GetBytes(json), DateTime.Now);
        tap.Complete(DateTime.Now);
        Assert.AreEqual(12, tap.Reasoning);
        Assert.AreEqual(4, tap.Content);
        CollectionAssert.AreEqual(new[] { "f1", "f2" }, tap.Tools);
        Assert.AreEqual("stop", tap.FinishReason);
    }

    [TestMethod]
    public void GarbageIsIgnored()
    {
        var tap = new ProxyTap { Stream = true };
        tap.Feed(Encoding.UTF8.GetBytes("data: {not json\n\n: comment\nevent: x\ndata: [DONE]\n"), DateTime.Now);
        tap.Complete(DateTime.Now);
        Assert.AreEqual(0, tap.Content + tap.Reasoning);
        var t2 = new ProxyTap { Stream = false };
        t2.Feed(Encoding.UTF8.GetBytes("<html>"), DateTime.Now); t2.Complete(DateTime.Now);
        Assert.IsNull(t2.FinishReason);
    }

    [TestMethod]
    public void GenerationPathsAndStreamFlag()
    {
        Assert.IsTrue(RequestProxy.IsGenerationPath("/v1/chat/completions"));
        Assert.IsTrue(RequestProxy.IsGenerationPath("/completion"));
        Assert.IsFalse(RequestProxy.IsGenerationPath("/props"));
        Assert.IsFalse(RequestProxy.IsGenerationPath("/slots"));
        Assert.IsTrue(RequestProxy.BodyWantsStream(Encoding.UTF8.GetBytes("{\"stream\":true,\"messages\":[]}")));
        Assert.IsFalse(RequestProxy.BodyWantsStream(Encoding.UTF8.GetBytes("{\"stream\":false}")));
        Assert.IsFalse(RequestProxy.BodyWantsStream(Encoding.UTF8.GetBytes("nope")));
        Assert.AreEqual(9081, RequestProxy.PickPort(8081, _ => false));
        Assert.AreEqual(9082, RequestProxy.PickPort(8081, p => p == 9081));
    }
}

[TestClass]
public class RequestEventTests
{
    private static readonly DateTime End = new(2026, 1, 1, 12, 0, 30);

    private static FinishedRequest Fin(int promptTok = 100, double promptTps = 100, int gen = 200, double genTps = 50, double secs = 5.2, int total = 100) =>
        new(1, "srv", "m", 7, 0, promptTok, promptTps, genTps, gen, secs, End, ReqStatus.Done, "Aider", total, "k");

    [TestMethod]
    public void FromLog_PhasesFromServerTimings()
    {
        var e = RequestEvent.FromLog(Fin(), "k", "m");
        Assert.AreEqual(1.0, e.PromptSec, 1e-9);          // 100 Token bei 100 t/s
        Assert.AreEqual(5.2, e.DurationSec, 1e-9);
        Assert.AreEqual(4.2, e.AnswerSec, 1e-9);
        Assert.AreEqual(0, e.ThinkSec);
        Assert.AreEqual(End - TimeSpan.FromSeconds(5.2), e.Start);
        Assert.AreEqual(0, e.CachedTokens);                // PromptTotal = ausgewertet: nichts aus dem Cache
        Assert.AreEqual("Aider", e.Client);
    }

    [TestMethod]
    public void FromLog_CachedTokensFromSlotPromptLength()
    {
        var e = RequestEvent.FromLog(Fin(promptTok: 100, total: 1100), "k", "m");
        Assert.AreEqual(1000, e.CachedTokens);
        Assert.AreEqual(1100, e.TotalPromptTokens);
        Assert.AreEqual(-1, RequestEvent.FromLog(Fin(total: 0), "k", "m").CachedTokens);   // Prompt-Länge unbekannt
    }

    [TestMethod]
    public void Merge_ProxyGivesThinkingAnswerAndTtft()
    {
        var log = RequestEvent.FromLog(Fin(), "k", "m");
        var start = End - TimeSpan.FromSeconds(6);
        var px = new ProxyRecord("k", "/v1/chat/completions", start, End, start.AddSeconds(1.5), start.AddSeconds(4.0), 120, 60,
            new[] { "read_file" }, "tool_calls", "qwen-code/1.0", true, 200, false);
        var e = RequestEvent.Merge(log, px, "k", "srv", "m");
        Assert.AreEqual(1.5, e.TtftSec, 1e-9);
        Assert.AreEqual(1.5, e.PromptSec, 1e-9);
        Assert.AreEqual(2.5, e.ThinkSec, 1e-9);
        Assert.AreEqual(2.0, e.AnswerSec, 1e-9);
        Assert.AreEqual(6.0, e.DurationSec, 1e-9);
        Assert.AreEqual(120, e.ReasoningTokens);
        Assert.AreEqual(60, e.ContentTokens);
        Assert.AreEqual(200, e.GenTokens);                 // aus dem Log
        CollectionAssert.AreEqual(new[] { "read_file" }, e.Tools);
        Assert.AreEqual("tool_calls", e.FinishReason);
        Assert.AreEqual("qwen-code/1.0", e.Client);
        Assert.IsTrue(e.FromProxy && e.LogSeen);
        Assert.AreEqual("done", e.Status);
    }

    [TestMethod]
    public void Merge_NoReasoningAndAbortedAndLength()
    {
        var start = End - TimeSpan.FromSeconds(4);
        var e = RequestEvent.Merge(null, new ProxyRecord("k", "/completion", start, End, start.AddSeconds(1), start.AddSeconds(1), 0, 30, Array.Empty<string>(), "length", "curl", true, 200, false), "k", "srv", "m");
        Assert.AreEqual(0, e.ThinkSec);
        Assert.AreEqual(3.0, e.AnswerSec, 1e-9);
        Assert.AreEqual("truncated", e.Status);
        Assert.AreEqual(30, e.GenTokens);
        var c = RequestEvent.Merge(null, new ProxyRecord("k", "/completion", start, End, null, null, -1, -1, Array.Empty<string>(), null, "", false, 200, true), "k", "srv", "m");
        Assert.AreEqual("cancelled", c.Status);
        Assert.AreEqual(4.0, c.AnswerSec, 1e-9);
    }
}

[TestClass]
public class RecordingSessionTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "slm-rec-" + Guid.NewGuid().ToString("N"));

    private static ServerWatcher Watcher(string url = "http://127.0.0.1:8095")
    {
        var info = new ServerInfo { Key = "127.0.0.1:8095", Port = 8095, Manual = true, ManualName = "test-model" };
        return new ServerWatcher(info, new HttpClient(new FakeHandler()), new FakePlatform(), () => NetSnapshot.Empty, new ClientNamer(new FakePlatform()));
    }

    [TestMethod]
    public void RecordsSamplesRequestsAndSummary_RoundTrip()
    {
        var dir = TempDir();
        try
        {
            var now = new DateTime(2026, 1, 1, 12, 0, 0);
            var w = Watcher();
            var gpu = new GpuSample("RTX", 50, 6.0, 16, 200, 300, 60, 40, 2500, 3000, 13000, 14000, 0);
            var sys = new SystemSample(10, 16, 20, 32, 30, 40);
            var session = new RecordingSession(dir, w.Key, new[] { w }, "RTX", proxy: true, now: () => now);
            for (int i = 1; i <= 10; i++) { now = now.AddSeconds(1); session.Sample(new[] { w }, gpu, sys); }
            // zwei Anfragen: eine mit Log+Proxy (zusammengeführt), eine nur aus dem Log
            var fin1 = new FinishedRequest(1, "test-model", "m", 1, 0, 200, 200, 50, 100, 3, now.AddSeconds(-5), ReqStatus.Done, "", 1200, w.Key);
            var fin2 = new FinishedRequest(2, "test-model", "m", 2, 0, 100, 100, 40, 80, 3, now, ReqStatus.Truncated, "", 100, w.Key);
            session.OnLogRequest(fin1, w.Key, "m");
            var px = new ProxyRecord(w.Key, "/v1/chat/completions", now.AddSeconds(-8), now.AddSeconds(-5), now.AddSeconds(-7), now.AddSeconds(-6), 40, 60, new[] { "tool" }, "stop", "ua", true, 200, false);
            session.OnProxyRecord(px, "test-model", "m");
            session.OnLogRequest(fin2, w.Key, "m");
            var sum = session.Stop()!;
            Assert.AreEqual(2, sum.Requests);
            Assert.AreEqual(10, sum.DurationSec, 0.01);
            Assert.AreEqual(200 * 9 / 3600.0, sum.EnergyWh, 1e-6);   // 9 Intervalle zu je 1 s bei 200 W
            Assert.AreEqual(1, sum.Truncated);
            Assert.AreEqual(1, sum.ToolCalls);
            Assert.AreEqual(180, sum.TokensOut);
            Assert.AreEqual(1300, sum.TokensIn);
            Assert.AreEqual(1000.0 / 1300.0 * 100, sum.CachePct, 0.01);
            Assert.IsTrue(sum.BusyPct > 0 && sum.BusyPct <= 100);
            Assert.IsTrue(File.Exists(RecordingStore.SummaryPath(session.File)));

            var data = RecordingStore.Load(session.File)!;
            Assert.AreEqual(10, data.Samples.Count);
            Assert.AreEqual(2, data.Requests.Count);
            Assert.AreEqual("test-model", data.Meta.ServerNames[0]);
            Assert.IsTrue(data.Meta.Proxy);
            Assert.AreEqual(6.0, data.Samples[0].Gpu!.MemUsedGb);
            Assert.AreEqual(200, data.Samples[0].Gpu!.PowerW);
            var merged = data.Requests.Single(r => r.FromProxy);
            Assert.IsTrue(merged.LogSeen);
            Assert.AreEqual(1.0, merged.TtftSec, 1e-6);
            Assert.AreEqual(40, merged.ReasoningTokens);
            CollectionAssert.AreEqual(new[] { "tool" }, merged.Tools);
            Assert.IsNotNull(data.Ended);

            // Liste und CSV
            var list = RecordingStore.List(dir);
            Assert.AreEqual(1, list.Count);
            var csv = RecordingStore.ToCsv(data);
            var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(11, lines.Length);
            Assert.IsTrue(lines[0].StartsWith("Time,ElapsedSec,Server"));
            Assert.IsTrue(lines[1].Contains("test-model"));
            Assert.AreEqual(3, RecordingStore.RequestsToCsv(data).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void UnpairedEventsAreEmittedAfterWindow_BrokenLinesSkipped()
    {
        var dir = TempDir();
        try
        {
            var now = new DateTime(2026, 1, 1, 12, 0, 0);
            var w = Watcher();
            var session = new RecordingSession(dir, "all", new[] { w }, "g", false, () => now);
            session.OnLogRequest(new FinishedRequest(1, "n", "m", 1, 0, 10, 10, 10, 10, 1, now, ReqStatus.Done, "", 0, w.Key), w.Key, "m");
            session.Sample(new[] { w }, null, null);
            Assert.AreEqual(0, session.Requests);                      // wartet noch auf einen Proxy-Partner
            now = now.AddSeconds(5);
            session.Sample(new[] { w }, null, null);
            Assert.AreEqual(1, session.Requests);                      // Fenster abgelaufen: allein ausgegeben
            session.Stop();
            File.AppendAllText(session.File, "{ broken json\n");
            Assert.AreEqual(1, RecordingStore.Load(session.File)!.Requests.Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void ManagerRoutesEventsToMatchingSessionsOnly()
    {
        var dir = TempDir();
        try
        {
            var w = Watcher();
            using var mgr = new RecordingManager(dir);
            var a = mgr.Start(w.Key, new[] { w }, "g", false);
            Assert.IsTrue(mgr.IsRecording(w.Key));
            Assert.IsFalse(mgr.IsRecording("other:1"));
            Assert.IsTrue(mgr.Active);
            var sum = mgr.Stop(a)!;
            Assert.IsFalse(mgr.Active);
            Assert.IsTrue(File.Exists(sum.File));
            var g = mgr.Start("all", new[] { w }, "g", false);
            Assert.IsTrue(mgr.IsRecording("anything"));   // global zeichnet alles auf
            mgr.StopAll();
            Assert.AreEqual(2, RecordingStore.List(dir).Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

// Proxy gegen einen kleinen lokalen Server, der wie llama-server antwortet (nur Loopback, hohe Testports)
[TestClass]
public class RequestProxyIntegrationTests
{
    private static async Task<HttpListener> StartFakeUpstream(int port, Func<HttpListenerContext, Task> handler)
    {
        var l = new HttpListener();
        l.Prefixes.Add($"http://127.0.0.1:{port}/");
        l.Start();
        _ = Task.Run(async () =>
        {
            while (l.IsListening)
            {
                HttpListenerContext c;
                try { c = await l.GetContextAsync(); } catch { break; }
                _ = Task.Run(async () => { try { await handler(c); } catch { } finally { try { c.Response.Close(); } catch { } } });
            }
        });
        await Task.Delay(50);
        return l;
    }

    [TestMethod]
    public async Task PassesThroughStreamingAndRecordsNumbersOnly()
    {
        const int up = 18461, px = 19461;
        // Nur Anfragen dieses Tests zählen (User-Agent): fremde Proben auf offene Ports (Server-Erkennung eines laufenden
        // StykkerLLM o. Ä.) erreichen über den Proxy ebenfalls diesen Server und dürfen die Werte nicht überschreiben.
        var seen = new System.Collections.Concurrent.ConcurrentQueue<(string Path, string? Auth, string Body)>();
        using var upstream = await StartFakeUpstream(up, async c =>
        {
            using var sr = new StreamReader(c.Request.InputStream);
            var reqBody = await sr.ReadToEndAsync();
            if (c.Request.UserAgent?.StartsWith("my-client/2.0", StringComparison.Ordinal) == true)
                seen.Enqueue((c.Request.Url!.AbsolutePath, c.Request.Headers["Authorization"], reqBody));
            if (c.Request.Url!.AbsolutePath == "/props") { var b = Encoding.UTF8.GetBytes("{\"build_info\":\"x\"}"); c.Response.ContentType = "application/json"; c.Response.ContentLength64 = b.Length; await c.Response.OutputStream.WriteAsync(b); return; }
            c.Response.ContentType = "text/event-stream";
            c.Response.SendChunked = true;
            foreach (var line in new[]
            {
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"hm\"}}]}\n\n",
                "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n",
                "data: {\"choices\":[{\"finish_reason\":\"stop\",\"delta\":{}}]}\n\n", "data: [DONE]\n\n",
            })
            {
                var bytes = Encoding.UTF8.GetBytes(line);
                await c.Response.OutputStream.WriteAsync(bytes);
                await c.Response.OutputStream.FlushAsync();
                await Task.Delay(30);
            }
        });
        using var proxy = new RequestProxy("127.0.0.1:" + up, $"http://127.0.0.1:{up}", px);
        var records = new List<ProxyRecord>();
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Recorded += r => { lock (records) records.Add(r); recorded.TrySetResult(); };
        proxy.Start();
        using var client = new HttpClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer abc");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("my-client/2.0");

        var props = await client.GetStringAsync($"http://127.0.0.1:{px}/props");
        Assert.IsTrue(props.Contains("build_info"));
        Assert.AreEqual("Bearer abc", seen.Single(x => x.Path == "/props").Auth);   // Header unverändert durchgereicht
        lock (records) Assert.AreEqual(0, records.Count);              // /props wird nicht aufgezeichnet

        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{px}/v1/chat/completions")
        { Content = new StringContent("{\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"SECRET PROMPT\"}]}", Encoding.UTF8, "application/json") };
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.IsTrue(text.Contains("\"content\":\"Hi\"") && text.Contains("[DONE]"));    // Stream unverändert
        Assert.IsTrue(seen.Single(x => x.Path == "/v1/chat/completions").Body.Contains("SECRET PROMPT"));   // Anfrage unverändert beim Server
        // Die Aufzeichnung kommt erst nach dem Schließen der Verbindung (finally im Proxy): auf das Ereignis warten, großzügig unter Last
        await recorded.Task.WaitAsync(TimeSpan.FromSeconds(30));
        ProxyRecord r;
        lock (records) r = records.Single();
        Assert.AreEqual(1, r.ReasoningTokens);
        Assert.AreEqual(1, r.ContentTokens);
        Assert.AreEqual("stop", r.FinishReason);
        Assert.AreEqual("my-client/2.0", r.UserAgent);
        Assert.IsTrue(r.Stream);
        Assert.IsNotNull(r.FirstTokenAt);
        Assert.IsTrue(r.End > r.Start);
        Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(r).Contains("SECRET"));   // keine Inhalte in der Aufzeichnung
    }

    [TestMethod]
    public async Task UpstreamDown_Gives502_ProxyKeepsRunning()
    {
        const int px = 19462;
        using var proxy = new RequestProxy("k", "http://127.0.0.1:18462", px);
        proxy.Start();
        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://127.0.0.1:{px}/props");
        Assert.AreEqual(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.IsTrue(proxy.Running);
    }
}
