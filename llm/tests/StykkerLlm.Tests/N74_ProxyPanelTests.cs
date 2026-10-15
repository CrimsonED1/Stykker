using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N74 – Proxy-Karte und Proxy-Dialog: was sie zeigen, rechnet ProxyPanel (Modellzahl dieses PCs, Name des Prüfmodells),
// und die Zahl der Quellen kommt aus Strings.PxSourceCounts.
[TestClass]
public class N74_ProxyPanelTests
{
    private static readonly ProxyChoice Llama = new("server-a", "llama-7b", "Llama 7B", false);
    private static readonly ProxyChoice Qwen = new("server-b", "qwen", "Qwen", false);
    private static readonly ProxyChoice Gemma = new("remote:http://127.0.0.1:18500", "gemma", "Gemma (other PC)", false);
    private static readonly ProxyChoice Cloud = new("provider:https://127.0.0.1/v1|c", "c", "Cloud/c", true);

    [TestMethod]
    public void LocalModelCount_CountsOnlyModelsOfThisPc()
    {
        Assert.AreEqual(2, ProxyPanel.LocalModelCount(new[] { Llama, Qwen, Gemma, Cloud }));
        Assert.AreEqual(0, ProxyPanel.LocalModelCount(Array.Empty<ProxyChoice>()));
    }

    [TestMethod]
    public void ClassifierLabel_EmptyMeansSameAsTheProxyTarget()
    {
        Assert.AreEqual(Strings.ClientAutoSame, ProxyPanel.ClassifierLabel("", new[] { Llama, Qwen }));
    }

    [TestMethod]
    public void ClassifierLabel_ShowsTheNameOfTheChosenModel()
    {
        Assert.AreEqual("Qwen", ProxyPanel.ClassifierLabel(Qwen.Value, new[] { Llama, Qwen }));
        Assert.AreEqual("Cloud/c", ProxyPanel.ClassifierLabel(Cloud.Value, new[] { Llama, Cloud }));
    }

    [TestMethod]
    public void ClassifierLabel_KeepsAChoiceThatIsNotListedNow()
    {
        Assert.AreEqual("server-z|gone", ProxyPanel.ClassifierLabel("server-z|gone", new[] { Llama, Qwen }));
    }

    [TestMethod]
    public void SourceCounts_AreSingularForOneAndPluralOtherwise()
    {
        Assert.AreEqual("1 machine · 1 cloud provider", Strings.PxSourceCounts(1, 1));
        Assert.AreEqual("0 machines · 2 cloud providers", Strings.PxSourceCounts(0, 2));
        Assert.AreEqual("3 machines · 0 cloud providers", Strings.PxSourceCounts(3, 0));
    }
}
