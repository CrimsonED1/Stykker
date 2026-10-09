using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Nr. 46: der Stykker-Proxy kann eingetragene Cloud-Anbieter bedienen. Die Modelle kommen live vom Anbieter und werden als
// „<Anbieter>/<Modell>" angeboten; der Schlüssel liegt DPAPI-geschützt im Datenordner und verlässt nur als Authorization-Kopfzeile
// den Proxy, ein Cloud-Modell wird nie automatisch gewählt.
[TestClass]
public class N46_CloudProviderTests
{
    // Der Anbieter als Rohserver: /models liefert die Modellliste, alles andere ist eine (streamende) Generierungsantwort
    private sealed class FakeProvider : IDisposable
    {
        public readonly RawUpstream Up = new();
        public List<string?> ChatAuth { get; } = new();
        public List<string?> ChatModels { get; } = new();
        public string[] Models { get; set; } = { "m1", "m2" };
        public int ModelRequests;                 // wie oft der Proxy die Modellliste geholt hat

        public FakeProvider()
        {
            Up.Handler = async (req, s, ct) =>
            {
                if (req.Line1.Contains("/models", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref ModelRequests);
                    var payload = "{\"object\":\"list\",\"data\":[" + string.Join(",", Models.Select(m => $"{{\"id\":\"{m}\"}}")) + "]}";
                    await RawUpstream.Send(s, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n{payload}", ct);
                    return;
                }
                lock (ChatAuth) ChatAuth.Add(req.Header("Authorization"));
                lock (ChatModels) ChatModels.Add(RouterProxy.ReadModel(req.Body) ?? "");
                await RawUpstream.Send(s, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n", ct);
                await RawUpstream.Send(s, "data: {\"choices\":[{\"delta\":{\"content\":\"A\"}}]}\n\n", ct);
                await RawUpstream.Send(s, "data: {\"choices\":[{\"delta\":{\"content\":\"B\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", ct);
            };
        }

        public string Url => Up.Url;
        public void Dispose() => Up.Dispose();
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-n46-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task WaitUntil(Func<bool> cond, int ms = 8000)
    {
        var end = DateTime.Now.AddMilliseconds(ms);
        while (!cond() && DateTime.Now < end) await Task.Delay(25);
    }

    private static async Task<string> Get(int port, string path)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var resp = await http.GetAsync($"http://127.0.0.1:{port}{path}");
        return (int)resp.StatusCode + " " + await resp.Content.ReadAsStringAsync();
    }

    // Eine Anfrage an den Proxy; authorization = was der Client mitschickt (darf beim Cloud-Ziel nie ankommen)
    private static async Task<string> Post(int port, string body, string? authorization = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/chat/completions")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (authorization != null) req.Headers.TryAddWithoutValidation("Authorization", authorization);
        using var resp = await http.SendAsync(req);
        return (int)resp.StatusCode + " " + await resp.Content.ReadAsStringAsync();
    }

    // Der Schlüssel liegt verschlüsselt im Datenordner, wird wieder lesbar und steht nirgends im Klartext
    [TestMethod]
    public void ProviderKey_IsStoredProtectedAndNeverInClearText()
    {
        var dir = TempDir();
        try
        {
            var keys = new ProviderKeys(dir, new FakePlatform { UserProtection = true });
            Assert.IsFalse(keys.Has("https://api.example.com/v1"));
            Assert.IsNull(keys.Get("https://api.example.com/v1"));

            keys.Set("https://api.example.com/v1", "sk-geheim");
            Assert.AreEqual("sk-geheim", keys.Get("https://api.example.com/v1"));
            Assert.IsTrue(keys.Has("https://api.example.com/v1"));

            var file = Directory.GetFiles(Path.Combine(dir, ProviderKeys.DirName), "*.key").Single();
            Assert.IsFalse(File.ReadAllText(file).Contains("sk-geheim"), "im Klartext steht der Schlüssel nirgends");

            keys.Remove("https://api.example.com/v1");
            Assert.IsFalse(keys.Has("https://api.example.com/v1"));
            Assert.IsNull(keys.Get("https://api.example.com/v1"));

            // Trailing slash / Groß-Kleinschreibung: derselbe Anbieter, derselbe Schlüssel
            keys.Set("https://api.example.com/v1", "sk-2");
            Assert.AreEqual("sk-2", keys.Get("HTTPS://API.example.com/v1/"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // Ohne Schlüssel wird der Anbieter gar nicht erst gefragt – Opt-in je Anbieter
    [TestMethod]
    public void Provider_WithoutKey_IsNeverAskedAndOffersNothing()
    {
        var dir = TempDir();
        try
        {
            var handler = new FakeHandler();
            using var http = new HttpClient(handler);
            var settings = new AppSettings { ProxyEnabled = true, ProxyPort = 0 };
            settings.ProxyProviders.Add(new ProxyProvider { Name = "Cloud", BaseUrl = "https://api.example.com/v1" });
            var plat = new FakePlatform { UserProtection = true };
            using var pm = new ProxyManager(() => Array.Empty<ServerWatcher>(), settings, _ => { }, http: http, keys: new ProviderKeys(dir, plat));
            Assert.IsTrue(pm.Toggle(out var error), error);

            Thread.Sleep(300);
            Assert.AreEqual(0, handler.Requests.Count, "ohne Schlüssel geht keine einzige Anfrage zum Anbieter");
            var st = pm.ProviderStates().Single();
            Assert.IsFalse(st.HasKey);
            Assert.AreEqual(0, pm.Choices().Count(c => c.Cloud), "ohne Schlüssel gibt es auch kein Cloud-Modell im Proxy");
        }
        finally { Directory.Delete(dir, true); }
    }

    // Eintragen braucht eine Basis-URL und einen Schlüssel; beides fehlt, wird nichts gespeichert
    [TestMethod]
    public void AddProvider_NeedsAHttpUrlAndAKey()
    {
        var dir = TempDir();
        try
        {
            var settings = new AppSettings();
            var keys = new ProviderKeys(dir, new FakePlatform { UserProtection = true });
            using var pm = new ProxyManager(() => Array.Empty<ServerWatcher>(), settings, _ => { }, keys: keys);

            pm.AddProvider("Cloud", "ftp://api.example.com", "sk-1", out var e1);
            Assert.AreEqual(Strings.ProxyProviderUrlInvalid, e1);
            pm.AddProvider("Cloud", "", "sk-1", out var e2);
            Assert.AreEqual(Strings.ProxyProviderUrlInvalid, e2);
            pm.AddProvider("Cloud", "https://api.example.com/v1", "  ", out var e3);
            Assert.AreEqual(Strings.ProxyProviderKeyMissing, e3);
            Assert.AreEqual(0, settings.ProxyProviders.Count, "ein abgelehnter Anbieter wird nicht eingetragen");

            pm.AddProvider("Cloud", "https://api.example.com/v1/", "sk-1", out var e4);
            Assert.IsNull(e4);
            Assert.AreEqual(1, settings.ProxyProviders.Count);
            Assert.AreEqual("https://api.example.com/v1", settings.ProxyProviders[0].BaseUrl, "der abschließende Schrägstrich ist weg");

            // Schlüssel für einen nicht eingetragenen Anbieter: das ist ein Fehler, kein stilles Anlegen
            pm.SetProviderKey("https://api.example.com/v1", "sk-2", out var e5);
            Assert.IsNull(e5);
            pm.SetProviderKey("https://api.other.com/v1", "sk-2", out var e6);
            Assert.AreEqual(Strings.ProxyProviderUnknown, e6);

            // Ohne Namen nimmt der Proxy den Host der Basis-URL
            pm.AddProvider("", "https://api.third.com/v1", "sk-3", out var e7);
            Assert.IsNull(e7);
            Assert.AreEqual("api.third.com", settings.ProxyProviders[1].Name);

            pm.RemoveProvider("https://api.example.com/v1");
            Assert.AreEqual(1, settings.ProxyProviders.Count);
            Assert.IsFalse(keys.Has("https://api.example.com/v1"), "mit dem Anbieter verschwindet auch sein Schlüssel");
        }
        finally { Directory.Delete(dir, true); }
    }

    // Die Modelle kommen live vom Anbieter und stehen im Proxy als „<Anbieter>/<Modell>"
    [TestMethod]
    public async Task ProviderModels_AreFetchedLiveAndOfferedAsProviderSlashModel()
    {
        var dir = TempDir();
        using var provider = new FakeProvider();
        try
        {
            var settings = new AppSettings { ProxyEnabled = true, ProxyPort = 0 };
            var plat = new FakePlatform { UserProtection = true };
            var records = new List<ProxyRecord>();
            using var http = RequestProxy.CreateClient();
            using var pm = new ProxyManager(() => Array.Empty<ServerWatcher>(), settings, r => { lock (records) records.Add(r); },
                http: http, keys: new ProviderKeys(dir, plat));
            pm.AddProvider("Cloud", provider.Url, "sk-cloud", out var error);
            Assert.IsNull(error);
            Assert.IsTrue(pm.Toggle(out error), error);

            await WaitUntil(() => pm.Choices().Any(c => c.Cloud));
            var choices = pm.Choices().Where(c => c.Cloud).ToList();
            Assert.AreEqual(2, choices.Count, "beide Modelle des Anbieters stehen zur Wahl");
            var m1 = choices.First(c => c.Model == "Cloud/m1");
            Assert.AreEqual(ProxyTarget.CloudKey(provider.Url, "m1"), m1.Value);
            StringAssert.StartsWith(m1.Display, "☁ ");

            // Der Proxy bietet sie genau so an, wie ein Client sie anfragt
            var models = await Get(pm.Port, "/v1/models");
            StringAssert.Contains(models, "\"Cloud/m1\"");
            StringAssert.Contains(models, "\"Cloud/m2\"");
            Assert.IsTrue(provider.ModelRequests > 0, "die Liste kam vom Anbieter, nicht aus einer festen Liste");
        }
        finally { Directory.Delete(dir, true); }
    }

    // Anfrage an ein Cloud-Modell: der Proxy setzt den Schlüssel des Anbieters und wirft den des Clients weg
    [TestMethod]
    public async Task CloudRequest_CarriesTheProvidersKey_NotTheClientsOne()
    {
        var dir = TempDir();
        using var provider = new FakeProvider();
        try
        {
            var settings = new AppSettings { ProxyEnabled = true, ProxyPort = 0 };
            var plat = new FakePlatform { UserProtection = true };
            var records = new List<ProxyRecord>();
            using var http = RequestProxy.CreateClient();
            using var pm = new ProxyManager(() => Array.Empty<ServerWatcher>(), settings, r => { lock (records) records.Add(r); },
                http: http, keys: new ProviderKeys(dir, plat));
            pm.AddProvider("Cloud", provider.Url, "sk-cloud", out var error);
            Assert.IsNull(error);
            Assert.IsTrue(pm.Toggle(out error), error);
            await WaitUntil(() => pm.Choices().Any(c => c.Cloud));

            // Ausdrücklich gewählt (nie automatisch)
            pm.SetChoice(pm.Choices().First(c => c.Model == "Cloud/m1"));
            Assert.AreEqual("", pm.TargetModel, "bei einem Cloud-Ziel trägt die Zielkennung das Modell");

            var r = await Post(pm.Port, "{\"model\":\"Cloud/m1\",\"stream\":true,\"messages\":[]}", "Bearer sk-localer-schluessel");
            StringAssert.StartsWith(r, "200");
            Assert.IsTrue(provider.ChatAuth.Count > 0, "der Anbieter hat die Anfrage bekommen");
            Assert.IsTrue(provider.ChatAuth.All(a => a == "Bearer sk-cloud"), string.Join(",", provider.ChatAuth));
            Assert.IsTrue(provider.ChatModels.All(m => m == "m1"), "das Model-Feld trägt den Namen des Anbieters: " + string.Join(",", provider.ChatModels));

            // Aufnahme: die Token-Zahlen kommen an (Kosten pflegt der Anbieter, nicht Stykker)
            await WaitUntil(() => records.Count > 0);
            var rec = records.Last();
            Assert.AreEqual(2, rec.ContentTokens);
            Assert.AreEqual(ProxyTarget.CloudKey(provider.Url, "m1"), rec.ServerKey);
        }
        finally { Directory.Delete(dir, true); }
    }

    // Ohne hinterlegten Schlüssel antwortet der Proxy mit einem klaren Hinweis statt den Anbieter zu fragen
    [TestMethod]
    public async Task CloudRequest_WithoutKey_AnswersWithAHint()
    {
        using var proxy = new RouterProxy(0);
        proxy.SetTargets(new[]
        {
            new ProxyTarget(ProxyTarget.CloudKey("https://api.example.com/v1", "m1"), "https://api.example.com/v1", "m1",
                BackendKind.LmStudio, true, false, Remote: true, PublicModel: "Cloud/m1", DisplayName: "Cloud · m1", Cloud: true),
        }, null);
        proxy.Start();

        var r = await Post(proxy.ListenPort, "{\"model\":\"Cloud/m1\",\"messages\":[]}");
        StringAssert.StartsWith(r, "503");
        StringAssert.Contains(r, "API key");
    }

    // Nie automatisch: ohne ausdrückliche Wahl nimmt „stykker" kein Cloud-Modell, auch wenn sonst keins da ist
    [TestMethod]
    public async Task AutoTarget_NeverBecomesACloudModel()
    {
        var cloud = new ProxyTarget(ProxyTarget.CloudKey("https://api.example.com/v1", "m1"), "https://api.example.com/v1", "m1",
            BackendKind.LmStudio, true, false, Remote: true, PublicModel: "Cloud/m1", DisplayName: "Cloud · m1", Cloud: true);
        using var onlyCloud = new RouterProxy(0);
        onlyCloud.SetTargets(new[] { cloud }, null);
        onlyCloud.Start();
        StringAssert.StartsWith(await Post(onlyCloud.ListenPort, "{\"model\":\"stykker\",\"messages\":[]}"), "503",
            "ohne ausdrückliche Wahl gibt es kein Standardziel, das ins Netz geht");

        // Mit einem lokalen Modell daneben nimmt „stykker" das lokale
        using var local = Echo("L");
        using var both = new RouterProxy(0);
        both.SetTargets(new[] { cloud, new ProxyTarget("k1|x", local.Url, "x", BackendKind.LlamaCpp, true, false, ServerKey: "k1", DisplayName: "L · x") }, null);
        both.Start();
        StringAssert.Contains(await Post(both.ListenPort, "{\"model\":\"stykker\",\"messages\":[]}"), "\"L\"");
    }

    [TestMethod]
    public async Task AutoTarget_IgnoresTheManagerChoiceToo()
    {
        var dir = TempDir();
        using var provider = new FakeProvider();
        try
        {
            var settings = new AppSettings { ProxyEnabled = true, ProxyPort = 0 };
            var plat = new FakePlatform { UserProtection = true };
            using var http = RequestProxy.CreateClient();
            using var pm = new ProxyManager(() => Array.Empty<ServerWatcher>(), settings, _ => { }, http: http, keys: new ProviderKeys(dir, plat));
            pm.AddProvider("Cloud", provider.Url, "sk-cloud", out _);
            Assert.IsTrue(pm.Toggle(out var error), error);
            await WaitUntil(() => pm.Choices().Any(c => c.Cloud));

            Assert.AreEqual("", pm.ChoiceValue(), "ohne Wahl steht die Auswahl auf Auto");
            StringAssert.StartsWith(await Post(pm.Port, "{\"model\":\"stykker\",\"messages\":[]}"), "503");
            Assert.AreEqual(0, provider.ChatAuth.Count, "es ging keine einzige Anfrage zum Anbieter");

            // Ausdrücklich gewählt geht raus
            pm.SetChoiceValue(pm.Choices().First(c => c.Model == "Cloud/m2").Value);
            Assert.AreEqual(ProxyTarget.CloudKey(provider.Url, "m2"), pm.ChoiceValue());
            StringAssert.StartsWith(await Post(pm.Port, "{\"model\":\"stykker\",\"messages\":[]}"), "200");
        }
        finally { Directory.Delete(dir, true); }
    }

    // Ein nicht erreichbarer Anbieter: die Oberfläche sieht den Grund, der Proxy meldet 503
    [TestMethod]
    public async Task ProviderOffline_ShowsTheReasonAndTheProxyAnswers503()
    {
        var dir = TempDir();
        try
        {
            var settings = new AppSettings { ProxyEnabled = true, ProxyPort = 0 };
            var plat = new FakePlatform { UserProtection = true };
            using var http = RequestProxy.CreateClient();
            using var pm = new ProxyManager(() => Array.Empty<ServerWatcher>(), settings, _ => { }, http: http, keys: new ProviderKeys(dir, plat));
            pm.AddProvider("Cloud", "http://127.0.0.1:1/v1", "sk-cloud", out var error);
            Assert.IsNull(error);
            Assert.IsTrue(pm.Toggle(out error), error);

            await WaitUntil(() => pm.ProviderStates().Any(p => p.Error.Length > 0));
            var st = pm.ProviderStates().Single();
            Assert.IsFalse(st.Ready);
            StringAssert.Contains(st.Error, "not reachable");
            Assert.AreEqual(0, pm.Choices().Count(c => c.Cloud), "ohne erreichte Modelle gibt es kein Ziel");

            var r = await Post(pm.Port, "{\"model\":\"Cloud/m1\",\"messages\":[]}");
            StringAssert.StartsWith(r, "503");
        }
        finally { Directory.Delete(dir, true); }
    }

    // Der Zustand nennt die Anbieter und ihre Modelle – den Schlüssel aber nie (weder Zustand noch settings.json)
    [TestMethod]
    public async Task State_CarriesTheProviders_ButNeverTheKey()
    {
        var dir = TempDir();
        try
        {
            var paths = new AppPaths(dir);
            var settings = AppSettings.Load(paths.SettingsFile);
            var plat = new FakePlatform { UserProtection = true };
            using var engine = new MonitorEngine(plat, paths, settings, new HttpClient(new FakeHandler()));
            engine.Proxies.AddProvider("Cloud", "https://api.example.com/v1", "sk-supergeheim", out var error);
            Assert.IsNull(error);

            var json = StateJson.WriteText(engine, null, null, 17400, DateTimeOffset.Now);
            StringAssert.Contains(json, "\"providers\":[");
            StringAssert.Contains(json, "Cloud");
            StringAssert.Contains(json, "\"hasKey\":true");
            Assert.IsFalse(json.Contains("sk-supergeheim"), "der Schlüssel gehört nicht in den Zustand");

            var state = StateSnapshot.Parse(json);
            var p = state.Proxy.Providers.Single();
            Assert.AreEqual("Cloud", p.Name);
            Assert.AreEqual("https://api.example.com/v1", p.Url);
            Assert.IsTrue(p.HasKey);

            Assert.IsFalse(File.ReadAllText(paths.SettingsFile).Contains("sk-supergeheim"),
                "in settings.json steht nur, welcher Anbieter eingetragen ist – nie sein Schlüssel");
            await Task.CompletedTask;
        }
        finally { Directory.Delete(dir, true); }
    }

    // Die Aktionen der Oberfläche: eintragen, Schlüssel ersetzen, entfernen – und die Auswahl „<Anbieter>/<Modell>"
    [TestMethod]
    public async Task Actions_AddKeyRemoveAndChooseAProviderModel()
    {
        var dir = TempDir();
        using var provider = new FakeProvider();
        try
        {
            var settings = new AppSettings();
            var plat = new FakePlatform { UserProtection = true };
            using var engine = new MonitorEngine(plat, new AppPaths(dir), settings, RequestProxy.CreateClient());
            var ctx = new ActionContext { Engine = engine, Launcher = new LaunchCoordinator(engine, new RemotePrompt()) };

            var add = new ActionRequest { Action = "provider.add", Secret = "sk-cloud" };
            add.Values["name"] = "Cloud";
            add.Values["url"] = provider.Url;
            Assert.IsTrue((await ActionApi.ExecuteAsync(add, ctx, new RemotePrompt())).Ok);

            await WaitUntil(() => engine.Proxies.Choices().Any(c => c.Cloud));
            var choice = engine.Proxies.Choices().First(c => c.Model == "Cloud/m1");

            var bad = new ActionRequest { Action = "provider.key", Arg = "https://nicht-da.example.com/v1", Secret = "x" };
            Assert.IsFalse((await ActionApi.ExecuteAsync(bad, ctx, new RemotePrompt())).Ok, "ein fremder Anbieter lässt sich nicht bedienen");

            var choose = await ActionApi.ExecuteAsync(new ActionRequest { Action = "proxy.target", Arg = choice.Value }, ctx, new RemotePrompt());
            Assert.IsTrue(choose.Ok, choose.Message);
            Assert.AreEqual(choice.Value, StateSnapshot.Parse(StateJson.WriteText(engine, null, null, 17400, DateTimeOffset.Now)).Proxy.TargetValue);

            var del = await ActionApi.ExecuteAsync(new ActionRequest { Action = "provider.remove", Arg = provider.Url }, ctx, new RemotePrompt());
            Assert.IsTrue(del.Ok, del.Message);
            Assert.AreEqual(0, engine.Proxies.Choices().Count(c => c.Cloud));
            Assert.AreEqual("", engine.Proxies.ChoiceValue(), "war das Ziel dieses Anbieters, gilt wieder Auto");
        }
        finally { Directory.Delete(dir, true); }
    }

    // Ein lokaler Rückfall: nach dem Entfernen des Anbieters ist wieder ein lokales Modell da
    private static RawUpstream Echo(string id)
    {
        var up = new RawUpstream();
        up.Handler = async (_, s, ct) =>
        {
            var payload = $"{{\"id\":\"{id}\"}}";
            await RawUpstream.Send(s, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n{payload}", ct);
        };
        return up;
    }
}
