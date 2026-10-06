using System.Net;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Beispiel-JSON nach der offiziellen API-Dokumentation von Ollama und LM Studio
internal static class BackendSamples
{
    // Ollama docs/api.md: "List Running Models"
    public const string OllamaPs = """
        {"models":[{"name":"mistral:latest","model":"mistral:latest","size":5137025024,"digest":"2ae6f6dd7a3dd734790bbbf58b8909a606e0e7e97e94b7604e0aa7ae4490e6d8",
        "details":{"parent_model":"","format":"gguf","family":"llama","families":["llama"],"parameter_size":"7.2B","quantization_level":"Q4_0"},
        "expires_at":"2024-06-04T14:38:31.83753-07:00","size_vram":5137025024}]}
        """;
    public const string OllamaPsWithCtx = """
        {"models":[{"name":"llama3:8b","model":"llama3:8b","size":6000000000,"size_vram":4000000000,"context_length":8192,
        "details":{"parameter_size":"8.0B","quantization_level":"Q4_K_M","family":"llama"},"expires_at":"2999-01-01T00:00:00Z"}]}
        """;
    public const string OllamaVersion = "{\"version\":\"0.5.1\"}";

    // LM Studio REST API v0: GET /api/v0/models
    public const string LmModels = """
        {"object":"list","data":[
         {"id":"qwen2-vl-7b-instruct","object":"model","type":"vlm","publisher":"mlx-community","arch":"qwen2_vl","compatibility_type":"mlx","quantization":"4bit","state":"not-loaded","max_context_length":32768},
         {"id":"meta-llama-3.1-8b-instruct","object":"model","type":"llm","publisher":"lmstudio-community","arch":"llama","compatibility_type":"gguf","quantization":"Q4_K_M","state":"loaded","max_context_length":131072,"loaded_context_length":4096},
         {"id":"text-embedding-nomic-embed-text-v1.5","object":"model","type":"embeddings","publisher":"nomic-ai","arch":"nomic-bert","compatibility_type":"gguf","quantization":"Q4_K_M","state":"not-loaded","max_context_length":2048}]}
        """;
    public const string LmEmpty = "{\"object\":\"list\",\"data\":[]}";
}

[TestClass]
public class BackendParserTests
{
    [TestMethod]
    public void Ollama_Version()
    {
        Assert.AreEqual("0.5.1", OllamaApi.ParseVersion(BackendSamples.OllamaVersion));
        Assert.IsNull(OllamaApi.ParseVersion("{\"x\":1}"));
        Assert.IsNull(OllamaApi.ParseVersion("nope"));
    }

    [TestMethod]
    public void Ollama_Ps_DocSample()
    {
        var m = OllamaApi.ParsePs(BackendSamples.OllamaPs)!.Single();
        Assert.AreEqual("mistral:latest", m.Name);
        Assert.AreEqual(5137025024, m.SizeBytes);
        Assert.AreEqual(5137025024, m.VramBytes);
        Assert.AreEqual("7.2B Q4_0 llama", m.Detail);
        Assert.IsNotNull(m.ExpiresAt);
        Assert.IsNull(m.ContextLength);
    }

    [TestMethod]
    public void Ollama_Ps_WithContextAndPartialOffload()
    {
        var m = OllamaApi.ParsePs(BackendSamples.OllamaPsWithCtx)!.Single();
        Assert.AreEqual(8192, m.ContextLength);
        Assert.AreEqual(4000000000, m.VramBytes);
        Assert.IsTrue(m.ExpiresAt!.Value.Year >= 2999);
    }

    [TestMethod]
    public void Ollama_Ps_EmptyNullAndInvalid()
    {
        Assert.AreEqual(0, OllamaApi.ParsePs("{\"models\":[]}")!.Count);   // läuft, nichts geladen
        Assert.AreEqual(0, OllamaApi.ParsePs("{\"models\":null}")!.Count);
        Assert.IsNull(OllamaApi.ParsePs("{\"other\":1}"));
        Assert.IsNull(OllamaApi.ParsePs("[]"));
        Assert.IsNull(OllamaApi.ParsePs("<html>"));
    }

