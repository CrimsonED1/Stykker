using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// R1.3 – Anthropic /v1/messages: SSE und Antworten ohne Stream richtig auswerten (Denken, Antwort, Werkzeuge, stop_reason, usage)
[TestClass]
public class R1_AnthropicTapTests
{
    private const string ThinkingToolStream = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"claude-x","usage":{"input_tokens":412,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Let me think about the weather."}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":" I should call the tool."}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"EqQBCg"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"I will look it up."}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: content_block_start
        data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"get_weather","input":{}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"city\":"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"\"Paris\"}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":2}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":60}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    [TestMethod]
    public void Stream_ThinkingTextToolUse_AreSeparated()
    {
        var tap = new ProxyTap { Stream = true };
        var t0 = DateTime.Now;
        foreach (var line in ThinkingToolStream.Replace("\r", "").Split('\n')) tap.Feed(Encoding.UTF8.GetBytes(line + "\n"), t0);
        tap.Complete(t0.AddSeconds(2));
        Assert.IsNotNull(tap.FirstReasoning);
        Assert.IsNotNull(tap.FirstContent);
        CollectionAssert.AreEqual(new[] { "get_weather" }, tap.Tools);
        Assert.AreEqual("tool_calls", tap.FinishReason);
        Assert.AreEqual(60, tap.PredictedN);
        // die 60 gemeldeten Ausgabe-Token verteilen sich nach Zeichenanteil auf Denken und Antwort
        Assert.AreEqual(60, tap.Reasoning + tap.Content);
        Assert.IsTrue(tap.Reasoning > tap.Content, $"mehr Denken ({tap.Reasoning}) als Antwort ({tap.Content})");
        Assert.IsNotNull(tap.FirstToken);
    }

    [TestMethod]
    public void Stream_TextOnly_EndTurn()
    {
        const string s = "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10}}}\n\n" +
                         "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
                         "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}\n\n" +
                         "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\" there\"}}\n\n" +
                         "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":2}}\n\n";
        var tap = new ProxyTap { Stream = true };
        tap.Feed(Encoding.UTF8.GetBytes(s), DateTime.Now);
        tap.Complete(DateTime.Now);
        Assert.AreEqual(0, tap.Reasoning);
        Assert.AreEqual(2, tap.Content);
        Assert.AreEqual("stop", tap.FinishReason);
        Assert.AreEqual(0, tap.Tools.Count);
        Assert.IsNull(tap.FirstReasoning);
    }

    [TestMethod]
    public void Stream_MaxTokens_IsLength()
    {
        const string s = "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"abc\"}}\n\n" +
                         "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"},\"usage\":{\"output_tokens\":1}}\n\n";
        var tap = new ProxyTap { Stream = true };
        tap.Feed(Encoding.UTF8.GetBytes(s), DateTime.Now);
        tap.Complete(DateTime.Now);
        Assert.AreEqual("length", tap.FinishReason);
    }

    [TestMethod]
    public void NoStream_Message_WithThinkingAndTool()
    {
        const string body = "{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[" +
                            "{\"type\":\"thinking\",\"thinking\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"signature\":\"x\"}," +
                            "{\"type\":\"text\",\"text\":\"bbbbbbbbbb\"}," +
                            "{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"read_file\",\"input\":{\"path\":\"a\"}}]," +
                            "\"stop_reason\":\"tool_use\",\"usage\":{\"input_tokens\":100,\"output_tokens\":50}}";
        var tap = new ProxyTap { Stream = false };
        tap.Feed(Encoding.UTF8.GetBytes(body), DateTime.Now);
        tap.Complete(DateTime.Now);
        Assert.AreEqual(50, tap.Reasoning + tap.Content);
        Assert.AreEqual(40, tap.Reasoning);
        Assert.AreEqual(10, tap.Content);
        CollectionAssert.AreEqual(new[] { "read_file" }, tap.Tools);
        Assert.AreEqual("tool_calls", tap.FinishReason);
    }

    [TestMethod]
    public void NoStream_Message_EndTurn_TextOnly()
    {
        const string body = "{\"type\":\"message\",\"content\":[{\"type\":\"text\",\"text\":\"Hi\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":5,\"output_tokens\":3}}";
        var tap = new ProxyTap { Stream = false };
        tap.Feed(Encoding.UTF8.GetBytes(body), DateTime.Now);
        tap.Complete(DateTime.Now);
        Assert.AreEqual(0, tap.Reasoning);
        Assert.AreEqual(3, tap.Content);
        Assert.AreEqual("stop", tap.FinishReason);
    }

    [TestMethod]
    public void OpenAiStream_IsUnaffected()
    {
        const string s = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"hm\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var tap = new ProxyTap { Stream = true };
        tap.Feed(Encoding.UTF8.GetBytes(s), DateTime.Now);
        tap.Complete(DateTime.Now);
        Assert.AreEqual(1, tap.Reasoning);
        Assert.AreEqual(1, tap.Content);
        Assert.AreEqual("stop", tap.FinishReason);
    }
}
