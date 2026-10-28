using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N102 – Failover im Stykker-Proxy: Ersatzziele für das Standardziel (offline, abgestürzt, 503, Kontext zu klein),
// Warten auf den Neustart eines abgestürzten Ziels, dasselbe für die Anthropic-Brücke, Zähler „rescued“, Einstellungen.
[TestClass]
public class N102_FailoverTests
{
    private const string Chat = "{\"model\":\"stykker\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

    // Ein Backend, das seinen Namen zurückgibt
    private static RawUpstream Echo(string id)
    {
        var up = new RawUpstream();
        up.Handler = async (req, s, ct) =>
        {
            var payload = $"{{\"id\":\"{id}\",\"choices\":[{{\"message\":{{\"role\":\"assistant\",\"content\":\"{id}\"}},\"finish_reason\":\"stop\"}}]}}";
            await RawUpstream.Send(s, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(payload)}\r\nConnection: close\r\n\r\n{payload}", ct);
        };
        return up;
    }

    // Ein abgestürzter Server: nimmt die Verbindung an und schließt sie sofort (der Client bekommt keine Antwort)
    private static RawUpstream Crashed() => new() { Handler = (_, _, _) => Task.CompletedTask };

    // Ein Server mit fester Antwort (Status und JSON)
    private static RawUpstream Fixed(int status, string reason, string json)
    {
        var up = new RawUpstream();
        up.Handler = async (req, s, ct) =>
            await RawUpstream.Send(s, $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(json)}\r\nConnection: close\r\n\r\n{json}", ct);
        return up;
    }

