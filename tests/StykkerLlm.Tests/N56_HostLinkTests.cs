using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Model-Host ↔ Server (docs/plan-hosts-gateway.md, P2): Host-Verzeichnis, Einwahl, Stand, Neuverbindung.
// Ohne HTTP: zwei WebSockets über ein Loopback-Socket-Paar (echtes WebSocket-Protokoll, kein Netz nach außen).
[TestClass]
public class N56_HostLinkTests
{
    private static AppPaths TempPaths(string tag)
    {
        var p = new AppPaths(Path.Combine(Path.GetTempPath(), "slm-n56-" + tag + "-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(p.Root);
        return p;
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
        // Der Stream besitzt den Socket: ein Abbruch schließt die Verbindung wirklich (wie in Kestrel)
        return (WebSocket.CreateFromStream(new NetworkStream(server.Client, ownsSocket: true), new WebSocketCreationOptions { IsServer = true }),
                WebSocket.CreateFromStream(new NetworkStream(client.Client, ownsSocket: true), new WebSocketCreationOptions { IsServer = false }));
    }

    private static async Task Until(Func<bool> condition, int ms = 5000)
    {
        var end = DateTime.Now.AddMilliseconds(ms);
        while (!condition() && DateTime.Now < end) await Task.Delay(30);
        Assert.IsTrue(condition(), "condition not reached in time");
    }

    [TestMethod, Timeout(20000)]
    public void Registry_GivesTheTokenOnce_StoresOnlyItsHash_AndPersists()
    {
        var paths = TempPaths("reg");
        var reg = new HostRegistry(paths, new FakePlatform());
        var (entry, token) = reg.Add("gpu-box", DateTime.Now);
        Assert.AreEqual(entry.Id, reg.Find(token)?.Id);
        Assert.IsNull(reg.Find("wrong"));
        Assert.IsNull(reg.Find(""));
        var file = File.ReadAllText(Path.Combine(paths.Root, HostRegistry.FileName));
        Assert.IsFalse(file.Contains(token, StringComparison.Ordinal), "the token itself is never stored");

        var again = new HostRegistry(paths, new FakePlatform());
        Assert.AreEqual("gpu-box", again.Find(token)?.Name);
        Assert.IsTrue(again.Remove(entry.Id));
        Assert.IsNull(new HostRegistry(paths, new FakePlatform()).Find(token));
    }

    [TestMethod, Timeout(20000)]
    public void ConnectUri_UsesWsOrWss()
    {
        Assert.AreEqual("ws://pc:8078/hosts/connect", HostConfig.ConnectUri("http://pc:8078").ToString());
        Assert.AreEqual("wss://gw.example/hosts/connect", HostConfig.ConnectUri("https://gw.example/").ToString());
    }

    [TestMethod, Timeout(20000)]
    public async Task Host_DialsIn_SaysHello_AndPushesItsState()
    {
        var paths = TempPaths("hub");
        var hub = new HostHub(new HostRegistry(paths, new FakePlatform()));
        var (entry, token) = hub.Registry.Add("box", DateTime.Now);
        using var sim = new SimHost(SimServerSpec.Defaults());
        sim.World.Start();
        await sim.Engine.TickAsync();

        var (serverWs, clientWs) = await SocketPair();
        using var stop = new CancellationTokenSource();
        var serving = hub.HandleAsync(serverWs, entry, "127.0.0.1", stop.Token);
        var config = new HostConfig { Server = "http://pc:8078", Token = token };
        var client = new HostLinkClient(config, () => StateJson.WriteText(sim.Engine, null, null, 0, DateTimeOffset.Now, withHistory: false),
            "GPU-BOX", "0.3.1", TimeSpan.FromMilliseconds(100), _ => Task.FromResult(clientWs));
        var running = client.RunAsync(stop.Token);

        await Until(() => hub.List().Single().State != null);
        var live = hub.List().Single();
        Assert.IsTrue(live.Connected);
        Assert.AreEqual("GPU-BOX", live.Name);
        Assert.AreEqual("0.3.1", live.Version);
        Assert.AreEqual(HostLinkState.Connected, client.State);
        Assert.AreEqual(sim.Engine.Servers.Count, live.State!.Servers.Count);
        var json = HostStateJson.Write(hub.List(), DateTime.Now);
        StringAssert.Contains(json, "\"connected\":true");
        Assert.IsFalse(json.Contains(token, StringComparison.Ordinal));

        stop.Cancel();
        try { await running; } catch (OperationCanceledException) { }
        await Until(() => !hub.List().Single().Connected);
        await serving;
    }

    [TestMethod, Timeout(20000)]
    public async Task ANewConnection_ReplacesTheOldOne()
    {
        var paths = TempPaths("replace");
        var hub = new HostHub(new HostRegistry(paths, new FakePlatform()));
        var (entry, _) = hub.Registry.Add("box", DateTime.Now);
        var (s1, c1) = await SocketPair();
        var (s2, c2) = await SocketPair();
        var first = hub.HandleAsync(s1, entry, "a", CancellationToken.None);
        await HostProtocol.SendAsync(c1, HostProtocol.Hello("one", "1"), CancellationToken.None);
        await Until(() => hub.List().Single().Name == "one");
        var second = hub.HandleAsync(s2, entry, "b", CancellationToken.None);
        await HostProtocol.SendAsync(c2, HostProtocol.Hello("two", "1"), CancellationToken.None);
        await Until(() => hub.List().Single().Name == "two");
        Assert.IsTrue(hub.List().Single().Connected);
        Assert.AreEqual("b", hub.List().Single().Address);
        string? old;
        try { old = await HostProtocol.ReceiveAsync(c1, CancellationToken.None); }
        catch (WebSocketException) { old = null; }
        Assert.IsNull(old, "the old connection was dropped");
        await first;
        Assert.IsTrue(hub.List().Single().Connected, "closing the old one does not mark the host offline");
        await c2.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await second;
        Assert.IsFalse(hub.List().Single().Connected);
    }

    [TestMethod, Timeout(20000)]
    public async Task RefusedToken_IsReported_AndNotHammered()
    {
        var config = new HostConfig { Server = "http://pc:8078", Token = "stale" };
        int tries = 0;
        var client = new HostLinkClient(config, () => "{}", "x", "1", connect: _ => { tries++; throw new HostRefusedException(); });
        using var stop = new CancellationTokenSource();
        var running = client.RunAsync(stop.Token);
        await Until(() => client.State == HostLinkState.Refused);
        Assert.AreEqual(Strings.HostRefused, client.LastError);
        await Task.Delay(300);
        Assert.AreEqual(1, tries, "after a refusal the next try waits a minute");
        stop.Cancel();
        await running;
    }

    [TestMethod, Timeout(20000)]
    public void BrokenHostJson_WithNulls_IsNotPaired_AndDoesNotCrash()
    {
        var paths = TempPaths("nulls");
        File.WriteAllText(Path.Combine(paths.Root, HostConfig.FileName), "{\"server\":\"http://pc:8078\",\"token\":null}");
        var config = HostConfig.Load(paths, new FakePlatform());
        Assert.IsFalse(config.Paired);
        Assert.AreEqual(HostLinkState.NotPaired, new HostLinkClient(config, () => "{}", "x", "1").State);
    }

    [TestMethod, Timeout(20000)]
    public async Task WithoutHostJson_TheHostIsNotPaired()
    {
        var client = new HostLinkClient(new HostConfig(), () => "{}", "x", "1");
        Assert.AreEqual(HostLinkState.NotPaired, client.State);
        await client.RunAsync(CancellationToken.None);
        Assert.AreEqual(HostLinkState.NotPaired, client.State);
    }
}
