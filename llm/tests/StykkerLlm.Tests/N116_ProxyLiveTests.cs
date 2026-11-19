using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N116 – Live-Anzeige des Proxys: Phase je laufender Anfrage (wartet, denkt, antwortet, Werkzeug) und das Register der laufenden Anfragen
[TestClass]
public class N116_ProxyLiveTests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 12, 0, 0);

    private static ProxyTap StreamTap() => new() { Stream = true };
    private static void Line(ProxyTap tap, string json, int sec) => tap.Feed(Encoding.UTF8.GetBytes("data: " + json + "\n\n"), T0.AddSeconds(sec));

    [TestMethod]
    public void OpenAiSse_WaitingThinkingAnsweringTool()
    {
        var tap = StreamTap();
        Assert.IsNull(tap.LiveState);   // vor dem ersten Delta: wartet
        Line(tap, """{"choices":[{"delta":{"role":"assistant"}}]}""", 1);
        Assert.IsNull(tap.LiveState);
        Line(tap, """{"choices":[{"delta":{"reasoning_content":"hm"}}]}""", 2);
        Assert.AreEqual(ProxyLivePhase.Thinking, tap.LiveState!.Phase);
        Assert.AreEqual(T0.AddSeconds(2), tap.LiveState.Since);
        Line(tap, """{"choices":[{"delta":{"content":"Hallo"}}]}""", 3);
        Assert.AreEqual(ProxyLivePhase.Answering, tap.LiveState!.Phase);
        Line(tap, """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"read_file","arguments":""}}]}}]}""", 4);
        Assert.AreEqual(ProxyLivePhase.Tool, tap.LiveState!.Phase);
        Assert.AreEqual("read_file", tap.LiveState.Tool);
        Assert.AreEqual(T0.AddSeconds(4), tap.LiveState.Since);
        Assert.AreEqual(1, tap.ToolCalls);
        Assert.AreEqual(1, tap.Reasoning);
        Assert.AreEqual(1, tap.Content);
    }

    [TestMethod]
    public void AnthropicSse_WaitingThinkingAnsweringTool()
    {
        var tap = StreamTap();
        Line(tap, """{"type":"message_start","message":{"usage":{"input_tokens":5,"output_tokens":1}}}""", 1);
        Line(tap, """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""", 1);
        Assert.IsNull(tap.LiveState);
        Line(tap, """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Erst denken."}}""", 2);
        Assert.AreEqual(ProxyLivePhase.Thinking, tap.LiveState!.Phase);
        Line(tap, """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Ich schaue nach."}}""", 3);
        Assert.AreEqual(ProxyLivePhase.Answering, tap.LiveState!.Phase);
        Line(tap, """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"get_weather","input":{}}}""", 4);
        Assert.AreEqual(ProxyLivePhase.Tool, tap.LiveState!.Phase);
        Assert.AreEqual("get_weather", tap.LiveState.Tool);
        Line(tap, """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"city\":"}}""", 5);
        Assert.AreEqual(ProxyLivePhase.Tool, tap.LiveState!.Phase);
        Assert.AreEqual("get_weather", tap.LiveState.Tool);
        Assert.AreEqual(T0.AddSeconds(4), tap.LiveState.Since);
        Assert.AreEqual(1, tap.ToolCalls);
    }

    [TestMethod]
    public void PhaseSince_ChangesOnlyOnPhaseChange()
    {
        var tap = StreamTap();
        Line(tap, """{"choices":[{"delta":{"reasoning_content":"a"}}]}""", 1);
        Line(tap, """{"choices":[{"delta":{"reasoning_content":"b"}}]}""", 5);
        Assert.AreEqual(T0.AddSeconds(1), tap.LiveState!.Since);
        Line(tap, """{"choices":[{"delta":{"content":"x"}}]}""", 7);
        Line(tap, """{"choices":[{"delta":{"content":"y"}}]}""", 9);
        Assert.AreEqual(T0.AddSeconds(7), tap.LiveState!.Since);
        Line(tap, """{"choices":[{"delta":{"reasoning_content":"c"}}]}""", 10);   // zurück zum Denken: neue Phase
        Assert.AreEqual(ProxyLivePhase.Thinking, tap.LiveState!.Phase);
        Assert.AreEqual(T0.AddSeconds(10), tap.LiveState.Since);
    }

    [TestMethod]
    public void OpenAiToolArguments_KeepNameAndCountCalls()
    {
        var tap = StreamTap();
        Line(tap, """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"grep","arguments":""}}]}}]}""", 1);
        Line(tap, """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"q\":1}"}}]}}]}""", 2);
        Assert.AreEqual("grep", tap.LiveState!.Tool);
        Assert.AreEqual(T0.AddSeconds(1), tap.LiveState.Since);
        Line(tap, """{"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"name":"read_file","arguments":""}}]}}]}""", 3);
        Assert.AreEqual("read_file", tap.LiveState!.Tool);   // zweiter Aufruf: neuer Name, Phase bleibt
        Assert.AreEqual(T0.AddSeconds(1), tap.LiveState.Since);
        Assert.AreEqual(2, tap.ToolCalls);
    }

    [TestMethod]
    public void Next_IsPure()
    {
        var s0 = ProxyLiveState.Initial(T0);
        Assert.AreSame(s0, ProxyLive.Next(s0, ProxyLivePhase.Waiting, T0.AddSeconds(1)));
        var s1 = ProxyLive.Next(s0, ProxyLivePhase.Thinking, T0.AddSeconds(1));
        Assert.AreEqual(new ProxyLiveState(ProxyLivePhase.Thinking, T0.AddSeconds(1), null), s1);
        Assert.AreSame(s1, ProxyLive.Next(s1, ProxyLivePhase.Thinking, T0.AddSeconds(2)));
        var s2 = ProxyLive.Next(s1, ProxyLivePhase.Tool, T0.AddSeconds(3), "bash");
        Assert.AreEqual(new ProxyLiveState(ProxyLivePhase.Tool, T0.AddSeconds(3), "bash"), s2);
        Assert.AreSame(s2, ProxyLive.Next(s2, ProxyLivePhase.Tool, T0.AddSeconds(4)));
        Assert.AreEqual(new ProxyLiveState(ProxyLivePhase.Answering, T0.AddSeconds(5), null), ProxyLive.Next(s2, ProxyLivePhase.Answering, T0.AddSeconds(5)));
    }

    // Antwortstrom, den der Test Stück für Stück freigibt (oder mit einem Fehler beendet)
    private sealed class GateStream : Stream
    {
        private readonly Channel<byte[]?> _ch = Channel.CreateUnbounded<byte[]?>();
        private byte[] _cur = Array.Empty<byte>();
        private int _pos;
        public void Push(string text) => _ch.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public void End() => _ch.Writer.TryComplete();
        public void Fail() => _ch.Writer.TryComplete(new IOException("upstream broke"));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_pos >= _cur.Length)
            {
                if (!await _ch.Reader.WaitToReadAsync(ct)) return 0;
                if (_ch.Reader.TryRead(out var next) && next != null) { _cur = next; _pos = 0; }
            }
            int n = Math.Min(buffer.Length, _cur.Length - _pos);
            _cur.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Upstream ohne Netz: antwortet mit dem Gate-Strom als SSE oder wirft, sobald Throw freigegeben wird
    private sealed class GateHandler : HttpMessageHandler
    {
        public GateStream Body { get; } = new();
        public TaskCompletionSource? Throw { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Throw != null) { await Throw.Task.WaitAsync(ct); throw new HttpRequestException("connection refused"); }
            var content = new StreamContent(Body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private static async Task<TcpClient> SendChat(int port)
    {
        var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, port);
        var body = """{"model":"m","stream":true,"messages":[{"role":"user","content":"hi"}]}""";
        var req = "POST /v1/chat/completions HTTP/1.1\r\nHost: 127.0.0.1\r\nUser-Agent: opencode/0.9 (win)\r\nContent-Type: application/json\r\n"
            + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n" + body;
        await c.GetStream().WriteAsync(Encoding.UTF8.GetBytes(req));
        return c;
    }

    private static async Task<bool> WaitUntil(Func<bool> cond, int ms = 5000)
    {
        var end = DateTime.Now.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.Now > end) return false; await Task.Delay(20); }
        return true;
    }

    [TestMethod]
    public async Task Registry_ShowsRunningRequestAndRemovesItAtEnd()
    {
        var handler = new GateHandler();
        using var http = new HttpClient(handler);
        using var proxy = new RequestProxy("srv-a", "http://upstream.invalid", 0, http);
        proxy.Start();
        Assert.AreEqual(0, proxy.Live().Count);
        using var c = await SendChat(proxy.ListenPort);

        Assert.IsTrue(await WaitUntil(() => proxy.Live().Count == 1));
        var r = proxy.Live()[0];
        Assert.AreEqual("srv-a", r.ServerKey);
        Assert.AreEqual("opencode", r.Client);
        Assert.AreEqual(ProxyLivePhase.Waiting, r.Phase);
        Assert.AreEqual(r.Started, r.PhaseSince);

        handler.Body.Push("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"hm\"}}]}\n\n");
        Assert.IsTrue(await WaitUntil(() => proxy.Live() is [{ Phase: ProxyLivePhase.Thinking }]));
        handler.Body.Push("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"bash\",\"arguments\":\"\"}}]}}]}\n\n");
        Assert.IsTrue(await WaitUntil(() => proxy.Live() is [{ Phase: ProxyLivePhase.Tool, Tool: "bash", ToolCalls: 1, ReasoningDeltas: 1 }]));

        handler.Body.Push("data: [DONE]\n\n");
        handler.Body.End();
        Assert.IsTrue(await WaitUntil(() => proxy.Live().Count == 0));
        Assert.IsTrue(await WaitUntil(() => proxy.ActiveRequests == 0));
    }

    [TestMethod]
    public async Task Registry_RemovesRequestAfterUpstreamException()
    {
        var handler = new GateHandler { Throw = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var http = new HttpClient(handler);
        using var proxy = new RequestProxy("srv-b", "http://upstream.invalid", 0, http);
        proxy.Start();
        using var c = await SendChat(proxy.ListenPort);
        Assert.IsTrue(await WaitUntil(() => proxy.Live().Count == 1));
        handler.Throw.SetResult();
        Assert.IsTrue(await WaitUntil(() => proxy.Live().Count == 0));
        Assert.IsTrue(await WaitUntil(() => proxy.ActiveRequests == 0));
    }

    [TestMethod]
    public async Task Registry_RemovesRequestWhenStreamBreaks()
    {
        var handler = new GateHandler();
        using var http = new HttpClient(handler);
        using var proxy = new RequestProxy("srv-c", "http://upstream.invalid", 0, http);
        proxy.Start();
        using var c = await SendChat(proxy.ListenPort);
        handler.Body.Push("data: {\"choices\":[{\"delta\":{\"content\":\"Hallo\"}}]}\n\n");
        Assert.IsTrue(await WaitUntil(() => proxy.Live() is [{ Phase: ProxyLivePhase.Answering, ContentDeltas: 1 }]));
        handler.Body.Fail();
        Assert.IsTrue(await WaitUntil(() => proxy.Live().Count == 0));
    }
}
