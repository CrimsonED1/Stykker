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
            Assert.AreEqual("2026-10-15 12:00:00,claude-sonnet-5,\"NVIDIA · glm, flash\",/v1/messages,claude-cli/2,200,2.5,42,no,-1,-1,,", lines[1]);
            log.Append(new ProxyRecord("k", "/v1/messages", t, t, null, null, 0, 1, Array.Empty<string>(), null, "c", false, 200, false,
                "claude-stykker", RequestShape.Read("{\"max_tokens\":64,\"messages\":[{},{}],\"tools\":[{\"name\":\"verdict\"}]}"u8.ToArray())), "x");
            StringAssert.EndsWith(File.ReadAllLines(Path.Combine(dir, "proxy.csv"))[2], ",64,2,verdict,");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [TestMethod]
    public void Debug_keeps_the_body_and_only_the_last_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stykker-n73-" + Guid.NewGuid().ToString("N"));
        try
        {
            var t = new DateTime(2026, 10, 15, 12, 0, 0);
            for (int i = 0; i < ProxyLog.KeepDebug + 3; i++)
                ProxyLog.WriteDebug(dir, new ProxyRecord("k", "/v1/messages", t.AddSeconds(i), t.AddSeconds(i), null, null, 0, 0, Array.Empty<string>(), null, "c",
                    false, 200, false, "claude/x"), "{\"a\":1}"u8.ToArray());
            var files = Directory.GetFiles(dir);
            Assert.AreEqual(ProxyLog.KeepDebug, files.Length);
            Assert.IsTrue(files.All(f => Path.GetFileName(f).EndsWith("-claude_x.json")));
            Assert.IsFalse(files.Any(f => Path.GetFileName(f).StartsWith("20261015-120000-")), "die ältesten fallen weg");
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
