using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N73 – proxy.csv: jede Anfrage über den Proxy mit dem gewünschten Modellnamen und dem Ziel (auch Cloud-Ziele)
[TestClass]
public class N73_ProxyLogTests
{
    [TestMethod]
    public void A_request_is_written_with_requested_model_and_target()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stykker-n73-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new ProxyLog(Path.Combine(dir, "proxy.csv"));
            var t = new DateTime(2026, 10, 15, 12, 0, 0);
            log.Append(new ProxyRecord("k", "/v1/messages", t, t.AddSeconds(2.5), null, null, 10, 32, Array.Empty<string>(), "stop",
                "claude-cli/2", true, 200, false, "claude-sonnet-5"), "NVIDIA · glm, flash");
            var lines = File.ReadAllLines(Path.Combine(dir, "proxy.csv"));
            Assert.AreEqual(ProxyLog.Header, lines[0]);
            Assert.AreEqual("2026-10-15 12:00:00,claude-sonnet-5,\"NVIDIA · glm, flash\",/v1/messages,claude-cli/2,200,2.5,42,no", lines[1]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [TestMethod]
    public void The_requested_model_is_read_from_the_body_only()
    {
        Assert.AreEqual("claude-sonnet-5", RouterProxy.ReadModel("{\"model\":\"claude-sonnet-5\",\"messages\":[]}"u8.ToArray()));
        Assert.IsNull(RouterProxy.ReadModel(null));
    }
}