    [TestMethod]
    public void Ollama_UnloadBody()
    {
        Assert.AreEqual("{\"model\":\"mistral:latest\",\"keep_alive\":0}", OllamaApi.UnloadBody("mistral:latest"));
    }

    [TestMethod]
    public void LmStudio_Models_LoadedFilter()
    {
        var all = LmStudioApi.ParseModels(BackendSamples.LmModels)!;
        Assert.AreEqual(3, all.Count);
        var loaded = LmStudioApi.Loaded(all);
        Assert.AreEqual(1, loaded.Count);
        Assert.AreEqual("meta-llama-3.1-8b-instruct", loaded[0].Name);
        Assert.AreEqual(4096, loaded[0].ContextLength);
        Assert.AreEqual("llama Q4_K_M gguf", loaded[0].Detail);
        Assert.IsTrue(LmStudioApi.LooksLikeLmStudio(all, BackendSamples.LmModels));
        Assert.AreEqual(0, LmStudioApi.Loaded(LmStudioApi.ParseModels(BackendSamples.LmEmpty)!).Count);
        Assert.IsTrue(LmStudioApi.LooksLikeLmStudio(LmStudioApi.ParseModels(BackendSamples.LmEmpty)!, BackendSamples.LmEmpty));
        Assert.IsNull(LmStudioApi.ParseModels("{\"x\":1}"));
    }

    [TestMethod]
    public void KindNames()
    {
        Assert.AreEqual(BackendKind.Ollama, BackendProbes.KindFromName("Ollama"));
        Assert.AreEqual(BackendKind.LmStudio, BackendProbes.KindFromName("lmstudio"));
        Assert.AreEqual(BackendKind.LlamaCpp, BackendProbes.KindFromName("llama.cpp"));
        Assert.IsNull(BackendProbes.KindFromName(null));
    }
}

[TestClass]
public class BackendDiscoveryTests
{
    private static (FakePlatform P, FakeHandler H, ServerDiscovery D) Make()
    {
        var p = new FakePlatform(); var h = new FakeHandler();
        return (p, h, new ServerDiscovery(p, null, new HttpClient(h), selfPid: 1));
    }

    [TestMethod]
    public async Task OllamaServe_IsDetected_RunnerIsNot()
    {
        var (p, h, d) = Make();
        h.Routes["127.0.0.1:11434/api/version"] = (HttpStatusCode.OK, BackendSamples.OllamaVersion);
        h.Routes["127.0.0.1:50123/api/version"] = (HttpStatusCode.OK, BackendSamples.OllamaVersion);
        p.AddServer(100, "C:\\Ollama\\ollama.exe", "ollama.exe serve", 11434, "127.0.0.1", parent: 99);
        p.Processes[99] = new ProcessDetails(99, 1, 1, "C:\\Ollama\\ollama app.exe", null, null, new Dictionary<string, string>());
        p.AddServer(101, "C:\\Ollama\\ollama.exe", "ollama.exe runner --model x --port 50123", 50123, "127.0.0.1", parent: 100);
        var r = await d.RunAsync();
        var s = r.Servers.Single();
        Assert.AreEqual(BackendKind.Ollama, s.Backend);
        Assert.AreEqual("0.5.1", s.BackendVersion);
        Assert.AreEqual("Ollama", s.Name);
        Assert.AreEqual(SaveBlock.OtherBackend, s.SaveBlock);
        Assert.IsFalse(s.CanSave);
        Assert.IsNull(s.Params);
        Assert.IsFalse(h.Requests.Any(x => x.Contains("50123")));   // der Runner wird gar nicht erst angefragt
    }

