using System.Net;
using System.Text;
using System.Text.Json;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Automatische Platzierung auf den Model-Hosts und Ziele der Hosts im Proxy (docs/plan-hosts-gateway.md, P6)
[TestClass]
public class N59_HostPlacementTests
{
    private static HostModelFile F(string name, double gb) => new($@"D:\models\{name}.gguf", name, gb);

    [TestMethod]
    public void Choose_TakesTheHostWithTheFile_AndTheMostFreeVram()
    {
        var hosts = new[]
        {
            new HostCandidate("a", "A", new[] { F("qwen3-8b", 5) }, 7),
            new HostCandidate("b", "B", new[] { F("qwen3-8b", 5), F("gemma", 16) }, 20),
            new HostCandidate("c", "C", new[] { F("other", 2) }, 24),
        };
        var c = HostPlacement.Choose("qwen3-8b", hosts);
        Assert.IsTrue(c.Ok);
        Assert.AreEqual("b", c.HostId);
        // Name mit Pfad oder Endung passt auch
        Assert.AreEqual("b", HostPlacement.Choose(@"X:\y\QWEN3-8B.gguf", hosts).HostId);
    }

    [TestMethod]
    public void Choose_NoFile_FallsBack_NoVram_IsAClearError()
    {
        var hosts = new[] { new HostCandidate("a", "A", new[] { F("gemma", 16) }, 10) };
        Assert.IsTrue(HostPlacement.Choose("unknown", hosts).NoHostHasIt);
        var c = HostPlacement.Choose("gemma", hosts);
        Assert.IsFalse(c.Ok);
        StringAssert.Contains(c.Error, "gemma");
        // Ohne GPU-Angabe (unbekannt) wird nicht abgelehnt
        Assert.IsTrue(HostPlacement.Choose("gemma", new[] { new HostCandidate("x", "X", new[] { F("gemma", 16) }, null) }).Ok);
    }

    [TestMethod]
    public void ParseFiles_ReadsTheModelsReply()
    {
        var data = JsonSerializer.SerializeToElement(new[] { new { path = @"D:\m\a.gguf", name = "a", sizeGb = 4.2 }, new { path = "", name = "x", sizeGb = 1.0 } });
        var files = HostPlacement.ParseFiles(data);
        Assert.AreEqual(1, files.Count);
        Assert.AreEqual(4.2, files[0].SizeGb, 1e-9);
    }

    [TestMethod]
    public void HostTargets_OfferTheFileName_AndCarryTheHost()
    {
        var rs = new RemoteServer { Key = "k1", Name = "qwen3-8b", Model = @"D:\models\qwen3-8b.gguf", Url = "http://127.0.0.1:8081", Port = 8081, Online = true };
        var t = ProxyManager.HostTargets(new HostServers("h1", "Box", new[] { rs }), rs).Single();
        Assert.AreEqual("h1", t.HostId);
        Assert.AreEqual("qwen3-8b", t.PublicModel);
        Assert.AreEqual(@"D:\models\qwen3-8b.gguf", t.Model);
        Assert.IsTrue(t.Ready);
        Assert.IsTrue(t.Remote);
    }

    // Router: ein unbekanntes Modell wird über HostStart gestartet, danach geht die Anfrage über den Client des Hosts
    private sealed class TestRouter(int port) : RouterProxy(port)
    {
        public Task<ProxyRoute> Route(string model) =>
            RouteAsync("POST", "/v1/chat/completions", Array.Empty<(string, string)>(), Encoding.UTF8.GetBytes("{\"model\":\"" + model + "\"}"), CancellationToken.None);
    }

    [TestMethod, Timeout(20000)]
    public async Task Router_StartsOnAHost_WaitsUntilReady_AndRoutesThroughTheTunnel()
    {
        var router = new TestRouter(0);
        var local = new ProxyTarget("s1|m", "http://127.0.0.1:9", "m", BackendKind.LlamaCpp, true, false, ServerKey: "s1");
        router.SetTargets(new[] { local }, null);
        using var client = new HttpClient();
        int starts = 0;
        router.HostClient = id => id == "h1" ? client : null;
        router.HostStart = (model, token) =>
        {
            starts++;
            // der Host meldet den Server kurz darauf als bereit
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                router.SetTargets(new[] { local, new ProxyTarget("host:h1|k|x", "http://127.0.0.1:8081", @"D:\x.gguf", BackendKind.LlamaCpp, true, false,
                    Remote: true, PublicModel: "x", HostId: "h1") }, null);
            });
            return Task.FromResult(new HostChoice("h1", "Box", new HostModelFile(@"D:\x.gguf", "x", 4)));
        };
        var r = await router.Route("x");
        Assert.AreEqual(1, starts);
        Assert.AreSame(client, r.Client);
        Assert.AreEqual("http://127.0.0.1:8081", r.Upstream);
        StringAssert.Contains(Encoding.UTF8.GetString(r.Body!), "x.gguf");

        // läuft schon: kein zweiter Start
        await router.Route("x");
        Assert.AreEqual(1, starts);
    }

    [TestMethod, Timeout(20000)]
    public async Task Router_UnknownEverywhere_UsesTheDefault_NoVram_Gives503()
    {
        var router = new TestRouter(0);
        var local = new ProxyTarget("s1|m", "http://127.0.0.1:9", "m", BackendKind.LlamaCpp, true, false, ServerKey: "s1");
        router.SetTargets(new[] { local }, null);
        router.HostStart = (_, _) => Task.FromResult(HostChoice.None);
        var r = await router.Route("nobody-has-this");
        Assert.AreEqual("http://127.0.0.1:9", r.Upstream);
        Assert.IsNull(r.Client);

        router.HostStart = (_, _) => Task.FromResult(HostChoice.Fail("no vram"));
        var bad = await router.Route("big");
        Assert.AreEqual(503, bad.LocalStatus);
        StringAssert.Contains(Encoding.UTF8.GetString(bad.LocalBody!), "no vram");
    }

    [TestMethod, Timeout(20000)]
    public async Task Router_HostGone_Gives503()
    {
        var router = new TestRouter(0);
        router.SetTargets(new[] { new ProxyTarget("host:h1|k|x", "http://127.0.0.1:8081", "x", BackendKind.LlamaCpp, true, false, Remote: true, HostId: "h1") }, null);
        router.HostClient = _ => null;
        var r = await router.Route("x");
        Assert.AreEqual(503, r.LocalStatus);
    }
}
