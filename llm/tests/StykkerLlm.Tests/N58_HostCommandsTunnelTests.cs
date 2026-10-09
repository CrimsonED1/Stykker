using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Befehle und Tunnel zwischen Server und Host (docs/plan-hosts-gateway.md, P4), über ein Loopback-WebSocket-Paar.
[TestClass]
public class N58_HostCommandsTunnelTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public required HostHub Hub { get; init; }
        public required string HostId { get; init; }
        public required CancellationTokenSource Stop { get; init; }
        public required Task Serving { get; init; }
        public required Task Running { get; init; }

        public async ValueTask DisposeAsync()
        {
            Stop.Cancel();
            try { await Running; } catch (OperationCanceledException) { }
            try { await Serving; } catch (OperationCanceledException) { }
        }
    }

    private static async Task<(WebSocket Server, WebSocket Client)> SocketPair()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var server = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        return (WebSocket.CreateFromStream(new NetworkStream(server.Client, ownsSocket: true), new WebSocketCreationOptions { IsServer = true }),
                WebSocket.CreateFromStream(new NetworkStream(client.Client, ownsSocket: true), new WebSocketCreationOptions { IsServer = false }));
    }

    private static async Task<Rig> Connect(Func<string, JsonElement, CancellationToken, Task<HostReply>>? onCommand = null, HttpMessageHandler? local = null)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "slm-n58-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(paths.Root);
        var hub = new HostHub(new HostRegistry(paths, new FakePlatform()));
        var (entry, token) = hub.Registry.Add("box", DateTime.Now);
        var (s, c) = await SocketPair();
        var stop = new CancellationTokenSource();
        var serving = hub.HandleAsync(s, entry, "127.0.0.1", stop.Token);
        var link = new HostLinkClient(new HostConfig { Server = "http://pc:17400", Token = token }, () => "{}", "box", "1",
            TimeSpan.FromMilliseconds(200), _ => Task.FromResult(c), onCommand, local);
        var running = link.RunAsync(stop.Token);
        var end = DateTime.Now.AddSeconds(5);
        while (hub.Find(entry.Id)?.Connected != true && DateTime.Now < end) await Task.Delay(20);
        return new Rig { Hub = hub, HostId = entry.Id, Stop = stop, Serving = serving, Running = running };
    }

    [TestMethod, Timeout(20000)]
    public async Task Command_GoesToTheHost_AndTheReplyComesBack()
    {
        await using var rig = await Connect((name, args, _) =>
            Task.FromResult(new HostReply(true, "did " + name + " " + args.GetProperty("x").GetString(), JsonSerializer.SerializeToElement(new[] { 1, 2 }))));
        var r = await rig.Hub.CommandAsync(rig.HostId, "models", new System.Text.Json.Nodes.JsonObject { ["x"] = "y" });
        Assert.IsTrue(r.Ok, r.Message);
        Assert.AreEqual("did models y", r.Message);
        Assert.AreEqual(2, r.Data!.Value.GetArrayLength());
    }

    [TestMethod, Timeout(20000)]
    public async Task Command_TimesOut_AndUnknownHostsAreRefused()
    {
        await using var rig = await Connect(async (_, _, ct) => { await Task.Delay(5000, ct); return new HostReply(true, "late"); });
        var r = await rig.Hub.CommandAsync(rig.HostId, "slow", timeout: TimeSpan.FromMilliseconds(300));
        Assert.IsFalse(r.Ok);
        Assert.AreEqual(Strings.HostNoAnswer, r.Message);
        Assert.AreEqual(Strings.HostNotConnected, (await rig.Hub.CommandAsync("nope", "models")).Message);
    }

    [TestMethod, Timeout(20000)]
    public async Task Tunnel_StreamsTheModelServersAnswer_ByteForByte()
    {
        var big = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 5000).Select(i => $"data: {{\"i\":{i}}}\n\n")));   // > 50 KB, mehrere Stücke
        var local = new FakeHandler();
        string? seenBody = null;
        local.Responder = (key, body) => { seenBody = body; return key.Contains("/v1/chat/completions") ? (HttpStatusCode.OK, Encoding.UTF8.GetString(big)) : null; };
        await using var rig = await Connect(local: local);

        using var http = new HttpClient(new HostTunnelHandler(rig.Hub, rig.HostId));
        using var req = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:8081/v1/chat/completions") { Content = new StringContent("{\"model\":\"m\"}", Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer abc");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        var got = await resp.Content.ReadAsByteArrayAsync();
        CollectionAssert.AreEqual(big, got);
        Assert.AreEqual("{\"model\":\"m\"}", seenBody);
        Assert.AreEqual("Bearer abc", local.AuthHeaders.Last());
    }

    [TestMethod, Timeout(20000)]
    public async Task Tunnel_OnlyToLocalhost_OnTheHost()
    {
        await using var rig = await Connect(local: new FakeHandler());
        using var http = new HttpClient(new HostTunnelHandler(rig.Hub, rig.HostId));
        var ex = await Assert.ThrowsExceptionAsync<HttpRequestException>(() => http.GetAsync("http://192.168.1.1/admin"));
        StringAssert.Contains(ex.Message, Strings.HostTunnelOnlyLocal);
    }

    [TestMethod, Timeout(20000)]
    public async Task HostGone_EndsWaitingCommandsAtOnce()
    {
        var rig = await Connect(async (_, _, ct) => { await Task.Delay(10000, ct); return new HostReply(true, ""); });
        var pending = rig.Hub.CommandAsync(rig.HostId, "slow", timeout: TimeSpan.FromSeconds(15));
        await Task.Delay(200);
        await rig.DisposeAsync();   // Host weg
        var r = await pending;
        Assert.IsFalse(r.Ok);
        Assert.IsTrue(r.Message.Contains("lost") || r.Message == Strings.HostNotConnected, r.Message);
    }

    [TestMethod]
    public void Settings_AllowOnlyModelServers_PlusTheOwnList()
    {
        var s = new HostSettings();
        Assert.IsTrue(s.Allows(@"C:\llama\llama-server.exe"));
        Assert.IsTrue(s.Allows("llama-server-cuda"));
        Assert.IsTrue(s.Allows("ollama"));
        Assert.IsFalse(s.Allows(@"C:\Windows\System32\cmd.exe"));
        Assert.IsFalse(s.Allows("powershell"));
        Assert.IsFalse(s.Allows(""));
        s.AllowPrograms.Add(@"D:\tools\my-server.exe");
        Assert.IsTrue(s.Allows(@"D:\tools\my-server.exe"));
    }

    [TestMethod, Timeout(20000)]
    public async Task Commands_StartInTheSimulator_AndRefuseOtherPrograms()
    {
        using var sim = new SimHost(SimServerSpec.Defaults());
        sim.World.Start();
        await sim.Engine.TickAsync();
        var cmds = new HostCommands(sim.Engine, new HostSettings());
        var bad = await cmds.RunAsync("start", JsonSerializer.SerializeToElement(new { program = "cmd.exe", args = new[] { "/c", "echo" } }), CancellationToken.None);
        Assert.IsFalse(bad.Ok);
        StringAssert.Contains(bad.Message, "cmd.exe");

        var ok = await cmds.RunAsync("start", JsonSerializer.SerializeToElement(new
        {
            name = "remote-qwen", program = @"C:\llama\llama-server.exe", args = new[] { "-m", @"D:\models\qwen.gguf", "--port", "8099" },
        }), CancellationToken.None);
        Assert.IsTrue(ok.Ok, ok.Message);
        Assert.IsFalse((await cmds.RunAsync("stop", JsonSerializer.SerializeToElement(new { key = "nope" }), CancellationToken.None)).Ok);
        Assert.IsFalse((await cmds.RunAsync("format-disk", default, CancellationToken.None)).Ok);
    }

    [TestMethod, Timeout(20000)]
    public async Task Commands_StartAModelFile_WithTheHostsLlamaServer()
    {
        using var sim = new SimHost(SimServerSpec.Defaults());
        sim.World.Start();
        await sim.Engine.TickAsync();
        var cmds = new HostCommands(sim.Engine, new HostSettings { LlamaServer = @"C:\llama\llama-server.exe" });
        var r = await cmds.RunAsync("start", JsonSerializer.SerializeToElement(new { model = @"D:\models\qwen3-8b.gguf" }), CancellationToken.None);
        Assert.IsTrue(r.Ok, r.Message);
        StringAssert.Contains(r.Message, "port");
    }
}
