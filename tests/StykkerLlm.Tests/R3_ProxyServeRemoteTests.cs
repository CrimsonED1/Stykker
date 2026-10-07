using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// R3: serviertes Modell wählbar (aktive Modelle), andere Stykker-Rechner anhängen, LAN-Freigabe, neuer Standardport.
[TestClass]
public class R3_ProxyServeRemoteTests
{
    private static RawUpstream Echo(string id, Action<byte[]>? onBody = null)
    {
        var up = new RawUpstream();
        up.Handler = async (req, s, ct) =>
        {
            onBody?.Invoke(req.Body);
            var payload = $"{{\"id\":\"{id}\"}}";
            await RawUpstream.Send(s, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n{payload}", ct);
        };
        return up;
    }

    private static async Task<string> Post(int port, string path, string json)
    {
        using var http = new HttpClient();
        var resp = await http.PostAsync($"http://127.0.0.1:{port}{path}", new StringContent(json, Encoding.UTF8, "application/json"));
        return (int)resp.StatusCode + " " + await resp.Content.ReadAsStringAsync();
    }

    private static async Task<string> Get(int port, string path)
    {
        using var http = new HttpClient();
        var resp = await http.GetAsync($"http://127.0.0.1:{port}{path}");
        return (int)resp.StatusCode + " " + await resp.Content.ReadAsStringAsync();
    }

    // Mehrere Modelle EINES Backends: jedes ist adressierbar, „stykker" nimmt das gewählte
    [TestMethod]
    public async Task MultiModelBackend_EachModelIsAddressable_StykkerUsesTheChosenOne()
    {
        string? seen = null;
        using var a = Echo("A", b => seen = RouterProxy.ReadModel(b));
        using var b = Echo("B");
        using var proxy = new RouterProxy(0);
        proxy.SetTargets(new[]
        {
            new ProxyTarget("s1|alpha", a.Url, "alpha", BackendKind.Ollama, true, false, ServerKey: "s1", DisplayName: "S1 · alpha"),
            new ProxyTarget("s1|beta", a.Url, "beta", BackendKind.Ollama, true, false, ServerKey: "s1", DisplayName: "S1 · beta"),
            new ProxyTarget("s2|gamma", b.Url, "gamma", BackendKind.LlamaCpp, true, false, ServerKey: "s2", DisplayName: "S2 · gamma"),
        }, "s1|beta");
        proxy.Start();

        var models = await Get(proxy.ListenPort, "/v1/models");
        StringAssert.Contains(models, "\"alpha\"");
        StringAssert.Contains(models, "\"beta\"");
        StringAssert.Contains(models, "\"gamma\"");

        // „stykker" → das gewählte Modell (beta), model-Feld wird auf den Zielnamen gesetzt
        StringAssert.Contains(await Post(proxy.ListenPort, "/v1/chat/completions", "{\"model\":\"stykker\",\"messages\":[]}"), "\"A\"");
        Assert.AreEqual("beta", seen);
        // jedes Modell direkt erreichbar
        StringAssert.Contains(await Post(proxy.ListenPort, "/v1/chat/completions", "{\"model\":\"alpha\",\"messages\":[]}"), "\"A\"");
        Assert.AreEqual("alpha", seen);
        StringAssert.Contains(await Post(proxy.ListenPort, "/v1/chat/completions", "{\"model\":\"gamma\",\"messages\":[]}"), "\"B\"");
    }

    // Namensgleichheit: lokal gewinnt, das entfernte Modell wird als „<Rechner>/<Modell>" adressiert
    [TestMethod]
    public async Task RemoteModel_IsRouted_AndNameCollisionGetsAMachinePrefix()
    {
        string? remoteSeen = null;
        using var local = Echo("L");
        using var remote = Echo("R", b => remoteSeen = RouterProxy.ReadModel(b));
        using var proxy = new RouterProxy(0);
        proxy.SetTargets(new[]
        {
            new ProxyTarget("k1|shared", local.Url, "shared", BackendKind.LlamaCpp, true, false, ServerKey: "k1", DisplayName: "Local · shared"),
            new ProxyTarget("remote:http://r|shared", remote.Url, "shared", BackendKind.LmStudio, true, false, Remote: true, PublicModel: "Home/shared", DisplayName: "Home · shared"),
        }, "k1|shared");
        proxy.Start();

        var models = await Get(proxy.ListenPort, "/v1/models");
        StringAssert.Contains(models, "\"shared\"");
        StringAssert.Contains(models, "\"Home/shared\"");

        // „shared" geht an das lokale Modell
        StringAssert.Contains(await Post(proxy.ListenPort, "/v1/chat/completions", "{\"model\":\"shared\",\"messages\":[]}"), "\"L\"");
        // „Home/shared" geht an den entfernten Rechner, model wird auf dessen echten Namen umgeschrieben
        StringAssert.Contains(await Post(proxy.ListenPort, "/v1/chat/completions", "{\"model\":\"Home/shared\",\"messages\":[]}"), "\"R\"");
        Assert.AreEqual("shared", remoteSeen);
    }

    [TestMethod]
    public async Task RemoteModel_Offline_Answers503()
    {
        using var remote = Echo("R");
        using var proxy = new RouterProxy(0);
        proxy.SetTargets(new[]
        {
            new ProxyTarget("remote:http://r|m", remote.Url, "m", BackendKind.LmStudio, false, false, Remote: true, PublicModel: "Home/m", DisplayName: "Home · m"),
        }, null);
        proxy.Start();
        var r = await Post(proxy.ListenPort, "/v1/chat/completions", "{\"model\":\"Home/m\",\"messages\":[]}");
        StringAssert.StartsWith(r, "503");
    }

    [TestMethod]
    public void ParseModelIds_IsTolerant()
    {
        CollectionAssert.AreEqual(new[] { "a", "b" }, ProxyManager.ParseModelIds("{\"object\":\"list\",\"data\":[{\"id\":\"a\"},{\"id\":\"b\",\"x\":1}]}"));
        Assert.AreEqual(0, ProxyManager.ParseModelIds("not json").Length);
        Assert.AreEqual(0, ProxyManager.ParseModelIds("{\"data\":[]}").Length);
    }

    [TestMethod]
    public void Remotes_AreAddedAndRemovedAndPersisted()
    {
        var settings = new AppSettings();
        using var pm = new ProxyManager(() => Array.Empty<ServerWatcher>(), settings, _ => { });
        pm.AddRemote("Home", "http://127.0.0.1:1/");
        Assert.AreEqual(1, pm.Remotes.Count);
        Assert.AreEqual("Home", pm.Remotes[0].Name);
        Assert.AreEqual("http://127.0.0.1:1", pm.Remotes[0].Url);   // abschließender Schrägstrich entfernt
        pm.RemoveRemote("http://127.0.0.1:1");
        Assert.AreEqual(0, pm.Remotes.Count);
    }

    // Alter Standardport 8079 → 17500; ein bewusst gesetzter anderer Port bleibt
    [TestMethod]
    public void OldDefaultPort_MigratesTo17500_ButAChosenPortStays()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-r3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, "{\"proxyPort\":8079,\"proxyEnabled\":true}");
            var s = AppSettings.Load(file);
            Assert.AreEqual(17500, s.ProxyPort);
            StringAssert.Contains(File.ReadAllText(file), "\"ProxyPort\": 17500");