    private static async Task<(int Status, string Body)> Post(int port, string path, string json)
    {
        using var http = new HttpClient();
        var resp = await http.PostAsync($"http://127.0.0.1:{port}{path}", new StringContent(json, Encoding.UTF8, "application/json"));
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    private static RouterProxy Router(params ProxyTarget[] targets)
    {
        var proxy = new RouterProxy(0) { RetryPause = TimeSpan.FromMilliseconds(50), RestartWait = TimeSpan.FromSeconds(5) };
        proxy.SetTargets(targets, targets[0].Key);
        return proxy;
    }

    private static ProxyTarget T(string key, RawUpstream up, string model, bool ready = true, int? ctx = null) =>
        new(key, up.Url, model, BackendKind.LlamaCpp, ready, false, ServerKey: "srv-" + key, DisplayName: model, ContextLength: ctx);

    // Die Beobachtung der nächsten Anfrage (sie kommt erst nach dem Schließen der Verbindung)
    private static Task<ProxyRecord> NextRecord(RouterProxy proxy)
    {
        var tcs = new TaskCompletionSource<ProxyRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Recorded += r => tcs.TrySetResult(r);
        return tcs.Task;
    }

    private static async Task<ProxyRecord> Wait(Task<ProxyRecord> t)
    {
        var done = await Task.WhenAny(t, Task.Delay(5000));
        Assert.AreSame(t, done, "no proxy record");
        return await t;
    }

    [TestMethod]
    public async Task CrashedDefault_GoesToTheFallback_AndIsRecordedAsFallback()
    {
        using var dead = Crashed();
        using var b = Echo("B");
        using var proxy = Router(T("ka", dead, "alpha"), T("kb", b, "beta"));
        proxy.SetFallbacks(new[] { "kb" });
        proxy.Start();
        var rec = NextRecord(proxy);

        var (status, body) = await Post(proxy.ListenPort, "/v1/chat/completions", Chat);
        Assert.AreEqual(200, status);
        StringAssert.Contains(body, "\"B\"");
        var r = await Wait(rec);
        Assert.AreEqual(ProxyRoutes.Fallback, r.Route);
        Assert.AreEqual("srv-kb", r.ServerKey);
        Assert.AreEqual(200, r.HttpStatus);
        Assert.IsTrue(proxy.IsDown("ka"), "a crashed target is marked down");
    }

    [TestMethod]
    public async Task DownTarget_IsSkipped_ForTheNextRequest()
    {
        using var dead = Crashed();
        using var b = Echo("B");
        using var proxy = Router(T("ka", dead, "alpha"), T("kb", b, "beta"));
        proxy.SetFallbacks(new[] { "kb" });
        proxy.Start();
        await Post(proxy.ListenPort, "/v1/chat/completions", Chat);
        int before = Volatile.Read(ref dead.Connections);

        var (status, body) = await Post(proxy.ListenPort, "/v1/chat/completions", Chat);
        Assert.AreEqual(200, status);
        StringAssert.Contains(body, "\"B\"");
        Assert.AreEqual(before, Volatile.Read(ref dead.Connections), "the down target gets no new connection");
    }

    [TestMethod]
    public async Task OfflineDefault_UsesTheFallbackBeforeSending()
    {
        using var a = Echo("A");
        using var b = Echo("B");
        using var proxy = Router(T("ka", a, "alpha", ready: false), T("kb", b, "beta"));
        proxy.SetFallbacks(new[] { "kb" });
        proxy.Start();
        var rec = NextRecord(proxy);

        var (status, body) = await Post(proxy.ListenPort, "/v1/chat/completions", Chat);
        Assert.AreEqual(200, status);
        StringAssert.Contains(body, "\"B\"");
        Assert.AreEqual(0, Volatile.Read(ref a.Connections));
        Assert.AreEqual(ProxyRoutes.Fallback, (await Wait(rec)).Route);
    }

    [TestMethod]
    public async Task OfflineDefault_WithoutFallback_StillAnswers503()
    {
        using var a = Echo("A");
        using var proxy = Router(T("ka", a, "alpha", ready: false));
        proxy.Start();
        var (status, _) = await Post(proxy.ListenPort, "/v1/chat/completions", Chat);
        Assert.AreEqual(503, status);
    }

    [TestMethod]
    public async Task TooSmallContext_GoesToAFallbackThatFits()
    {
        using var small = Echo("SMALL");
        using var tiny = Echo("TINY");
        using var big = Echo("BIG");
        using var proxy = Router(T("ks", small, "small", ctx: 100), T("kt", tiny, "tiny", ctx: 200), T("kg", big, "big", ctx: 100_000));
        proxy.SetFallbacks(new[] { "kt", "kg" });   // der erste Ersatz ist auch zu klein und wird übersprungen
        proxy.Start();
        string longText = new('x', 6000);   // Untergrenze 1000 Token
        var (status, body) = await Post(proxy.ListenPort, "/v1/chat/completions",
            "{\"model\":\"stykker\",\"messages\":[{\"role\":\"user\",\"content\":\"" + longText + "\"}]}");
        Assert.AreEqual(200, status);
        StringAssert.Contains(body, "\"BIG\"");
        Assert.AreEqual(0, Volatile.Read(ref small.Connections));
        Assert.AreEqual(0, Volatile.Read(ref tiny.Connections));
    }

    [TestMethod]
    public async Task BackendContextOverflow_IsRetriedOnABiggerFallback()
    {
        const string overflow = "{\"error\":{\"code\":400,\"message\":\"the request exceeds the available context size, try increasing it\",\"type\":\"exceed_context_size_error\"}}";
        using var a = Fixed(400, "Bad Request", overflow);
        using var b = Echo("B");
        using var proxy = Router(T("ka", a, "alpha"), T("kb", b, "beta"));
        proxy.SetFallbacks(new[] { "kb" });
        proxy.Start();
        var (status, body) = await Post(proxy.ListenPort, "/v1/chat/completions", Chat);
        Assert.AreEqual(200, status);
        StringAssert.Contains(body, "\"B\"");
        Assert.IsFalse(proxy.IsDown("ka"), "a context overflow does not mark the target down");
    }

    [TestMethod]
    public async Task BackendContextOverflow_WithoutFallback_PassesTheErrorUnchanged()
    {
        const string overflow = "{\"error\":{\"code\":400,\"message\":\"the request exceeds the available context size, try increasing it\",\"type\":\"exceed_context_size_error\"}}";
        using var a = Fixed(400, "Bad Request", overflow);
        using var proxy = Router(T("ka", a, "alpha"));
        proxy.Start();
        var (status, body) = await Post(proxy.ListenPort, "/v1/chat/completions", Chat);
        Assert.AreEqual(400, status);
        Assert.AreEqual(overflow, body, "the error text the proxy read must still reach the client in full");
    }

    [TestMethod]
    public async Task OtherBadRequest_IsNotRerouted()
    {
        const string bad = "{\"error\":{\"message\":\"invalid tool schema\"}}";
        using var a = Fixed(400, "Bad Request", bad);
        using var b = Echo("B");
        using var proxy = Router(T("ka", a, "alpha"), T("kb", b, "beta"));
        proxy.SetFallbacks(new[] { "kb" });
        proxy.Start();
        var (status, body) = await Post(proxy.ListenPort, "/v1/chat/completions", Chat);
        Assert.AreEqual(400, status);
        Assert.AreEqual(bad, body);
        Assert.AreEqual(0, Volatile.Read(ref b.Connections));
    }

    [TestMethod]
    public async Task Loading503_GoesToTheFallback()
    {
        using var a = Fixed(503, "Service Unavailable", "{\"error\":{\"code\":503,\"message\":\"Loading model\",\"type\":\"unavailable_error\"}}");
        using var b = Echo("B");
        using var proxy = Router(T("ka", a, "alpha"), T("kb", b, "beta"));
        proxy.SetFallbacks(new[] { "kb" });
        proxy.Start();
        var (status, body) = await Post(proxy.ListenPort, "/v1/chat/completions", Chat);
        Assert.AreEqual(200, status);
        StringAssert.Contains(body, "\"B\"");
    }

    [TestMethod]
    public async Task CrashedTarget_WithoutFallback_IsSentAgainAfterItsRestart()
    {
        // Die ersten beiden Verbindungen brechen ab (Absturz), danach antwortet der Server wieder
        int calls = 0;
        using var a = new RawUpstream();
        a.Handler = async (req, s, ct) =>
        {
            if (Interlocked.Increment(ref calls) <= 2) return;
            const string payload = "{\"id\":\"A\"}";
            await RawUpstream.Send(s, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n{payload}", ct);
        };
        using var proxy = Router(T("ka", a, "alpha"));
        proxy.Start();
        var rec = NextRecord(proxy);

        var (status, body) = await Post(proxy.ListenPort, "/v1/chat/completions", Chat);
        Assert.AreEqual(200, status);
        StringAssert.Contains(body, "\"A\"");
        Assert.AreEqual(3, Volatile.Read(ref calls));
        var r = await Wait(rec);
        Assert.AreEqual(ProxyRoutes.Retry, r.Route);
        Assert.IsFalse(proxy.IsDown("ka"), "a target that answered again is no longer down");
    }

    [TestMethod]
    public async Task RestartWaitOff_AnswersAtOnceWith502()
    {
        using var dead = Crashed();
        using var proxy = Router(T("ka", dead, "alpha"));
        proxy.RestartWait = TimeSpan.Zero;
        proxy.Start();
        var (status, _) = await Post(proxy.ListenPort, "/v1/chat/completions", Chat);
        Assert.AreEqual(502, status);
        Assert.AreEqual(1, Volatile.Read(ref dead.Connections));
    }

    [TestMethod]
    public async Task RestartWait_GivesUpAfterItsTime()
    {
        using var dead = Crashed();
        using var proxy = Router(T("ka", dead, "alpha"));
        proxy.RestartWait = TimeSpan.FromMilliseconds(400);
        proxy.Start();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (status, _) = await Post(proxy.ListenPort, "/v1/chat/completions", Chat);
        Assert.AreEqual(502, status);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(4), "waited " + watch.Elapsed);
        Assert.IsTrue(Volatile.Read(ref dead.Connections) >= 2, "it tried again at least once");
    }

    [TestMethod]
    public async Task NamedModel_DoesNotJumpToAFallback()
    {
        using var dead = Crashed();
        using var b = Echo("B");
        using var proxy = Router(T("ka", dead, "alpha"), T("kb", b, "beta"));
        proxy.SetFallbacks(new[] { "kb" });
        proxy.RestartWait = TimeSpan.Zero;
        proxy.Start();
        var (status, _) = await Post(proxy.ListenPort, "/v1/chat/completions", "{\"model\":\"alpha\",\"messages\":[]}");
        Assert.AreEqual(502, status, "the client asked for alpha by name; another model would surprise it");
        Assert.AreEqual(0, Volatile.Read(ref b.Connections));
    }

    [TestMethod]
    public async Task AnthropicBridge_CrashedDefault_IsAnsweredByTheFallback()
    {
        using var dead = Crashed();
        string? sentModel = null;
        using var b = new RawUpstream();
        b.Handler = async (req, s, ct) =>
        {
            sentModel = RouterProxy.ReadModel(req.Body);
            const string payload = "{\"id\":\"x\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hello from B\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4}}";
            await RawUpstream.Send(s, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n{payload}", ct);
        };
        using var proxy = Router(T("ka", dead, "alpha"), T("kb", b, "beta"));
        proxy.SetFallbacks(new[] { "kb" });
        proxy.Start();
        var rec = NextRecord(proxy);

        var (status, body) = await Post(proxy.ListenPort, "/v1/messages",
            "{\"model\":\"claude-stykker\",\"max_tokens\":64,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");
        Assert.AreEqual(200, status);
        StringAssert.Contains(body, "hello from B");
        StringAssert.Contains(body, "\"type\":\"message\"");
        Assert.AreEqual("beta", sentModel, "the fallback gets its own model name");
        var r = await Wait(rec);
        Assert.AreEqual(ProxyRoutes.Fallback, r.Route);
        Assert.AreEqual("srv-kb", r.ServerKey);
    }

    [TestMethod]
    public async Task AnthropicBridge_WithoutFallback_WritesTheErrorOnce()
    {
        using var dead = Crashed();
        using var proxy = Router(T("ka", dead, "alpha"));
        proxy.RestartWait = TimeSpan.Zero;
        proxy.Start();
        var (status, body) = await Post(proxy.ListenPort, "/v1/messages",
            "{\"model\":\"claude-stykker\",\"max_tokens\":64,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");
        Assert.AreEqual(502, status);
        StringAssert.Contains(body, "\"type\":\"error\"");
    }

    [TestMethod]
    public void FallbackCandidates_SkipNotReady_Down_TooSmall_AndCloudWithoutKey()
    {
        using var u = Echo("U");
        var proxy = new RouterProxy(0);
        var cloud = new ProxyTarget(ProxyTarget.CloudKey("https://cloud.example/v1", "m"), "https://cloud.example/v1", "m", BackendKind.LmStudio, true, false, Cloud: true);
        var targets = new[]
        {
            T("off", u, "off", ready: false),
            T("small", u, "small", ctx: 10),
            cloud,
            T("ok", u, "ok"),
        };
        proxy.SetTargets(targets, "off");
        proxy.SetFallbacks(new[] { "off", "small", cloud.Key, "ok", "ok", "" });
        CollectionAssert.AreEqual(new[] { "off", "small", cloud.Key, "ok" }, proxy.FallbackKeys.ToArray(), "duplicates and empty keys are dropped");
        var body = Encoding.UTF8.GetBytes("{\"messages\":[{\"role\":\"user\",\"content\":\"" + new string('y', 600) + "\"}]}");
        var picked = proxy.FallbackCandidates(targets, Array.Empty<string>(), body).Select(t => t.Key).ToArray();
        CollectionAssert.AreEqual(new[] { "ok" }, picked, "cloud without a key, offline and too-small targets cannot take it");

        proxy.AuthFor = _ => "secret";
        picked = proxy.FallbackCandidates(targets, Array.Empty<string>(), body).Select(t => t.Key).ToArray();
        CollectionAssert.AreEqual(new[] { cloud.Key, "ok" }, picked);
    }

    [TestMethod]
    public void RouteCounts_CountOnlySuccessfulRescues()
    {
        var c = new ProxyRouteCounts();
        ProxyRecord Rec(string? route, int status) => new("k", "/v1/chat/completions", DateTime.Now, DateTime.Now, null, null, 0, 1,
            Array.Empty<string>(), "stop", "ua", false, status, false, "stykker", null, route);
        c.Add(Rec(ProxyRoutes.Fallback, 200));
        c.Add(Rec(ProxyRoutes.Retry, 200));
        c.Add(Rec(ProxyRoutes.Fallback, 502));
        c.Add(Rec(ProxyRoutes.Default, 200));
        Assert.AreEqual(2, c.Rescued);
        Assert.AreEqual("fallback", ProxyRoutes.Label(ProxyRoutes.Fallback));
        Assert.AreEqual("retry", ProxyRoutes.Label(ProxyRoutes.Retry));
        Assert.IsTrue(ProxyRoutes.Tip(ProxyRoutes.Fallback).Length > 0);
    }

    [TestMethod]
    public void Manager_StoresFallbacks_Compacted_AndWithoutDuplicates()
    {
        var settings = new AppSettings();
        using var pm = new ProxyManager(() => Array.Empty<ServerWatcher>(), settings, _ => { });
        pm.SetFallbackValue(0, "srv1|m1");
        pm.SetFallbackValue(1, "srv2|m2");
        CollectionAssert.AreEqual(new[] { "srv1|m1", "srv2|m2" }, pm.FallbackValues().ToArray());
        pm.SetFallbackValue(1, "srv1|m1");   // dasselbe Ziel steht nur einmal in der Liste
        CollectionAssert.AreEqual(new[] { "srv1|m1" }, pm.FallbackValues().ToArray());
        pm.SetFallbackValue(1, "srv2|m2");
        pm.SetFallbackValue(0, "");          // Platz 1 frei: Platz 2 rückt nach
        CollectionAssert.AreEqual(new[] { "srv2|m2" }, pm.FallbackValues().ToArray());
        Assert.AreEqual("srv2|m2", pm.FallbackValue(0));
        Assert.AreEqual("", pm.FallbackValue(1));
        pm.SetFallbackValue(5, "ignored");
        Assert.AreEqual(1, settings.ProxyFallbacks.Count);
        Assert.IsTrue(pm.RestartWait, "waiting for a crashed model is on by default");
        pm.SetRestartWait(false);
        Assert.IsFalse(settings.ProxyRestartWait);
    }

    [TestMethod]
    public void Manager_ResolvesFallbackValues_ToTargetKeys()
    {
        using var u = Echo("U");
        var targets = new[]
        {
            new ProxyTarget("srv1|m1", u.Url, "m1", BackendKind.LlamaCpp, true, false, ServerKey: "srv1"),
            new ProxyTarget(ProxyTarget.CloudKey("https://cloud.example/v1", "c"), "https://cloud.example/v1", "c", BackendKind.LmStudio, true, false, Cloud: true),
        };
        var keys = ProxyManager.FallbackKeys(new[] { "srv1|m1", ProxyTarget.CloudKey("https://cloud.example/v1", "c"), "gone|x", "srv1" }, targets);
        CollectionAssert.AreEqual(new[] { "srv1|m1", ProxyTarget.CloudKey("https://cloud.example/v1", "c") }, keys.ToArray(),
            "a server that is not running drops out; a value without a model names the same target once");
    }

    [TestMethod]
    public async Task CloudRateLimit_GoesToTheFallback_ButALocal500IsPassedOn()
    {
        using var cloudUp = Fixed(429, "Too Many Requests", "{\"error\":{\"message\":\"rate limit\"}}");
        using var b = Echo("B");
        var cloud = new ProxyTarget(ProxyTarget.CloudKey(cloudUp.Url, "big"), cloudUp.Url, "big", BackendKind.LmStudio, true, false, Cloud: true, DisplayName: "cloud");
        using var proxy = Router(cloud, T("kb", b, "beta"));
        proxy.AuthFor = _ => "secret";
        proxy.SetFallbacks(new[] { "kb" });
        proxy.Start();
        var (status, body) = await Post(proxy.ListenPort, "/v1/chat/completions", Chat);
        Assert.AreEqual(200, status);
        StringAssert.Contains(body, "\"B\"");
        Assert.IsTrue(proxy.IsDown(cloud.Key), "a rate-limited provider is skipped for a while");

        using var local = Fixed(500, "Internal Server Error", "{\"error\":{\"message\":\"Failed to parse tool call\"}}");
        using var proxy2 = Router(T("ka", local, "alpha"), T("kb", b, "beta"));
        proxy2.SetFallbacks(new[] { "kb" });
        proxy2.Start();
        var (status2, body2) = await Post(proxy2.ListenPort, "/v1/chat/completions", Chat);
        Assert.AreEqual(500, status2, "a local 500 is an error of the request or the model, not an outage");
        StringAssert.Contains(body2, "Failed to parse tool call");
    }

    [TestMethod]
    public async Task Health_ReportsTheDefaultAndTheFallbacks()
    {
        using var a = Echo("A");
        using var b = Echo("B");
        using var proxy = Router(T("ka", a, "alpha", ctx: 32768), T("kb", b, "beta"));
        proxy.SetFallbacks(new[] { "kb" });
        proxy.Start();
        using var http = new HttpClient();

        var ok = await http.GetAsync($"http://127.0.0.1:{proxy.ListenPort}/health");
        var okJson = await ok.Content.ReadAsStringAsync();
        Assert.AreEqual(200, (int)ok.StatusCode);
        StringAssert.Contains(okJson, "\"status\":\"ok\"");
        StringAssert.Contains(okJson, "\"context_length\":32768");
        StringAssert.Contains(okJson, "\"name\":\"beta\"");
        Assert.AreEqual(0, Volatile.Read(ref a.Connections), "the proxy answers /health itself");

        // Standardziel offline, Ersatzziel bereit: noch 200, Status „fallback“
        proxy.SetTargets(new[] { T("ka", a, "alpha", ready: false), T("kb", b, "beta") }, "ka");
        var fb = await http.GetAsync($"http://127.0.0.1:{proxy.ListenPort}/health");
        Assert.AreEqual(200, (int)fb.StatusCode);
        StringAssert.Contains(await fb.Content.ReadAsStringAsync(), "\"status\":\"fallback\"");

        // nichts nimmt an: 503 mit Fehlerobjekt
        proxy.SetTargets(new[] { T("ka", a, "alpha", ready: false), T("kb", b, "beta", ready: false) }, "ka");
        var down = await http.GetAsync($"http://127.0.0.1:{proxy.ListenPort}/health");
        var downJson = await down.Content.ReadAsStringAsync();
        Assert.AreEqual(503, (int)down.StatusCode);
        StringAssert.Contains(downJson, "\"status\":\"unavailable\"");
        StringAssert.Contains(downJson, "unavailable_error");
    }

    [TestMethod]
    public async Task PrefixedStream_ReturnsThePrefixThenTheRest()
    {
        using var inner = new MemoryStream(Encoding.ASCII.GetBytes("world"));
        using var s = new RequestProxy.PrefixedStream(Encoding.ASCII.GetBytes("hello "), inner);
        using var reader = new StreamReader(s);
        Assert.AreEqual("hello world", await reader.ReadToEndAsync());
    }
}
