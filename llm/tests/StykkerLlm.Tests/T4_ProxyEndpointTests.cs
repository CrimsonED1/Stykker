using System.Net;
using System.Net.Sockets;
using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class T4_ProxyEndpointTests
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

    private static RawUpstream OkUpstream()
    {
        var up = new RawUpstream();
        up.Handler = async (_, s, ct) => await RawUpstream.Send(s, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}", ct);
        return up;
    }

    [TestMethod]
    public async Task EphemeralPortIsAssignedAndServed()
    {
        using var up = OkUpstream();
        using var proxy = new RequestProxy("k", up.Url, 0);
        Assert.AreEqual(0, proxy.ListenPort);
        proxy.Start();
        Assert.IsTrue(proxy.ListenPort is > 0 and <= 65535);
        using var http = new HttpClient();
        Assert.AreEqual("{}", await http.GetStringAsync($"http://127.0.0.1:{proxy.ListenPort}/props"));
    }

    [TestMethod]
    public void PickPortSkipsUsedPortsAndGivesUp()
    {
        Assert.AreEqual(9081, RequestProxy.PickPort(8081, _ => false));
        Assert.AreEqual(9086, RequestProxy.PickPort(8081, p => p <= 9085));
        Assert.AreEqual(65000, RequestProxy.PickPort(64000, p => p < 65000));   // Obergrenze
        Assert.IsNull(RequestProxy.PickPort(64960, _ => true));                 // nichts mehr frei
    }

    [TestMethod]
    public async Task Ipv6LoopbackClientIsServed()
    {
        if (!Socket.OSSupportsIPv6) Assert.Inconclusive("no IPv6 on this machine");
        using var up = OkUpstream();
        using var proxy = StartProxy(up.Url);
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.IPv6Loopback, proxy.ListenPort);
        var s = c.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes("GET /props HTTP/1.1\r\nHost: [::1]\r\nConnection: close\r\n\r\n"));
        var sb = new StringBuilder();
        var buf = new byte[4096];
        using var cts = new CancellationTokenSource(5000);
        try { while (true) { int n = await s.ReadAsync(buf, cts.Token); if (n <= 0) break; sb.Append(Encoding.Latin1.GetString(buf, 0, n)); } }
        catch { }
        StringAssert.StartsWith(sb.ToString(), "HTTP/1.1 200");
    }

    [TestMethod]
    public async Task MalformedRequestsGet400WithoutReachingUpstream()
    {
        using var up = OkUpstream();
        using var proxy = StartProxy(up.Url);
        var bad = new[]
        {
            "GARBAGE\r\n\r\n",                                                        // Anfragezeile unlesbar
            "GET noslash HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n",                        // kein origin-form
            "GET / HTTP/2.0\r\nHost: 127.0.0.1\r\n\r\n",                              // falsche Version
            "GET / HTTP/1.1\r\nBad Header Line\r\nHost: 127.0.0.1\r\n\r\n",           // Kopfzeile ohne Doppelpunkt
        };
        foreach (var req in bad)
        {
            var res = await RawExchange(proxy.ListenPort, req);
            StringAssert.StartsWith(res, "HTTP/1.1 400", req);
        }
        Assert.AreEqual(0, up.Connections);
        Assert.IsTrue(proxy.Running);   // der Proxy bleibt für weitere Anfragen bestehen
    }

    [TestMethod]
    public async Task OversizedRequestHeaderGets431()
    {
        using var up = OkUpstream();
        using var proxy = StartProxy(up.Url);
        var res = await RawExchange(proxy.ListenPort,
            $"GET /props HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Big: {new string('A', 70000)}\r\n\r\n");
        StringAssert.StartsWith(res, "HTTP/1.1 431");
        Assert.AreEqual(0, up.Connections);
    }

    [TestMethod]
    public async Task ChunkedBodyOverLimitGets413()
    {
        using var up = OkUpstream();
        using var proxy = StartProxy(up.Url);
        var res = await RawExchange(proxy.ListenPort,
            "POST /v1/chat/completions HTTP/1.1\r\nHost: 127.0.0.1\r\nTransfer-Encoding: chunked\r\n\r\n4000001\r\n");
        StringAssert.StartsWith(res, "HTTP/1.1 413");
        Assert.AreEqual(0, up.Connections);
        Assert.IsTrue(proxy.Running);
    }

    [TestMethod]
    public async Task QueryStringIsForwardedIntact()
    {
        using var up = new RawUpstream();
        string? line = null;
        up.Handler = async (r, s, ct) =>
        {
            line = r.Line1;
            await RawUpstream.Send(s, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}", ct);
        };
        using var proxy = StartProxy(up.Url);
        var res = await RawExchange(proxy.ListenPort, "GET /props?a=1&b=zwei+drei HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        StringAssert.StartsWith(res, "HTTP/1.1 200");
        StringAssert.Contains(line, "GET /props?a=1&b=zwei+drei HTTP/1.1");
    }

    [TestMethod]
    public void BodyWantsStreamOnlyForTrueLiteral()
    {
        Assert.IsTrue(RequestProxy.BodyWantsStream(Encoding.UTF8.GetBytes("{\"stream\":true}")));
        Assert.IsTrue(RequestProxy.BodyWantsStream(Encoding.UTF8.GetBytes("{\"messages\":[],\"stream\": true }")));
        Assert.IsFalse(RequestProxy.BodyWantsStream(Encoding.UTF8.GetBytes("{\"stream\":false}")));
        Assert.IsFalse(RequestProxy.BodyWantsStream(Encoding.UTF8.GetBytes("{\"stream\":\"true\"}")));
        Assert.IsFalse(RequestProxy.BodyWantsStream(Encoding.UTF8.GetBytes("{\"stream\":1}")));
        Assert.IsFalse(RequestProxy.BodyWantsStream(Encoding.UTF8.GetBytes("[{\"stream\":true}]")));
        Assert.IsFalse(RequestProxy.BodyWantsStream(Encoding.UTF8.GetBytes("not json")));
        Assert.IsFalse(RequestProxy.BodyWantsStream(new byte[] { 0xff }));
        Assert.IsFalse(RequestProxy.BodyWantsStream(Array.Empty<byte>()));
    }

    [TestMethod]
    public void IsGenerationPathIsExactAndCaseInsensitive()
    {
        Assert.IsTrue(RequestProxy.IsGenerationPath("/v1/chat/completions"));
        Assert.IsTrue(RequestProxy.IsGenerationPath("/V1/Chat/Completions"));
        Assert.IsTrue(RequestProxy.IsGenerationPath("/completions"));
        Assert.IsTrue(RequestProxy.IsGenerationPath("/v1/messages"));
        Assert.IsFalse(RequestProxy.IsGenerationPath("/v1/chat/completions/extra"));
        Assert.IsFalse(RequestProxy.IsGenerationPath("/props"));
        Assert.IsFalse(RequestProxy.IsGenerationPath("/v1/chat/completions?x=1"));
    }

    [TestMethod]
    public void LoopbackHostCheckAcceptsAndRejectsCorrectly()
    {
        Assert.IsTrue(RequestProxy.IsLoopbackHost("127.0.0.1:80"));
        Assert.IsTrue(RequestProxy.IsLoopbackHost("[::1]:9"));
        Assert.IsTrue(RequestProxy.IsLoopbackHost("sub.localhost"));
        Assert.IsFalse(RequestProxy.IsLoopbackHost("[fe80::1]:9"));
        Assert.IsFalse(RequestProxy.IsLoopbackHost("localhost.evil.com"));
        Assert.IsFalse(RequestProxy.IsLoopbackHost("192.168.1.5:8080"));
        Assert.IsFalse(RequestProxy.IsLoopbackHost("example.com"));
    }

    [TestMethod]
    public async Task ProxySurvivesUpstreamConnectionRefused()
    {
        using var proxy = StartProxy("http://127.0.0.1:1");   // dort läuft nichts
        var res = await RawExchange(proxy.ListenPort, "GET /props HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        StringAssert.StartsWith(res, "HTTP/1.1 502");
        Assert.IsTrue(proxy.Running);
        await WaitUntil(() => proxy.ActiveRequests == 0);
        Assert.AreEqual(0, proxy.ActiveRequests);
    }
}