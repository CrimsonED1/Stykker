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
}