    [TestMethod]
    public async Task OllamaExe_IsNeverTakenForLlamaCpp()
    {
        var (p, h, d) = Make();
        h.Routes["127.0.0.1:11434/props"] = (HttpStatusCode.OK, Samples.PropsJson);   // selbst wenn /props antworten würde
        p.AddServer(100, "C:\\Ollama\\ollama.exe", "ollama.exe serve", 11434, "127.0.0.1");
        var r = await d.RunAsync();
        Assert.AreEqual(0, r.Servers.Count);
        Assert.IsFalse(h.Requests.Any(x => x.EndsWith("/props")));
    }

    [TestMethod]
    public async Task LmStudio_IsDetectedByModelList()
    {
        var (p, h, d) = Make();
        h.Routes["127.0.0.1:1234/api/v0/models"] = (HttpStatusCode.OK, BackendSamples.LmModels);
        p.AddServer(200, "C:\\Program Files\\LM Studio\\LM Studio.exe", "\"LM Studio.exe\"", 1234, "127.0.0.1");
        var s = (await d.RunAsync()).Servers.Single();
        Assert.AreEqual(BackendKind.LmStudio, s.Backend);
        Assert.AreEqual("LM Studio", s.Name);
        Assert.IsFalse(s.CanSave);
    }

    [TestMethod]
    public async Task LlamaServerWins_OtherListenersStayUnknown()
    {
        var (p, h, d) = Make();
        h.Routes["127.0.0.1:7000/props"] = (HttpStatusCode.OK, Samples.PropsJson);
        p.AddServer(300, "C:\\x\\renamed.exe", "renamed.exe -m a.gguf", 7000, "127.0.0.1");
        p.AddServer(301, "C:\\x\\web.exe", "web.exe", 7001, "127.0.0.1");
        var r = await d.RunAsync();
        Assert.AreEqual(BackendKind.LlamaCpp, r.Servers.Single().Backend);
    }

    [TestMethod]
    public async Task DetectAsync_ForAddServerDialog()
    {
        var h = new FakeHandler();
        h.Routes["10.0.0.5:11434/api/version"] = (HttpStatusCode.OK, BackendSamples.OllamaVersion);
        h.Routes["10.0.0.6:8080/props"] = (HttpStatusCode.OK, Samples.PropsJson);
        using var http = new HttpClient(h);
        Assert.AreEqual(BackendKind.Ollama, (await BackendProbes.DetectAsync(http, "http://10.0.0.5:11434", TimeSpan.FromSeconds(2)))!.Kind);
        Assert.AreEqual(BackendKind.LlamaCpp, (await BackendProbes.DetectAsync(http, "http://10.0.0.6:8080", TimeSpan.FromSeconds(2)))!.Kind);
        Assert.IsNull(await BackendProbes.DetectAsync(http, "http://10.0.0.7:1", TimeSpan.FromSeconds(2)));
    }
}

[TestClass]
public class BackendWatcherTests
{
    private static ServerWatcher Watcher(FakeHandler h, string kind, string url)
    {
        var p = new FakePlatform();
        var reg = new ServerRegistry(p, new HttpClient(h), null, new[] { new ManualServer { Name = kind, Url = url, Kind = kind } });
        reg.RefreshNowAsync().GetAwaiter().GetResult();
        var w = reg.Servers.Single();
        w.ExternalEverySeconds = 0;
        return w;
    }

