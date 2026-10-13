using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class FramePolicyTests
{
    [TestMethod]
    public void WithoutHub_OnlySelf()
    {
        Assert.AreEqual("'self'", FramePolicy.Ancestors("http", "pc:8078", 0));
        Assert.AreEqual("'self'", FramePolicy.Ancestors("http", "pc:8078", 70000));
    }

    [TestMethod]
    public void WithHub_AllowsTheHubUnderTheSameHostName()
    {
        Assert.AreEqual("'self' http://pc:8079", FramePolicy.Ancestors("http", "pc:8078", 8079));
        Assert.AreEqual("'self' https://192.168.0.5:8079", FramePolicy.Ancestors("https", "192.168.0.5", 8079));
        Assert.AreEqual("'self' http://my-pc.tailnet.ts.net:8079", FramePolicy.Ancestors("http", "my-pc.tailnet.ts.net:8078", 8079));
        Assert.AreEqual("'self' http://[::1]:8079", FramePolicy.Ancestors("http", "[::1]:8078", 8079));
    }

    [TestMethod]
    public void AHostHeaderThatIsNotAName_FallsBackToSelf()
    {
        // Der Host-Kopf kommt vom Aufrufer: nichts davon darf in der Antwort landen
        foreach (var host in new[] { null, "", "pc; script-src *", "pc 'unsafe-inline'", "evil.example/path", "[::1", "[zz]:80", "a b", new string('a', 300) })
            Assert.AreEqual("'self'", FramePolicy.Ancestors("http", host, 8079), host);
    }

    [TestMethod]
    public void AnUnknownScheme_FallsBackToSelf()
    {
        Assert.AreEqual("'self'", FramePolicy.Ancestors("javascript", "pc", 8079));
    }
}
