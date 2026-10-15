using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N79 – Anfragen mit Bildern gehen an das eingestellte Vision-Ziel, alles andere wie bisher
[TestClass]
public class N79_VisionTargetTests
{
    private static readonly ProxyTarget Main = new("main|m", "http://127.0.0.1:1", "main-model", BackendKind.LlamaCpp, true, false);
    private static readonly ProxyTarget Eye = new("provider:https://cloud.example/v1|v", "https://cloud.example/v1", "v", BackendKind.LmStudio, true, false,
        Remote: true, PublicModel: "Cloud/v", Cloud: true);

    private static byte[] B(string json) => System.Text.Encoding.UTF8.GetBytes(json);

    [TestMethod]
    public void Images_are_found_in_messages_and_tool_results()
    {
        Assert.IsTrue(RouterProxy.HasImage(B("{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image\",\"source\":{}}]}]}")));
        Assert.IsTrue(RouterProxy.HasImage(B("{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{}}]}]}")));
        Assert.IsTrue(RouterProxy.HasImage(B("{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"content\":[{\"type\":\"image\"}]}]}]}")));
        Assert.IsFalse(RouterProxy.HasImage(B("{\"messages\":[{\"role\":\"user\",\"content\":\"an image of a cat\"}]}")));
        Assert.IsFalse(RouterProxy.HasImage(null));
    }

    [TestMethod]
    public void A_set_vision_target_takes_requests_with_images()
    {
        var proxy = new RouterProxy(0);
        var targets = new[] { Main, Eye };
        var img = B("{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image\"}]}]}");
        proxy.SetTargets(targets, Main.Key, null, Eye.Key);
        Assert.AreSame(Eye, proxy.VisionTarget(targets, img));
        Assert.IsNull(proxy.VisionTarget(targets, B("{\"messages\":[]}")));
        proxy.SetTargets(targets, Main.Key);
        Assert.IsNull(proxy.VisionTarget(targets, img), "nicht gesetzt → Standardziel");
    }
}