    [TestMethod]
    public async Task Ollama_NotRunning_ThenIdle_ThenLoaded_ThenUnload()
    {
        var h = new FakeHandler();
        var w = Watcher(h, "ollama", "http://127.0.0.1:11434");
        Assert.AreEqual(BackendKind.Ollama, w.Kind);
        Assert.AreEqual(ServerWatcher.ExternalState.NotRunning, w.External);   // von Anfang an: nichts erreichbar
        await w.PollAsync();
        Assert.AreEqual(ServerWatcher.ExternalState.NotRunning, w.External);

        h.Routes["127.0.0.1:11434/api/version"] = (HttpStatusCode.OK, BackendSamples.OllamaVersion);
        h.Routes["127.0.0.1:11434/api/ps"] = (HttpStatusCode.OK, "{\"models\":[]}");
        await w.PollAsync();
        Assert.AreEqual(ServerWatcher.ExternalState.Idle, w.External);   // läuft, nichts geladen
        Assert.AreEqual("0.5.1", w.BackendVersion);
        Assert.AreEqual(0, w.VramGb);

        h.Routes["127.0.0.1:11434/api/ps"] = (HttpStatusCode.OK, BackendSamples.OllamaPs);
        await w.PollAsync();
        Assert.AreEqual(ServerWatcher.ExternalState.Loaded, w.External);
        Assert.AreEqual(1, w.Models.Count);
        Assert.AreEqual(5137025024 / 1073741824.0, w.VramGb!.Value, 1e-9);

        Assert.IsTrue(await w.UnloadAsync("mistral:latest") == false);   // ohne Route schlägt der Aufruf fehl
        h.Routes["127.0.0.1:11434/api/generate"] = (HttpStatusCode.OK, "{}");
        Assert.IsTrue(await w.UnloadAsync("mistral:latest"));
        Assert.IsTrue(h.Posts.Any(x => x.StartsWith("POST 127.0.0.1:11434/api/generate") && x.Contains("\"keep_alive\":0") && x.Contains("mistral:latest")));
    }

    [TestMethod]
    public async Task Ollama_GoesNotRunningAfterThreeFailures()
    {
        var h = new FakeHandler();
        h.Routes["127.0.0.1:11434/api/version"] = (HttpStatusCode.OK, BackendSamples.OllamaVersion);
        h.Routes["127.0.0.1:11434/api/ps"] = (HttpStatusCode.OK, BackendSamples.OllamaPs);
        var w = Watcher(h, "ollama", "http://127.0.0.1:11434");
        await w.PollAsync();
        Assert.AreEqual(ServerWatcher.ExternalState.Loaded, w.External);
        h.Routes.Clear();
        await w.PollAsync(); await w.PollAsync();
        Assert.AreEqual(ServerWatcher.ExternalState.Loaded, w.External);   // zwei Fehlschläge: noch letzte Werte
        await w.PollAsync();
        Assert.AreEqual(ServerWatcher.ExternalState.NotRunning, w.External);
    }

    [TestMethod]
    public async Task LmStudio_ShowsLoadedModelsOnly()
    {
        var h = new FakeHandler();
        h.Routes["127.0.0.1:1234/api/v0/models"] = (HttpStatusCode.OK, BackendSamples.LmModels);
        var w = Watcher(h, "lmstudio", "http://127.0.0.1:1234");
        await w.PollAsync();
        Assert.AreEqual(ServerWatcher.ExternalState.Loaded, w.External);
        Assert.AreEqual("meta-llama-3.1-8b-instruct", w.Models.Single().Name);
        Assert.AreEqual(3, w.InstalledModels.Count);
        Assert.IsFalse(await w.UnloadAsync("x"));   // LM Studio: kein Entladen von hier aus
    }

    [TestMethod]
    public async Task ExternalBackends_AreNotRecordedInLibrary()
    {
        var h = new FakeHandler();
        h.Routes["127.0.0.1:11434/api/version"] = (HttpStatusCode.OK, BackendSamples.OllamaVersion);
        h.Routes["127.0.0.1:11434/api/ps"] = (HttpStatusCode.OK, "{\"models\":[]}");
        var lib = new Library();
        var p = new FakePlatform();
        p.AddServer(100, "C:\\Ollama\\ollama.exe", "ollama.exe serve", 11434, "127.0.0.1");
        var reg = new ServerRegistry(p, new HttpClient(h), lib);
        await reg.RefreshNowAsync();
        Assert.AreEqual(BackendKind.Ollama, reg.Servers.Single().Kind);
        reg.ObserveLibrary(DateTime.Now);
        Assert.AreEqual(0, lib.History.Count);
        reg.Dispose();
    }
}
