using System.Net;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Der Hinweis zur Rückfrage, so wie der Client ihn aus der Antwort des Servers liest (ohne Netz)
[TestClass]
public class ShutdownHintClientTests
{
    private const string Route = "127.0.0.1:17400/api/shutdown-outlook";

    [TestMethod]
    public async Task Hint_ReadsNoteWarningAndProxyCount()
    {
        var fake = new FakeHandler();
        fake.Routes[Route] = (HttpStatusCode.OK,
            "{\"wouldStop\":true,\"note\":\"Quitting shuts the server down.\",\"warning\":\"A request through the proxy is running.\",\"proxyActive\":2}");
        using var client = ServerClient.ForDevice("http://127.0.0.1:17400", "token", fake);
        var hint = await client.ShutdownHintAsync("ui:7", 1);
        Assert.IsNotNull(hint);
        Assert.AreEqual("Quitting shuts the server down.", hint.Note);
        Assert.AreEqual("A request through the proxy is running.", hint.Warning);
        Assert.AreEqual(2, hint.ProxyActive);
    }

    [TestMethod]
    public async Task Hint_WithoutWarning_CountsNoRequest()
    {
        var fake = new FakeHandler();
        fake.Routes[Route] = (HttpStatusCode.OK,
            "{\"wouldStop\":false,\"note\":\"Quitting keeps the server running.\",\"warning\":null,\"proxyActive\":0}");
        using var client = ServerClient.ForDevice("http://127.0.0.1:17400", "token", fake);
        var hint = await client.ShutdownHintAsync("ui:7", 0);
        Assert.IsNotNull(hint);
        Assert.IsNull(hint.Warning);
        Assert.AreEqual(0, hint.ProxyActive);
    }

    [TestMethod]
    public async Task Hint_ServerNotReachable_IsNull()
    {
        var fake = new FakeHandler();                    // keine Route: der Fake meldet „connection refused“
        using var client = ServerClient.ForDevice("http://127.0.0.1:17400", "token", fake);
        Assert.IsNull(await client.ShutdownHintAsync("ui:7", 0));
    }
}
