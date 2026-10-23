using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N99 – „Describe images only“ auch für OpenAI-Clients: „image_url“-Blöcke werden zur Beschreibung, damit ein Gespräch mit
// Werkzeugen und einem Bild im Verlauf beim Standardziel bleibt und nicht ganz an das (kleine) Vision-Modell geht
[TestClass]
public class N99_DescribeImagesOpenAiTests
{
    private static readonly ProxyTarget Main = new("main|m", "http://127.0.0.1:1", "main-model", BackendKind.LlamaCpp, true, false);
    private static readonly ProxyTarget Eye = new("provider:https://cloud.example/v1|v", "https://cloud.example/v1", "v", BackendKind.LmStudio, true, false,
        Remote: true, PublicModel: "Cloud/v", Cloud: true);
    private static readonly ProxyTarget EyeDown = Eye with { Ready = false };

    private static byte[] B(string json) => Encoding.UTF8.GetBytes(json);

    // Ein Agent-Gespräch wie von pi: Werkzeuge, ein Bild weit vorn im Verlauf, danach Werkzeugaufrufe
    private static readonly byte[] AgentChat = B("{\"model\":\"stykker\",\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"bash\"}}],\"messages\":[" +
        "{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"what is this\"},{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/jpeg;base64,AAA\"}}]}," +
        "{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"bash\",\"arguments\":\"{}\"}}]}," +
        "{\"role\":\"tool\",\"tool_call_id\":\"c1\",\"content\":\"ok\"}," +
        "{\"role\":\"user\",\"content\":\"go on\"}]}");

    [TestMethod]
    public async Task OpenAi_images_become_text_and_tool_calls_stay()
    {
        var asked = new List<string>();
        var result = await RouterProxy.ReplaceImagesAsync(AgentChat, (url, key) => { asked.Add(url); return Task.FromResult("a plate with sauce"); });
        var json = Encoding.UTF8.GetString(result);

        CollectionAssert.AreEqual(new[] { "data:image/jpeg;base64,AAA" }, asked);
        StringAssert.Contains(json, "[Image: a plate with sauce]");
        StringAssert.Contains(json, "\"tool_call_id\":\"c1\"");
        StringAssert.Contains(json, "\"text\":\"what is this\"");
        Assert.IsFalse(RouterProxy.HasImage(result));
    }

    [TestMethod]
    public async Task An_address_given_as_plain_text_is_read_too()
    {
        var body = B("{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":\"https://img.example/a.png\"}]}]}");
        var asked = new List<string>();
        var result = await RouterProxy.ReplaceImagesAsync(body, (url, key) => { asked.Add(url); return Task.FromResult("x"); });

        CollectionAssert.AreEqual(new[] { "https://img.example/a.png" }, asked);
        Assert.IsFalse(RouterProxy.HasImage(result));
    }

    [TestMethod]
    public async Task With_describe_on_the_chat_goes_to_the_default_target()
    {
        var proxy = new RouterProxy(0) { VisionDescribe = true };
        var targets = new[] { Main, Eye };
        proxy.SetTargets(targets, Main.Key, null, Eye.Key);

        var result = await proxy.DescribeImagesAsync(targets, AgentChat, (vt, url, key) => Task.FromResult("described by " + vt.Model));

        StringAssert.Contains(Encoding.UTF8.GetString(result!), "[Image: described by v]");
        Assert.IsNull(proxy.VisionTarget(targets, result), "kein Bild mehr → nicht das Vision-Ziel");
    }

    [TestMethod]
    public async Task With_describe_off_or_the_vision_target_down_the_body_is_untouched()
    {
        var targets = new[] { Main, Eye };
        var off = new RouterProxy(0) { VisionDescribe = false };
        off.SetTargets(targets, Main.Key, null, Eye.Key);
        Assert.AreSame(AgentChat, await off.DescribeImagesAsync(targets, AgentChat, (vt, u, k) => Task.FromResult("x")));

        var downTargets = new[] { Main, EyeDown };
        var down = new RouterProxy(0) { VisionDescribe = true };
        down.SetTargets(downTargets, Main.Key, null, EyeDown.Key);
        Assert.AreSame(AgentChat, await down.DescribeImagesAsync(downTargets, AgentChat, (vt, u, k) => Task.FromResult("x")));
    }
}