            File.WriteAllText(file, "{\"proxyPort\":19000}");
            Assert.AreEqual(19000, AppSettings.Load(file).ProxyPort);

            File.WriteAllText(file, "{}");
            Assert.AreEqual(17500, AppSettings.Load(file).ProxyPort);
        }
        finally { Directory.Delete(dir, true); }
    }

    // LAN-Freigabe bindet nicht mehr nur an Loopback
    [TestMethod]
    public void BindLan_ListensBeyondLoopback()
    {
        using var proxy = new RequestProxy("k", "http://127.0.0.1:1", 0) { BindLan = true };
        proxy.Start();
        var ends = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(e => e.Port == proxy.ListenPort).ToList();
        Assert.IsTrue(ends.Any(e => !IPAddress.IsLoopback(e.Address)), string.Join(",", ends));
    }

    // Mit LAN-Freigabe entfällt die Host-Header-Prüfung (kein Zugangscode)
    [TestMethod]
    public async Task BindLan_DoesNotRejectForeignHostHeader()
    {
        using var up = Echo("A");
        using var proxy = new RequestProxy("k", up.Url, 0) { BindLan = true };
        proxy.Start();
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{proxy.ListenPort}/props");
        req.Headers.Host = "evil.example.com";
        using var resp = await http.SendAsync(req);
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
    }

    // Angehängter Rechner: seine Modelle erscheinen im eigenen /v1/models und werden dorthin geroutet
    [TestMethod]
    public async Task AttachedMachine_ModelsShowUpAndAreRouted()
    {
        string? seen = null;
        using var up = new RawUpstream();
        up.Handler = async (req, s, ct) =>
        {
            if (req.Line1.Contains("/v1/models"))
            {
                const string payload = "{\"object\":\"list\",\"data\":[{\"id\":\"remote-model\"}]}";
                await RawUpstream.Send(s, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n{payload}", ct);
                return;
            }
            seen = RouterProxy.ReadModel(req.Body);
            await RawUpstream.Send(s, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}", ct);
        };
        var settings = new AppSettings { ProxyEnabled = true, ProxyPort = 0 };
        settings.RemoteStykkers.Add(new RemoteStykker { Name = "Home", Url = up.Url });
        using var http = RequestProxy.CreateClient();
        using var pm = new ProxyManager(() => Array.Empty<ServerWatcher>(), settings, _ => { }, http: http);
        Assert.IsTrue(pm.Toggle(out var err), err);
        int port = pm.Port;

        string models = "";
        var end = DateTime.Now.AddSeconds(8);
        while (DateTime.Now < end)
        {
            models = await Get(port, "/v1/models");
            if (models.Contains("remote-model")) break;
            await Task.Delay(200);
        }
        StringAssert.Contains(models, "remote-model");

        var r = await Post(port, "/v1/chat/completions", "{\"model\":\"remote-model\",\"messages\":[]}");
        StringAssert.StartsWith(r, "200");
        Assert.AreEqual("remote-model", seen);
    }

    // Nr. 47: der Proxy erkennt die „Kontext zu klein"-Antwort des Backends (Status 400) und meldet sie – Antwort bleibt unverändert
    [TestMethod]
    public async Task Upstream400_ContextTooSmall_RaisesUpstreamError()
    {
        using var up = new RawUpstream();
        up.Handler = async (_, s, ct) =>
        {
            const string payload = "{\"error\":{\"message\":\"request (21203 tokens) exceeds the available context size (4096 tokens), try increasing it\"}}";
            await RawUpstream.Send(s, $"HTTP/1.1 400 Bad Request\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n{payload}", ct);
        };
        using var proxy = new RequestProxy("k", up.Url, 0);
        string? seen = null;
        proxy.UpstreamError += t => seen = t;
        proxy.Start();
        var r = await Post(proxy.ListenPort, "/v1/chat/completions", "{\"model\":\"x\",\"messages\":[]}");
        StringAssert.StartsWith(r, "400");
        var end = DateTime.Now.AddSeconds(5);
        while (seen == null && DateTime.Now < end) await Task.Delay(25);
        Assert.IsNotNull(seen);
        StringAssert.Contains(seen!, "available context size");
    }

    [TestMethod]
    public void ContextHint_TurnsTheBackendMessageIntoPlainText()
    {
        var hint = ProxyManager.ContextHint("{\"error\":{\"message\":\"request (21203 tokens) exceeds the available context size (4096 tokens), try increasing it\"}}");
        Assert.IsNotNull(hint);
        StringAssert.Contains(hint!, "too small");
        Assert.AreNotEqual(Strings.ProxyContextTooSmallGeneric, hint);   // die Zahlen wurden erkannt

        Assert.AreEqual(Strings.ProxyContextTooSmallGeneric, ProxyManager.ContextHint("{\"error\":\"request exceeds the available context size\"}"));
        Assert.IsNull(ProxyManager.ContextHint("{\"error\":\"model not found\"}"));
        Assert.IsNull(ProxyManager.ContextHint(""));
    }

    [TestMethod]
    public void IsServed_IsFalseWhenTheProxyIsOff()
    {
        var settings = new AppSettings();
        using var pm = new ProxyManager(() => Array.Empty<ServerWatcher>(), settings, _ => { });
        Assert.IsFalse(pm.IsServed("anything"));
    }
}