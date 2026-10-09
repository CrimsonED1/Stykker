using System.Text;
using System.Text.Json;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// R4 – Übersetzung Anthropic <-> OpenAI: Anfrage, Antwort ohne Stream, SSE-Strom, Fehlerobjekt
[TestClass]
public class R4_AnthropicBridgeTests
{
    private const string Request = """
        {
          "model": "claude-stykker",
          "max_tokens": 1024,
          "temperature": 0.2,
          "stop_sequences": ["STOP"],
          "stream": true,
          "thinking": { "type": "enabled", "budget_tokens": 2048 },
          "system": [{ "type": "text", "text": "You are helpful." }],
          "tools": [{ "name": "get_weather", "description": "Weather", "input_schema": { "type": "object", "properties": { "city": { "type": "string" } }, "required": ["city"] } }],
          "tool_choice": { "type": "any" },
          "messages": [
            { "role": "user", "content": "Wetter in Paris?" },
            { "role": "assistant", "content": [
              { "type": "thinking", "thinking": "ich denke", "signature": "x" },
              { "type": "text", "text": "Ich schaue nach." },
              { "type": "tool_use", "id": "toolu_1", "name": "get_weather", "input": { "city": "Paris" } } ] },
            { "role": "user", "content": [
              { "type": "tool_result", "tool_use_id": "toolu_1", "content": "21 Grad" },
              { "type": "text", "text": "Und morgen?" } ] }
          ]
        }
        """;

