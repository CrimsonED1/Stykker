using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Kleiner Server auf Rohsockets (Loopback, freier Port): der Test steuert jedes Byte und jede Pause der Antwort
internal sealed class RawUpstream : IDisposable
{
    internal sealed record Req(string Head, byte[] Body)
    {
        public string Line1 => Head.Split("\r\n")[0];
        public string? Header(string name) => Head.Split("\r\n").Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => p[1].Trim()).FirstOrDefault();
    }

    private readonly TcpListener _l = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}";
    public int Connections;
    public Func<Req, NetworkStream, CancellationToken, Task> Handler { get; set; } = (_, _, _) => Task.CompletedTask;

    public RawUpstream()
    {
        _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { break; }
                Interlocked.Increment(ref Connections);
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        try
                        {
                            var s = c.GetStream();
                            var req = await ReadAsync(s, _cts.Token);
                            if (req != null) await Handler(req, s, _cts.Token);
                        }
                        catch { }
                    }
                });
            }
        });
    }

    private static async Task<Req?> ReadAsync(NetworkStream s, CancellationToken ct)
    {
        var ms = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one, ct) <= 0) return null;
            ms.WriteByte(one[0]);
            var b = ms.GetBuffer();
            int n = (int)ms.Length;
            if (n >= 4 && b[n - 4] == '\r' && b[n - 3] == '\n' && b[n - 2] == '\r' && b[n - 1] == '\n') break;
        }
        var head = Encoding.Latin1.GetString(ms.ToArray(), 0, (int)ms.Length - 4);
        int len = 0;
        foreach (var l in head.Split("\r\n").Skip(1))
            if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) len = int.Parse(l[15..].Trim());
        var body = new byte[len];
        int got = 0;
        while (got < len) { int n = await s.ReadAsync(body.AsMemory(got), ct); if (n <= 0) break; got += n; }
        return new Req(head, body);
    }

    public static Task Send(NetworkStream s, string text, CancellationToken ct = default) => s.WriteAsync(Encoding.UTF8.GetBytes(text), ct).AsTask();

    public void Dispose() { _cts.Cancel(); _l.Stop(); }
}

[TestClass]
public class ProxyHardeningTests
{
    private static RequestProxy StartProxy(string upstreamUrl, List<ProxyRecord>? records = null)
    {
        var p = new RequestProxy("k", upstreamUrl, 0);
        if (records != null) p.Recorded += r => { lock (records) records.Add(r); };
        p.Start();
        return p;
    }

