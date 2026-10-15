using System.Text;
using System.Text.Json.Nodes;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N86 – Ein Vision-Modell nimmt nur wenige Bilder je Anfrage: Grenze aus der Fehlermeldung lesen, ältere Bilder weglassen
[TestClass]
public class N86_ImageLimitTests
{
    [TestMethod]
    public void The_limit_is_read_from_the_provider_error()
    {
        Assert.AreEqual(1, RouterProxy.ImageLimit("{\"error\":\"At most 1 image(s) may be provided in one prompt.\"}"));
        Assert.AreEqual(4, RouterProxy.ImageLimit("at most 4 images may be provided"));
        Assert.IsNull(RouterProxy.ImageLimit("context too long"));
        Assert.IsNull(RouterProxy.ImageLimit(null));
    }

    [TestMethod]
    public void Only_the_last_images_stay()
    {
        var body = Encoding.UTF8.GetBytes("{\"model\":\"m\",\"messages\":[" +
            "{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"a\"}},{\"type\":\"text\",\"text\":\"one\"}]}," +
            "{\"role\":\"assistant\",\"content\":\"ok\"}," +
            "{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"b\"}},{\"type\":\"image_url\",\"image_url\":{\"url\":\"c\"}}]}]}");
        var root = JsonNode.Parse(RouterProxy.TrimImages(body, 1))!;
        var json = root.ToJsonString();
        StringAssert.Contains(json, "\"url\":\"c\"");
        Assert.IsFalse(json.Contains("\"url\":\"a\"") || json.Contains("\"url\":\"b\""));
        Assert.AreEqual(2, json.Split("an earlier image was left out").Length - 1);
        Assert.AreEqual("one", (string?)root["messages"]![0]!["content"]![1]!["text"]);

        Assert.AreSame(body, RouterProxy.TrimImages(body, 3), "nicht mehr Bilder als erlaubt: unverändert");
    }
}
