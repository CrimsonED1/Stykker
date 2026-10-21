using System.Text.Json;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N69 – Die Modellliste für Claude Desktop: nur laufende lokale Server und die gewählten Cloud-Modelle, im Format, das
// OpenAI- und Anthropic-Clients beide lesen. Andere Clients sehen die Liste wie bisher.
[TestClass]
public class N69_ClaudeModelListTests
{
    private static RouterProxy Proxy()
    {
        var proxy = new RouterProxy(0);
        proxy.SetTargets(new[]
        {
            new ProxyTarget("run", "http://127.0.0.1:1", "running-model", BackendKind.LlamaCpp, true, false, DisplayName: "Running"),
            new ProxyTarget("off", "http://127.0.0.1:2", "stopped-model", BackendKind.LlamaCpp, false, false),
            new ProxyTarget("provider:https://cloud.example/v1|cm", "https://cloud.example/v1", "cm", BackendKind.LmStudio, false, false,
                Remote: true, PublicModel: "Cloud/cm", DisplayName: "Cloud · cm", Cloud: true),
        }, "run");
        proxy.Start();
        return proxy;
    }

    private static async Task<JsonElement> Models(int port, bool anthropic)
    {
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/v1/models");
        if (anthropic) req.Headers.Add("anthropic-version", "2023-06-01");
        var resp = await http.SendAsync(req);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string[] Ids(JsonElement root) => root.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray();

    [TestMethod]
    public async Task Claude_sees_running_local_models_and_cloud_models_only()
    {
        using var proxy = Proxy();
        var root = await Models(proxy.ListenPort, anthropic: true);
        var ids = Ids(root);

        Assert.AreEqual(ClaudeDesktop.ModelAlias, ids[0], "der erste Eintrag ist das Standardmodell der App");
        CollectionAssert.Contains(ids, "running-model");
        CollectionAssert.Contains(ids, "Cloud/cm");
        CollectionAssert.DoesNotContain(ids, "stopped-model");
        var first = root.GetProperty("data")[0];
        Assert.AreEqual("model", first.GetProperty("type").GetString());
        Assert.IsTrue(first.TryGetProperty("display_name", out _));
        Assert.IsFalse(root.GetProperty("has_more").GetBoolean());
    }

    [TestMethod]
    public async Task Other_clients_still_see_every_target()
    {
        using var proxy = Proxy();
        var ids = Ids(await Models(proxy.ListenPort, anthropic: false));
        CollectionAssert.Contains(ids, "stopped-model");
        CollectionAssert.Contains(ids, "running-model");
    }

    // N-Context: der Proxy liefert die Kontextgröße pro Modell (pro Slot) über "context_length".
    // llama.cpp = Props.NCtx, Ollama/LM Studio = je Modell; unbekannt bleibt das Feld aus.
    [TestMethod]
    public async Task context_length_is_exposed_per_slot()
    {
        using var proxy = new RouterProxy(0);
        proxy.SetTargets(new[]
        {
            new ProxyTarget("s1|llama", "http://127.0.0.1:1", "llama-model", BackendKind.LlamaCpp, true, false,
                DisplayName: "Llama · llama-model", ContextLength: 8192),
            new ProxyTarget("s2|ollama-a", "http://127.0.0.1:2", "ollama-a", BackendKind.Ollama, true, false,
                DisplayName: "Ollama · ollama-a", ContextLength: 131072),
            new ProxyTarget("s2|ollama-b", "http://127.0.0.1:2", "ollama-b", BackendKind.Ollama, true, false,
                DisplayName: "Ollama · ollama-b", ContextLength: 4096),
            new ProxyTarget("s3|noctx", "http://127.0.0.1:3", "no-ctx", BackendKind.LlamaCpp, true, false,
                DisplayName: "NoCtx · no-ctx"),
        }, "s1|llama");
        proxy.Start();

        var root = await Models(proxy.ListenPort, anthropic: false);
        var data = root.GetProperty("data").EnumerateArray().ToArray();

        var llama = data.Single(d => d.GetProperty("id").GetString() == "llama-model");
        Assert.AreEqual(8192, llama.GetProperty("context_length").GetInt32());

        // Pro Slot: zwei Ollama-Modelle auf demselben Server, unterschiedliche Kontextgrößen
        var ollamaA = data.Single(d => d.GetProperty("id").GetString() == "ollama-a");
        Assert.AreEqual(131072, ollamaA.GetProperty("context_length").GetInt32());
        var ollamaB = data.Single(d => d.GetProperty("id").GetString() == "ollama-b");
        Assert.AreEqual(4096, ollamaB.GetProperty("context_length").GetInt32());

        // Unbekannte Kontextgröße: Feld bleibt absent (keine falsche Angabe)
        var noCtx = data.Single(d => d.GetProperty("id").GetString() == "no-ctx");
        Assert.IsFalse(noCtx.TryGetProperty("context_length", out _));
    }
}