    [TestMethod]
    public void Request_BecomesOpenAiChatRequest()
    {
        var openAi = AnthropicBridge.ToOpenAi(Encoding.UTF8.GetBytes(Request), "local-model");

        using var doc = JsonDocument.Parse(openAi);
        var root = doc.RootElement;
        Assert.AreEqual("local-model", root.GetProperty("model").GetString());
        Assert.AreEqual(1024, root.GetProperty("max_tokens").GetInt32());
        Assert.AreEqual(0.2, root.GetProperty("temperature").GetDouble(), 0.001);
        Assert.IsTrue(root.GetProperty("stream").GetBoolean());
        Assert.IsTrue(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.AreEqual("STOP", root.GetProperty("stop")[0].GetString());
        Assert.AreEqual("required", root.GetProperty("tool_choice").GetString());
        Assert.IsTrue(root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());

        var tool = root.GetProperty("tools")[0];
        Assert.AreEqual("function", tool.GetProperty("type").GetString());
        Assert.AreEqual("get_weather", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.AreEqual("string", tool.GetProperty("function").GetProperty("parameters").GetProperty("properties").GetProperty("city").GetProperty("type").GetString());

        var msgs = root.GetProperty("messages");
        Assert.AreEqual(5, msgs.GetArrayLength());
        Assert.AreEqual("system", msgs[0].GetProperty("role").GetString());
        Assert.AreEqual("You are helpful.", msgs[0].GetProperty("content").GetString());
        Assert.AreEqual("user", msgs[1].GetProperty("role").GetString());
        Assert.AreEqual("Wetter in Paris?", msgs[1].GetProperty("content").GetString());

        // Denkblock der Vorgeschichte entfällt, Text und Werkzeugaufruf bleiben
        var assistant = msgs[2];
        Assert.AreEqual("assistant", assistant.GetProperty("role").GetString());
        Assert.AreEqual("Ich schaue nach.", assistant.GetProperty("content").GetString());
        var call = assistant.GetProperty("tool_calls")[0];
        Assert.AreEqual("toolu_1", call.GetProperty("id").GetString());
        Assert.AreEqual("get_weather", call.GetProperty("function").GetProperty("name").GetString());
        Assert.AreEqual("{\"city\":\"Paris\"}", call.GetProperty("function").GetProperty("arguments").GetString());

        // Werkzeugergebnis wird eine eigene „tool"-Nachricht, vor der Nutzer-Nachricht
        Assert.AreEqual("tool", msgs[3].GetProperty("role").GetString());
        Assert.AreEqual("toolu_1", msgs[3].GetProperty("tool_call_id").GetString());
        Assert.AreEqual("21 Grad", msgs[3].GetProperty("content").GetString());
        Assert.AreEqual("user", msgs[4].GetProperty("role").GetString());
        Assert.AreEqual("Und morgen?", msgs[4].GetProperty("content").GetString());
    }

    [TestMethod]
    public void Response_BecomesAnthropicMessage()
    {
        const string openAi = """
            { "id": "x", "model": "local", "choices": [ { "index": 0, "finish_reason": "tool_calls", "message": {
                "role": "assistant", "content": "Ich rufe das Wetter ab.", "reasoning_content": "kurz gedacht",
                "tool_calls": [ { "id": "call_1", "type": "function", "function": { "name": "get_weather", "arguments": "{\"city\":\"Paris\"}" } } ] } } ],
              "usage": { "prompt_tokens": 412, "completion_tokens": 37 } }
            """;

        var anthropic = AnthropicBridge.ToAnthropic(Encoding.UTF8.GetBytes(openAi), "claude-stykker");

        using var doc = JsonDocument.Parse(anthropic);
        var root = doc.RootElement;
        Assert.AreEqual("message", root.GetProperty("type").GetString());
        Assert.AreEqual("assistant", root.GetProperty("role").GetString());
        Assert.AreEqual("claude-stykker", root.GetProperty("model").GetString());
        Assert.AreEqual("tool_use", root.GetProperty("stop_reason").GetString());
        Assert.AreEqual(412, root.GetProperty("usage").GetProperty("input_tokens").GetInt64());
        Assert.AreEqual(37, root.GetProperty("usage").GetProperty("output_tokens").GetInt64());

        var blocks = root.GetProperty("content");
        Assert.AreEqual(3, blocks.GetArrayLength());
        Assert.AreEqual("thinking", blocks[0].GetProperty("type").GetString());
        Assert.AreEqual("kurz gedacht", blocks[0].GetProperty("thinking").GetString());
        Assert.AreEqual("text", blocks[1].GetProperty("type").GetString());
        Assert.AreEqual("Ich rufe das Wetter ab.", blocks[1].GetProperty("text").GetString());
        Assert.AreEqual("tool_use", blocks[2].GetProperty("type").GetString());
        Assert.AreEqual("call_1", blocks[2].GetProperty("id").GetString());
        Assert.AreEqual("get_weather", blocks[2].GetProperty("name").GetString());
        Assert.AreEqual("Paris", blocks[2].GetProperty("input").GetProperty("city").GetString());
    }

    private const string TextToolStream = """
        data: {"id":"1","choices":[{"index":0,"delta":{"role":"assistant","content":"Hallo"}}]}

        data: {"id":"1","choices":[{"index":0,"delta":{"content":" Welt"}}]}

        data: {"id":"1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"get_weather","arguments":"{\"ci"}}]}}]}

        data: {"id":"1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"ty\":\"Paris\"}"}}]}}]}

        data: {"id":"1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

        data: {"id":"1","choices":[],"usage":{"prompt_tokens":100,"completion_tokens":12}}

        data: [DONE]

        """;

    [TestMethod]
    public void Stream_TextAndToolUse_BecomesAnthropicEvents()
    {
        var text = Run(TextToolStream, "claude-stykker");

        Assert.IsTrue(text.StartsWith("event: message_start\n"), "message_start muss zuerst kommen");
        Assert.AreEqual(2, Count(text, "event: content_block_start\n"));
        Assert.AreEqual(2, Count(text, "event: content_block_stop\n"));
        Assert.AreEqual(1, Count(text, "event: message_delta\n"));
        Assert.IsTrue(text.EndsWith("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"));

        Assert.IsTrue(text.Contains("\"type\":\"text\",\"text\":\"\""), text);
        Assert.IsTrue(text.Contains("\"type\":\"text_delta\",\"text\":\"Hallo\""), text);
        Assert.IsTrue(text.Contains("\"type\":\"text_delta\",\"text\":\" Welt\""), text);
        // Werkzeug: Block mit Namen, Argumente stückweise, gleicher Blockindex
        Assert.IsTrue(text.Contains("\"type\":\"tool_use\",\"id\":\"call_1\",\"name\":\"get_weather\",\"input\":{}"), text);
        Assert.IsTrue(text.Contains("\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"ci\""), text);
        Assert.IsTrue(text.Contains("\"type\":\"input_json_delta\",\"partial_json\":\"ty\\\":\\\"Paris\\\"}\""), text);
        // Abschluss: Grund und Zahlen des Servers
        Assert.IsTrue(text.Contains("\"stop_reason\":\"tool_use\""), text);
        Assert.IsTrue(text.Contains("\"output_tokens\":12"), text);
        Assert.IsTrue(text.Contains("\"input_tokens\":100"), text);
    }

    [TestMethod]
    public void Stream_ThinkingThenText_SwitchesBlocks()
    {
        const string stream = """
            data: {"id":"1","choices":[{"index":0,"delta":{"reasoning_content":"erst denken"}}]}

            data: {"id":"1","choices":[{"index":0,"delta":{"content":"dann antworten"}}]}

            data: {"id":"1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var text = Run(stream, "claude-stykker");

        int thinking = text.IndexOf("\"content_block\":{\"type\":\"thinking\"", StringComparison.Ordinal);
        int thinkingStop = text.IndexOf("event: content_block_stop\n", StringComparison.Ordinal);
        int reply = text.IndexOf("\"content_block\":{\"type\":\"text\"", StringComparison.Ordinal);
        Assert.IsTrue(thinking >= 0 && thinkingStop > thinking && reply > thinkingStop, "Denkblock schließen, bevor der Textblock beginnt");
        Assert.IsTrue(text.Contains("\"index\":0,\"content_block\":{\"type\":\"thinking\""));
        Assert.IsTrue(text.Contains("\"index\":1,\"content_block\":{\"type\":\"text\""));
        Assert.IsTrue(text.Contains("\"stop_reason\":\"end_turn\""));
    }

    [TestMethod]
    public void Stream_SplitMultibyteChar_StaysIntact()
    {
        // „Grüße in zwei Stücken: das ü (0xC3 0xBC) wird über die Chunkgrenze geteilt
        var chunk1 = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Gr\u00fc");
        var chunk2 = Encoding.UTF8.GetBytes("\u00dfe\"}}]}\n\ndata: [DONE]\n\n");

        var s = new AnthropicBridge.AnthropicStream("m");
        var text = s.Begin() + s.Feed(chunk1) + s.Feed(chunk2) + s.Finish();

        Assert.IsTrue(text.Contains("\"text_delta\",\"text\":\"Gr\u00fc\u00dfe\""), text);
        Assert.IsFalse(text.Contains('\ufffd'), "kein Ersatzzeichen – die Chunkgrenze darf das Zeichen nicht zerteilen");
    }

    [TestMethod]
    public void IsMessagesPath_IgnoresQueryAndCase()
    {
        Assert.IsTrue(AnthropicBridge.IsMessagesPath("/v1/messages"));
        Assert.IsTrue(AnthropicBridge.IsMessagesPath("/v1/messages?beta=true"));
        Assert.IsTrue(AnthropicBridge.IsMessagesPath("/V1/Messages"));
        Assert.IsFalse(AnthropicBridge.IsMessagesPath("/v1/chat/completions"));
        Assert.IsFalse(AnthropicBridge.IsMessagesPath("/v1/messages/x"));
    }

    [TestMethod]
    public void ErrorJson_MapsStatusToAnthropicType()
    {
        Assert.AreEqual("invalid_request_error", TypeOf(400));
        Assert.AreEqual("rate_limit_error", TypeOf(429));
        Assert.AreEqual("api_error", TypeOf(503));
        Assert.AreEqual("overloaded_error", TypeOf(529));

        static string TypeOf(int status)
        {
            using var doc = JsonDocument.Parse(AnthropicBridge.ErrorJson(status, "kaputt"));
            var root = doc.RootElement;
            Assert.AreEqual("error", root.GetProperty("type").GetString());
            Assert.AreEqual("kaputt", root.GetProperty("error").GetProperty("message").GetString());
            return root.GetProperty("error").GetProperty("type").GetString()!;
        }
    }

    private static string Run(string openAiStream, string model)
    {
        var s = new AnthropicBridge.AnthropicStream(model);
        var sb = new StringBuilder(s.Begin());
        foreach (var line in openAiStream.Replace("\r", "").Split('\n'))
            sb.Append(s.Feed(Encoding.UTF8.GetBytes(line + "\n")));
        sb.Append(s.Finish());
        return sb.ToString();
    }

    private static int Count(string text, string needle)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}