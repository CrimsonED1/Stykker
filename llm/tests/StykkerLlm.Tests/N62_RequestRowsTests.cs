using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Zeilen „Letzte Anfragen“ (docs/plan-ui-redesign.md, U7): Log-Zahlen plus die Beobachtung des Proxys
[TestClass]
public class N62_RequestRowsTests
{
    private static readonly DateTime T0 = new(2026, 10, 8, 12, 0, 0);

    private static FinishedRequest F(int seq, DateTime seen, ReqStatus st = ReqStatus.Done, string key = "127.0.0.1:8081") =>
        new(seq, "qwen", "qwen.gguf", seq, 0, 100, 500, 50, 200, 4, seen, st, "", 100, key);

    private static ProxyRecord P(DateTime end, string[] tools, int think, string ua = "opencode/0.9 (win)", string? finish = "stop",
        int status = 200, bool aborted = false, string key = "127.0.0.1:8081") =>
        new(key, "/v1/chat/completions", end.AddSeconds(-4), end, null, null, think, 200, tools, finish, ua, true, status, aborted);

    [TestMethod]
    public void ProxyRecord_IsMatchedByServerAndTime_AndAddsToolsThinkingClient()
    {
        var rows = RequestRows.Build(new[] { (F(1, T0), (string?)null), (F(2, T0.AddSeconds(30)), null) },
            new[] { P(T0.AddSeconds(1), new[] { "read", "edit" }, 350), P(T0.AddMinutes(5), Array.Empty<string>(), 0) });
        Assert.AreEqual(2, rows.Count);
        var older = rows[1];
        Assert.AreEqual(2, older.Tools);
        Assert.AreEqual(350, older.ThinkTokens);
        Assert.AreEqual("opencode", older.Client);
        Assert.AreEqual("agent", older.ClientKind);
        Assert.IsNull(rows[0].Tools, "ohne passende Proxy-Beobachtung: unbekannt, nicht 0");
    }

    [TestMethod]
    public void OtherServer_OrHostRequests_AreNotMatched()
    {
        var rows = RequestRows.Build(new[] { (F(1, T0), (string?)null), (F(2, T0, key: "x:1"), "GPU-BOX") },
            new[] { P(T0, new[] { "t" }, 0, key: "127.0.0.1:9999") });
        Assert.IsTrue(rows.All(r => r.Tools == null));
        Assert.AreEqual("GPU-BOX", rows.Single(r => r.ServerKey == "x:1").Host);
    }

    [TestMethod]
    public void Result_ComesFromStatusAndProxy()
    {
        Assert.AreEqual("ok", RequestRows.Result(F(1, T0), null));
        Assert.AreEqual("full", RequestRows.Result(F(1, T0, ReqStatus.Truncated), null));
        Assert.AreEqual("abort", RequestRows.Result(F(1, T0, ReqStatus.Cancelled), null));
        Assert.AreEqual("full", RequestRows.Result(F(1, T0), P(T0, Array.Empty<string>(), 0, finish: "length")));
        Assert.AreEqual("abort", RequestRows.Result(F(1, T0), P(T0, Array.Empty<string>(), 0, aborted: true)));
        Assert.AreEqual("err", RequestRows.Result(F(1, T0), P(T0, Array.Empty<string>(), 0, status: 500)));
    }

    [TestMethod]
    public void ClientKind_And_SpeedLevel()
    {
        Assert.AreEqual("editor", RequestRows.ClientKind("Code"));
        Assert.AreEqual("editor", RequestRows.ClientKind("cursor"));
        Assert.AreEqual("agent", RequestRows.ClientKind("opencode"));
        Assert.AreEqual("agent", RequestRows.ClientKind("Qwen Code"));
        Assert.AreEqual("web", RequestRows.ClientKind("Mozilla/5.0"));
        Assert.AreEqual("cli", RequestRows.ClientKind("curl"));
        Assert.AreEqual("cli", RequestRows.ClientKind(""));
        Assert.AreEqual("aider", RequestRows.ClientFromAgent("aider/0.60 python"));
        Assert.AreEqual(1, RequestRows.SpeedLevel(5));
        Assert.AreEqual(4, RequestRows.SpeedLevel(90));
    }
}
