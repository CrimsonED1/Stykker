using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N87 – Bilder als Text: jedes Bild (auch in Werkzeugergebnissen) wird durch die Beschreibung des Vision-Modells ersetzt
[TestClass]
public class N87_DescribeImagesTests
{
    [TestMethod]
    public async Task Images_become_text_and_the_rest_stays()
    {
        var body = Encoding.UTF8.GetBytes("{\"model\":\"claude-stykker\",\"messages\":[" +
            "{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"look\"},{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"AAA\"}}]}," +
            "{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":[{\"type\":\"image\",\"source\":{\"type\":\"url\",\"url\":\"https://img.example/x.png\"}}]}]}]}");
        var asked = new List<string>();
        var result = await RouterProxy.ReplaceImagesAsync(body, (url, key) => { asked.Add(url); return Task.FromResult("a red square"); });
        var json = Encoding.UTF8.GetString(result);

        CollectionAssert.AreEqual(new[] { "data:image/png;base64,AAA", "https://img.example/x.png" }, asked);
        Assert.AreEqual(2, json.Split("[Image: a red square]").Length - 1);
        StringAssert.Contains(json, "\"text\":\"look\"");
        StringAssert.Contains(json, "\"tool_use_id\":\"t1\"");
        Assert.IsFalse(RouterProxy.HasImage(result), "danach kein Bild mehr – das Gespräch geht an das Standardziel");
    }

    [TestMethod]
    public async Task Without_images_the_body_is_untouched()
    {
        var body = Encoding.UTF8.GetBytes("{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");
        Assert.AreSame(body, await RouterProxy.ReplaceImagesAsync(body, (u, k) => Task.FromResult("x")));
    }
}

// Ohne „stream": true bekommt ein Anthropic-Client eine fertige JSON-Nachricht (so fragt die Sicherheitsprüfung an)
[TestClass]
public class N87_StreamFlagTests
{
    [TestMethod]
    public void Only_an_explicit_stream_true_means_streaming()
    {
        Assert.IsTrue(RouterProxy.WantsStream(Encoding.UTF8.GetBytes("{\"stream\":true}")));
        Assert.IsFalse(RouterProxy.WantsStream(Encoding.UTF8.GetBytes("{\"stream\":false}")));
        Assert.IsFalse(RouterProxy.WantsStream(Encoding.UTF8.GetBytes("{\"max_tokens\":2112}")));
        Assert.IsFalse(RouterProxy.WantsStream(null));
    }
}

// Stoppzeichen: OpenAI meldet nur „stop"; endet der Text auf das Stoppzeichen (oder dessen Anfang), meldet die Brücke es wie Anthropic
[TestClass]
public class N87_StopSequenceTests
{
    [TestMethod]
    public void A_cut_stop_sequence_is_reported()
    {
        var oai = Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"<block>yes</block\"},\"finish_reason\":\"stop\"}]}");
        var json = Encoding.UTF8.GetString(AnthropicBridge.ToAnthropic(oai, "m", new[] { "</block>" }));
        StringAssert.Contains(json, "\"text\":\"<block>yes\"");
        StringAssert.Contains(json, "\"stop_reason\":\"stop_sequence\"");
        StringAssert.Contains(json, "\"stop_sequence\":\"</block>\"");

        var plain = Encoding.UTF8.GetString(AnthropicBridge.ToAnthropic(
            Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"hello\"},\"finish_reason\":\"stop\"}]}"), "m", new[] { "</block>" }));
        StringAssert.Contains(plain, "\"stop_reason\":\"end_turn\"");
    }
}