    private static async Task<string> RawExchange(int port, string request, int readMs = 5000)
    {
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, port);
        var s = c.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes(request));
        var sb = new StringBuilder();
        var buf = new byte[4096];
        using var cts = new CancellationTokenSource(readMs);
        try
        {
            while (true)
            {
                int n = await s.ReadAsync(buf, cts.Token);
                if (n <= 0) break;
                sb.Append(Encoding.Latin1.GetString(buf, 0, n));
            }
        }
        catch { }
        return sb.ToString();
    }

    private static async Task WaitUntil(Func<bool> cond, int ms = 5000)
    {
        var end = DateTime.Now.AddMilliseconds(ms);
        while (!cond() && DateTime.Now < end) await Task.Delay(25);
    }

    [TestMethod]
    public void Listens_OnLoopbackOnly()
    {
        using var proxy = StartProxy("http://127.0.0.1:1");
        var ends = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(e => e.Port == proxy.ListenPort).ToList();
        Assert.IsTrue(ends.Count >= 1);
        Assert.IsTrue(ends.All(e => IPAddress.IsLoopback(e.Address)), string.Join(",", ends));
    }

    [TestMethod]
    public async Task NonLoopbackAddressIsRefused()
    {
        using var proxy = StartProxy("http://127.0.0.1:1");
        var addrs = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254")).ToList();
        if (addrs.Count == 0) Assert.Inconclusive("no non-loopback IPv4 address on this machine");
        foreach (var a in addrs)
        {
            using var c = new TcpClient();
            using var cts = new CancellationTokenSource(3000);
            bool connected;
            try { await c.ConnectAsync(a, proxy.ListenPort, cts.Token); connected = true; } catch { connected = false; }
            Assert.IsFalse(connected, $"proxy port reachable via {a}");
        }
    }

    [TestMethod]
    public async Task ForeignHostHeaderIsRejected_LoopbackNamesAreAccepted()
    {
        using var up = new RawUpstream();
        up.Handler = async (_, s, ct) => await RawUpstream.Send(s, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok", ct);
        using var proxy = StartProxy(up.Url);
        var bad = await RawExchange(proxy.ListenPort, "GET /props HTTP/1.1\r\nHost: evil.example.com\r\nConnection: close\r\n\r\n");
        StringAssert.StartsWith(bad, "HTTP/1.1 403");
        Assert.AreEqual(0, up.Connections);
        foreach (var host in new[] { "127.0.0.1:9", "localhost:9", "[::1]:9", "app.localhost" })
        {
            var ok = await RawExchange(proxy.ListenPort, $"GET /props HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
            StringAssert.StartsWith(ok, "HTTP/1.1 200", host);
        }
        Assert.IsFalse(RequestProxy.IsLoopbackHost("127.0.0.1.evil.com"));
        Assert.IsFalse(RequestProxy.IsLoopbackHost("192.168.1.5:8080"));
    }

    [TestMethod]
    public async Task ClientAbortDuringPromptProcessing_CancelsUpstream()
    {
        using var up = new RawUpstream();
        bool upstreamSawClose = false, handlerEntered = false;
        up.Handler = async (_, s, ct) =>
        {
            handlerEntered = true;
            // "Prompt wird verarbeitet": keine Antwort; solange warten, bis die Verbindung vom Proxy beendet wird
            var one = new byte[1];
            try { while (await s.ReadAsync(one, ct) > 0) { } upstreamSawClose = true; } catch { upstreamSawClose = true; }
        };
        var records = new List<ProxyRecord>();
        using var proxy = StartProxy(up.Url, records);
        using (var c = new TcpClient())
        {
            await c.ConnectAsync(IPAddress.Loopback, proxy.ListenPort);
            var body = "{\"stream\":true,\"messages\":[]}";
            await c.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"POST /v1/chat/completions HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}"));
            await WaitUntil(() => handlerEntered);
            Assert.IsTrue(handlerEntered);
            await Task.Delay(200);
        }   // Verbindung schließen = Nutzer bricht im Chat ab
        await WaitUntil(() => upstreamSawClose);
        Assert.IsTrue(upstreamSawClose, "upstream request was not aborted");
        await WaitUntil(() => records.Count > 0);
        Assert.IsTrue(records.Single().ClientAborted);
        await WaitUntil(() => proxy.ActiveRequests == 0);
        Assert.AreEqual(0, proxy.ActiveRequests);
    }

    [TestMethod]
    public async Task LargeBody_IsForwardedIntact()
    {
        using var up = new RawUpstream();
        up.Handler = async (r, s, ct) =>
        {
            var hash = Convert.ToHexString(SHA256.HashData(r.Body));
            await RawUpstream.Send(s, $"HTTP/1.1 200 OK\r\nX-Hash: {hash}\r\nX-Len: {r.Body.Length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct);
        };
        using var proxy = StartProxy(up.Url);
        var data = new byte[5 * 1024 * 1024 + 123];
        new Random(7).NextBytes(data);
        using var http = new HttpClient();
        using var resp = await http.PostAsync($"http://127.0.0.1:{proxy.ListenPort}/upload", new ByteArrayContent(data));
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        Assert.AreEqual(data.Length.ToString(), resp.Headers.GetValues("X-Len").Single());
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(data)), resp.Headers.GetValues("X-Hash").Single());
    }

    [TestMethod]
    public async Task ChunkedRequestBody_IsDecodedAndForwarded()
    {
        using var up = new RawUpstream();
        string? seen = null;
        up.Handler = async (r, s, ct) =>
        {
            seen = Encoding.UTF8.GetString(r.Body);
            await RawUpstream.Send(s, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct);
        };
        using var proxy = StartProxy(up.Url);
        var res = await RawExchange(proxy.ListenPort,
            "POST /x HTTP/1.1\r\nHost: 127.0.0.1\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n");
        StringAssert.StartsWith(res, "HTTP/1.1 200");
        Assert.AreEqual("hello world", seen);
    }

    [TestMethod]
    public async Task BodyOverLimit_Gets413_AndNothingReachesUpstream()
    {
        using var up = new RawUpstream();
        using var proxy = StartProxy(up.Url);
        var res = await RawExchange(proxy.ListenPort,
            $"POST /v1/chat/completions HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: {RequestProxy.MaxBodyBytes + 1}\r\n\r\nabc");
        StringAssert.StartsWith(res, "HTTP/1.1 413");
        Assert.AreEqual(0, up.Connections);
        Assert.IsTrue(proxy.Running);
    }

    [TestMethod]
    public async Task SseChunkArrivesBeforeTheStreamEnds()
    {
        using var up = new RawUpstream();
        var gate = new TaskCompletionSource();
        up.Handler = async (_, s, ct) =>
        {
            await RawUpstream.Send(s, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n", ct);
            await RawUpstream.Send(s, "data: {\"choices\":[{\"delta\":{\"content\":\"A\"}}]}\n\n", ct);
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);   // der zweite Teil kommt erst, wenn der Test den ersten gesehen hat
            await RawUpstream.Send(s, "data: {\"choices\":[{\"delta\":{\"content\":\"B\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", ct);
        };
        var records = new List<ProxyRecord>();
        using var proxy = StartProxy(up.Url, records);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{proxy.ListenPort}/v1/chat/completions")
        { Content = new StringContent("{\"stream\":true,\"messages\":[]}", Encoding.UTF8, "application/json") };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        using var rd = new StreamReader(await resp.Content.ReadAsStreamAsync());
        var first = await rd.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));   // vor dem Ende des Streams
        StringAssert.Contains(first, "\"A\"");
        Assert.IsFalse(gate.Task.IsCompleted);
        gate.SetResult();
        var rest = await rd.ReadToEndAsync();
        StringAssert.Contains(rest, "[DONE]");
        await WaitUntil(() => records.Count > 0);
        var r = records.Single();
        Assert.AreEqual(2, r.ContentTokens);
        Assert.AreEqual("stop", r.FinishReason);
        Assert.IsFalse(r.ClientAborted);
    }

    [TestMethod]
    public async Task ContentHeadersAreForwardedToUpstream()
    {
        using var up = new RawUpstream();
        RawUpstream.Req? seen = null;
        up.Handler = async (r, s, ct) =>
        {
            seen = r;
            await RawUpstream.Send(s, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Language: de\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}", ct);
        };
        using var proxy = StartProxy(up.Url);
        var res = await RawExchange(proxy.ListenPort,
            "POST /v1/embeddings HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Encoding: identity\r\nContent-Language: en\r\nAuthorization: Bearer k\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
        Assert.AreEqual("application/json", seen!.Header("Content-Type"));
        Assert.AreEqual("identity", seen.Header("Content-Encoding"));
        Assert.AreEqual("en", seen.Header("Content-Language"));
        Assert.AreEqual("Bearer k", seen.Header("Authorization"));
        StringAssert.Contains(res, "Content-Language: de");   // auch Antwort-Inhaltsköpfe kommen an
        StringAssert.Contains(res, "Content-Type: application/json");
    }

    [TestMethod]
    public async Task RequestCounter_IsExactUnderParallelLoad()
    {
        using var up = new RawUpstream();
        up.Handler = async (_, s, ct) => await RawUpstream.Send(s, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}", ct);
        var records = new List<ProxyRecord>();
        using var proxy = StartProxy(up.Url, records);
        using var http = new HttpClient();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ =>
        {
            using var resp = await http.PostAsync($"http://127.0.0.1:{proxy.ListenPort}/v1/chat/completions", new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        }));
        await WaitUntil(() => proxy.Requests >= 40 && records.Count >= 40);
        Assert.AreEqual(40, proxy.Requests);
        Assert.AreEqual(40, records.Count);
    }

    [TestMethod]
    public async Task UpstreamNeverAnswers_HeaderTimeoutGives504_NotAHang()
    {
        using var up = new RawUpstream();
        up.Handler = async (_, _, ct) => { try { await Task.Delay(30000, ct); } catch { } };
        using var proxy = StartProxy(up.Url);
        proxy.HeaderTimeout = TimeSpan.FromMilliseconds(600);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await RawExchange(proxy.ListenPort, "GET /props HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        StringAssert.StartsWith(res, "HTTP/1.1 504");
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task RedirectsAreNotFollowed()
    {
        using var up = new RawUpstream();
        up.Handler = async (_, s, ct) => await RawUpstream.Send(s, "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct);
        using var proxy = StartProxy(up.Url);
        var res = await RawExchange(proxy.ListenPort, "GET /go HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        StringAssert.StartsWith(res, "HTTP/1.1 302");
        StringAssert.Contains(res, "Location: http://127.0.0.1:1/elsewhere");
    }
}
