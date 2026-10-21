using System.Text;
using System.Text.Json;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N91 – Kontext und Slots am Proxy: Vorprüfung „Anfrage passt sicher nicht in den Kontext“, Überlauf-Meldungen der Backends in
// Anthropic-Form, Slots gesamt/frei in /v1/models (llama.cpp) und nur der geladene Kontext von LM Studio.
[TestClass]
public class N91_ContextSlotsTests
{
    private static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);

    private static string Chat(int chars) =>
        "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"" + new string('a', chars) + "\"}]}";

    // ── Schätzung ──

    [TestMethod]
    public void Min_tokens_count_text_but_not_image_data()
    {
        string img = new('Q', 60000);
        var anthropic = Body("{\"model\":\"m\",\"system\":\"" + new string('s', 600) + "\",\"messages\":[{\"role\":\"user\",\"content\":[" +
                             "{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"" + img + "\"}}," +
                             "{\"type\":\"text\",\"text\":\"" + new string('t', 600) + "\"}]}]}");
        int n = RouterProxy.MinPromptTokens(anthropic);
        Assert.IsTrue(n >= 1200 / RouterProxy.CharsPerTokenMin, $"Text zählt: {n}");
        Assert.IsTrue(n < 2000 / RouterProxy.CharsPerTokenMin, $"Bilddaten zählen nicht: {n}");

        var openAi = Body("{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64," + img + "\"}}]}]}");
        Assert.IsTrue(RouterProxy.MinPromptTokens(openAi) < 10);
    }

    [TestMethod]
    public void Min_tokens_of_non_json_is_zero()
    {
        Assert.AreEqual(0, RouterProxy.MinPromptTokens(Body("not json")));
    }

    [TestMethod]
    public void Too_long_only_when_context_is_known_and_clearly_exceeded()
    {
        var known = new ProxyTarget("k", "http://127.0.0.1:1", "m", BackendKind.Ollama, true, false, ContextLength: 1000);
        var unknown = known with { ContextLength = null };

        Assert.IsNull(RouterProxy.TooLong(known, Body(Chat(1000 * RouterProxy.CharsPerTokenMin - 100))), "knapp darunter: das Backend entscheidet");
        Assert.IsNull(RouterProxy.TooLong(unknown, Body(Chat(1_000_000))), "ohne Kontextangabe keine Prüfung");
        Assert.IsNull(RouterProxy.TooLong(known, null));

        var msg = RouterProxy.TooLong(known, Body(Chat(20000 * RouterProxy.CharsPerTokenMin)));
        Assert.IsNotNull(msg);
        StringAssert.StartsWith(msg, "prompt is too long: ");
        StringAssert.Contains(msg, "> 1000 maximum");
    }

    // ── Überlauf-Meldungen der Backends ──

    [TestMethod]
    public void Backend_overflow_messages_are_recognised()
    {
        Assert.IsTrue(RouterProxy.IsContextOverflow("the request exceeds the available context size, try increasing it"));   // llama.cpp
        Assert.IsTrue(RouterProxy.IsContextOverflow("This model's maximum context length is 32768 tokens. However, you requested 40000 tokens"));   // vLLM/OpenAI
        Assert.IsTrue(RouterProxy.IsContextOverflow("context_length_exceeded"));
        Assert.IsFalse(RouterProxy.IsContextOverflow("model not found"));
        Assert.IsFalse(RouterProxy.IsContextOverflow(null));

        Assert.AreEqual("prompt is too long: the request exceeds the available context size",
            RouterProxy.AnthropicOverflowMessage("the request exceeds the available context size"));
        Assert.AreEqual("prompt is too long: 5 tokens > 4 maximum", RouterProxy.AnthropicOverflowMessage("prompt is too long: 5 tokens > 4 maximum"));
        Assert.AreEqual("model not found", RouterProxy.AnthropicOverflowMessage("model not found"));
    }

    // ── Ziele aus den Servern ──

    private static SlotView Slot(int id, bool busy, int ctx) => new(id, busy, false, 0, 0, 0, ctx, 0, 0, -1, 0);

    [TestMethod]
    public void Llama_slots_total_idle_and_context_per_slot()
    {
        var props = new LlamaProps(null, null, null, TotalSlots: 4, NCtx: 8192, IsSleeping: false, Role: null, ModelFtype: null);
        var slots = new[] { Slot(0, true, 8192), Slot(1, false, 8192), Slot(2, false, 8192), Slot(3, true, 8192) };

        Assert.AreEqual((8192, 4, 2), ProxyManager.LlamaSlots(props, slots));
        Assert.AreEqual((8192, 4, (int?)null), ProxyManager.LlamaSlots(props, Array.Empty<SlotView>()), "ohne /slots: frei unbekannt");
        Assert.AreEqual((4096, 2, 1), ProxyManager.LlamaSlots(null, new[] { Slot(0, true, 4096), Slot(1, false, 4096) }), "ohne /props: aus /slots");
        Assert.AreEqual(((int?)null, (int?)null, (int?)null), ProxyManager.LlamaSlots(null, Array.Empty<SlotView>()));
    }

    private static LoadedModel Model(string name, int? ctx, bool isMax = false) => new(name, 0, 0, null, ctx, "", "loaded", isMax);

    [TestMethod]
    public void Same_model_name_counts_once_regardless_of_case()
    {
        var list = ProxyManager.DistinctModels(new[] { Model("Qwen3", 8192), Model("qwen3", 4096), Model("", 1), Model("llama", null) });
        CollectionAssert.AreEqual(new[] { "Qwen3", "llama" }, list.Select(m => m.Name).ToArray());
        Assert.AreEqual(8192, list[0].ContextLength, "der erste Eintrag gilt");
    }

    [TestMethod]
    public void Lm_studio_max_context_is_not_reported_as_loaded_context()
    {
        var all = LmStudioApi.ParseModels("""
            {"object":"list","data":[
             {"id":"a","object":"model","type":"llm","arch":"llama","compatibility_type":"gguf","quantization":"Q4_K_M","state":"loaded","max_context_length":131072,"loaded_context_length":4096},
             {"id":"b","object":"model","type":"llm","arch":"llama","compatibility_type":"gguf","quantization":"Q4_K_M","state":"loaded","max_context_length":131072}]}
            """)!;
        var loaded = LmStudioApi.Loaded(all);
        Assert.AreEqual(4096, ProxyManager.LoadedContext(loaded.Single(m => m.Name == "a")));
        var b = loaded.Single(m => m.Name == "b");
        Assert.AreEqual(131072, b.ContextLength, "die Anzeige behält den Höchstwert");
        Assert.IsTrue(b.ContextIsMax);
        Assert.IsNull(ProxyManager.LoadedContext(b), "an Clients geht nur der geladene Kontext");
    }

    // ── über den Proxy ──

    private static async Task<JsonElement> Models(int port)
    {
        using var http = new HttpClient();
        var text = await http.GetStringAsync($"http://127.0.0.1:{port}/v1/models");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [TestMethod]
    public async Task Models_list_carries_slots_for_llama_and_default_entries()
    {
        using var proxy = new RouterProxy(0);
        proxy.SetTargets(new[]
        {
            new ProxyTarget("s1|llama", "http://127.0.0.1:1", "llama-model", BackendKind.LlamaCpp, true, false,
                ContextLength: 8192, Slots: 4, SlotsIdle: 3),
            new ProxyTarget("s2|ollama", "http://127.0.0.1:2", "ollama-a", BackendKind.Ollama, true, false, ContextLength: 4096),
        }, "s1|llama");
        proxy.Start();

        var data = (await Models(proxy.ListenPort)).GetProperty("data").EnumerateArray().ToArray();
        var llama = data.Single(d => d.GetProperty("id").GetString() == "llama-model");
        Assert.AreEqual(4, llama.GetProperty("slots_total").GetInt32());
        Assert.AreEqual(3, llama.GetProperty("slots_idle").GetInt32());

        var ollama = data.Single(d => d.GetProperty("id").GetString() == "ollama-a");
        Assert.IsFalse(ollama.TryGetProperty("slots_total", out _), "ohne Slot-Angabe kein Feld");

        // „stykker“ und der App-Name gehen an das Standardziel und tragen dessen Angaben
        var virt = data.Single(d => d.GetProperty("id").GetString() == RouterProxy.VirtualModel);
        Assert.AreEqual(8192, virt.GetProperty("context_length").GetInt32());
        Assert.AreEqual(4, virt.GetProperty("slots_total").GetInt32());
    }

    [TestMethod]
    public async Task Request_that_cannot_fit_gets_context_length_exceeded_without_reaching_the_backend()
    {
        using var proxy = new RouterProxy(0);
        // Port 1: dort lauscht nichts – käme die Anfrage durch, gäbe es einen Verbindungsfehler statt 400
        proxy.SetTargets(new[] { new ProxyTarget("s|m", "http://127.0.0.1:1", "m", BackendKind.Ollama, true, false, ContextLength: 512) }, "s|m");
        proxy.Start();

        using var http = new HttpClient();
        var resp = await http.PostAsync($"http://127.0.0.1:{proxy.ListenPort}/v1/chat/completions",
            new StringContent(Chat(512 * RouterProxy.CharsPerTokenMin * 3), Encoding.UTF8, "application/json"));
        Assert.AreEqual(400, (int)resp.StatusCode);
        var err = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.AreEqual("context_length_exceeded", err.GetProperty("code").GetString());
        StringAssert.StartsWith(err.GetProperty("message").GetString(), "prompt is too long: ");

        // Anthropic-Form (Claude-App): gleiche Meldung als invalid_request_error
        var anth = await http.PostAsync($"http://127.0.0.1:{proxy.ListenPort}/v1/messages",
            new StringContent(Chat(512 * RouterProxy.CharsPerTokenMin * 3), Encoding.UTF8, "application/json"));
        Assert.AreEqual(400, (int)anth.StatusCode);
        var aerr = JsonDocument.Parse(await anth.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.AreEqual("invalid_request_error", aerr.GetProperty("type").GetString());
        StringAssert.StartsWith(aerr.GetProperty("message").GetString(), "prompt is too long: ");
    }
}
