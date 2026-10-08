using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StykkerLlm.Core;

// Die Claude-App spricht die Anthropic-Messages-API (POST /v1/messages), die lokalen Backends sprechen OpenAI.
// Diese Klasse übersetzt beide Richtungen: die Anfrage nach OpenAI, die Antwort (ganzes JSON oder SSE) nach Anthropic.
// Sie macht keinen Netzzugriff und hält keinen Zustand außerhalb eines einzelnen Streams – damit ist sie ohne Server prüfbar.
public static class AnthropicBridge
{
    public const string Path = "/v1/messages";

    // Umlaute und Anführungszeichen bleiben lesbar (so wie Anthropic sie sendet); die Bedeutung des JSON ändert das nicht.
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Ist das die Adresse der Messages-API? (Abfrageteil und Groß-/Kleinschreibung egal)
    public static bool IsMessagesPath(string target)
    {
        int q = target.IndexOf('?');
        var path = q >= 0 ? target[..q] : target;
        return path.Equals(Path, StringComparison.OrdinalIgnoreCase);
    }

    // ── Anfrage: Anthropic → OpenAI ──

    // includeUsage: den Zielserver um die Schlussmeldung mit den Tokenzahlen bitten (stream_options).
    public static byte[] ToOpenAi(byte[] anthropicBody, string model, bool includeUsage = true)
    {
        JsonObject? a = null;
        try { a = JsonNode.Parse(anthropicBody.AsSpan()) as JsonObject; } catch { }
        if (a == null) return anthropicBody;

        var req = new JsonObject { ["model"] = model };
        var messages = new JsonArray();

        // „system" kommt als Text oder als Blockfeld; beides wird zu einer System-Nachricht
        var system = TextOf(a["system"]);
        if (system.Length > 0) messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });

        if (a["messages"] is JsonArray history)
            foreach (var m in history) AppendMessage(messages, m);
        req["messages"] = messages;

        foreach (var key in new[] { "max_tokens", "temperature", "top_p", "top_k" })
            if (a[key] is { } v) req[key] = v.DeepClone();

        if (a["stop_sequences"] is JsonArray stops && stops.Count > 0)
        {
            var stop = new JsonArray();
            foreach (var s in stops) stop.Add(s?.DeepClone());
            req["stop"] = stop;
        }

        if (BoolOf(a["stream"]))
        {
            req["stream"] = true;
            if (includeUsage) req["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        if (a["tools"] is JsonArray tools)
        {
            var list = new JsonArray();
            foreach (var t in tools)
            {
                if (t is not JsonObject to) continue;
                string name = StrOf(to["name"]);
                if (name.Length == 0) continue;
                var fn = new JsonObject { ["name"] = name };
                if (to["description"] is { } d) fn["description"] = d.DeepClone();
                fn["parameters"] = to["input_schema"]?.DeepClone() ?? new JsonObject { ["type"] = "object" };
                list.Add(new JsonObject { ["type"] = "function", ["function"] = fn });
            }
            if (list.Count > 0) req["tools"] = list;
        }

        if (ToolChoiceOf(a["tool_choice"]) is { } choice) req["tool_choice"] = choice;

        // Denken schaltet Anthropic über ein Objekt ein; llama.cpp über chat_template_kwargs
        if (a["thinking"] is JsonObject th && StrOf(th["type"]) == "enabled")
            req["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = true };

        return Encoding.UTF8.GetBytes(req.ToJsonString(Json));
    }

    // Eine Anthropic-Nachricht in OpenAI-Nachrichten übersetzen. Werkzeugergebnisse werden eigene „tool"-Nachrichten,
    // Werkzeugaufrufe der Antwort werden zu tool_calls. Denkblöcke der Vorgeschichte entfallen (die Backends nehmen sie nicht an).
    private static void AppendMessage(JsonArray messages, JsonNode? node)
    {
        if (node is not JsonObject m) return;
        string role = StrOf(m["role"]);
        if (role != "user" && role != "assistant") return;
        var content = m["content"];

        if (content is JsonValue)   // einfacher Text
        {
            string text = StrOf(content);
            if (text.Length > 0) messages.Add(new JsonObject { ["role"] = role, ["content"] = text });
            return;
        }
        if (content is not JsonArray blocks) return;

        var text2 = new StringBuilder();
        var parts = new JsonArray();          // nur belegt, wenn Bilder dabei sind
        var tools = new JsonArray();          // Werkzeugaufrufe (Assistenz)
        var results = new JsonArray();        // Werkzeugergebnisse (Nutzer)

        foreach (var b in blocks)
        {
            if (b is not JsonObject bo) continue;
            switch (StrOf(bo["type"]))
            {
                case "text":
                    AppendText(text2, StrOf(bo["text"]));
                    break;
                case "image":
                {
                    if (bo["source"] is JsonObject src && StrOf(src["type"]) == "base64")
                    {
                        string media = StrOf(src["media_type"]);
                        if (media.Length == 0) media = "image/png";
                        parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{media};base64,{StrOf(src["data"])}" } });
                    }
                    break;
                }
                case "tool_use":
                {
                    string name = StrOf(bo["name"]);
                    if (name.Length == 0) break;
                    tools.Add(new JsonObject
                    {
                        ["id"] = StrOf(bo["id"]) ?? "",
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = name,
                            ["arguments"] = bo["input"] is { } input ? input.ToJsonString(Json) : "{}",
                        },
                    });
                    break;
                }
                case "tool_result":
                    results.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = StrOf(bo["tool_use_id"]) ?? "",
                        ["content"] = TextOf(bo["content"]),
                    });
                    break;
            }
        }

        // Werkzeugergebnisse stehen vor der eigentlichen Nutzer-Nachricht
        foreach (var r in results)
            if (r is { } res) messages.Add(res.DeepClone());

        string text3 = text2.ToString();
        if (parts.Count == 0)
        {
            if (text3.Length == 0 && tools.Count == 0) return;
            var plain = new JsonObject { ["role"] = role, ["content"] = text3.Length > 0 ? text3 : null };
            if (tools.Count > 0) plain["tool_calls"] = tools;   // Text und Werkzeugaufrufe gehören in dieselbe Nachricht
            messages.Add(plain);
            return;
        }
        if (text3.Length > 0) parts.Insert(0, new JsonObject { ["type"] = "text", ["text"] = text3 });
        var msg = new JsonObject { ["role"] = role, ["content"] = parts };
        if (tools.Count > 0) msg["tool_calls"] = tools;
        messages.Add(msg);
    }

    private static void AppendText(StringBuilder sb, string text)
    {
        if (text.Length == 0) return;
        if (sb.Length > 0) sb.Append('\n');
        sb.Append(text);
    }

    private static JsonNode? ToolChoiceOf(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        return StrOf(o["type"]) switch
        {
            "auto" => (JsonNode)"auto",
            "any" => (JsonNode)"required",
            "none" => (JsonNode)"none",
            "tool" => new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = StrOf(o["name"]) ?? "" } },
            _ => null,
        };
    }

    // ── Antwort ohne Stream: OpenAI → Anthropic ──

    public static byte[] ToAnthropic(byte[] openAiBody, string model)
    {
        JsonObject? r = null;
        try { r = JsonNode.Parse(openAiBody.AsSpan()) as JsonObject; } catch { }
        if (r == null) return openAiBody;

        var choice = (r["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var msg = choice?["message"] as JsonObject;

        var content = new JsonArray();
        string thinking = TextOf(msg?["reasoning_content"]);
        if (thinking.Length == 0) thinking = TextOf(msg?["reasoning"]);
        if (thinking.Length > 0) content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = thinking });

        string text = StrOf(msg?["content"]);
        if (text.Length == 0) text = TextOf(msg?["content"]);
        if (text.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });

        if (msg?["tool_calls"] is JsonArray tcs)
            foreach (var tc in tcs)
            {
                if (tc is not JsonObject t) continue;
                var fn = t["function"] as JsonObject;
                string name = StrOf(fn?["name"]);
                if (name.Length == 0) continue;
                string id = StrOf(t["id"]);
                content.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = id.Length > 0 ? id : NewId("toolu_"),
                    ["name"] = name,
                    ["input"] = ParseObject(StrOf(fn?["arguments"])),
                });
            }

        var message = new JsonObject
        {
            ["id"] = NewId("msg_"),
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = model,
            ["content"] = content,
            ["stop_reason"] = StopReason(StrOf(choice?["finish_reason"])),
            ["stop_sequence"] = null,
            ["usage"] = UsageOf(r["usage"] as JsonObject),
        };
        return Encoding.UTF8.GetBytes(message.ToJsonString(Json));
    }

    // Anthropic-Fehlerobjekt: die App wertet „type" und „message" aus und zeigt den Text an
    public static byte[] ErrorJson(int status, string message)
    {
        string type = status switch
        {
            400 => "invalid_request_error",
            401 => "authentication_error",
            403 => "permission_error",
            404 => "not_found_error",
            413 => "request_too_large",
            429 => "rate_limit_error",
            529 => "overloaded_error",
            >= 500 => "api_error",
            _ => "invalid_request_error",
        };
        var obj = new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = type, ["message"] = message } };
        return Encoding.UTF8.GetBytes(obj.ToJsonString(Json));
    }

    private static JsonObject UsageOf(JsonObject? u) => new()
    {
        ["input_tokens"] = LongOf(u?["prompt_tokens"]) ?? 0,
        ["output_tokens"] = LongOf(u?["completion_tokens"]) ?? 0,
    };

    private static string StopReason(string? finish) => finish switch
    {
        "length" => "max_tokens",
        "tool_calls" or "function_call" => "tool_use",
        _ => "end_turn",
    };

    private static JsonNode ParseObject(string json)
    {
        if (json.Length > 0)
            try { if (JsonNode.Parse(json) is JsonObject o) return o; } catch { }
        return new JsonObject();
    }

    private static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..24];

    // ── Antwort als SSE: OpenAI-Ereignisse → Anthropic-Ereignisse ──

    // Nimmt die Zeilen eines OpenAI-SSE-Stroms an und liefert die Anthropic-Ereignisse, die an den Client gehen.
    // Blockreihenfolge: Denken, Antwort, dann je Werkzeugaufruf einer. Blöcke werden über content_block_start/-stop geöffnet und geschlossen.
    public sealed class AnthropicStream
    {
        private readonly string _model;
        private readonly string _id = NewId("msg_");
        private readonly StringBuilder _line = new();
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly Dictionary<int, int> _toolBlock = new();   // Werkzeugindex des Servers → Blockindex bei Anthropic
        private int _index = -1;          // zuletzt geöffneter Block; -1 = keiner
        private string _open = "";        // "thinking" | "text" | "tool" | ""
        private string _stop = "end_turn";
        private long _in, _out;
        private bool _finished;

        public AnthropicStream(string model) => _model = model;

        // Muss das erste Ereignis sein. input_tokens steht hier bei Anthropic; der Zielserver meldet die Zahl erst am Ende,
        // deshalb beginnt es bei 0 und wird im Abschluss nachgereicht.
        public string Begin()
        {
            var start = new JsonObject
            {
                ["type"] = "message_start",
                ["message"] = new JsonObject
                {
                    ["id"] = _id,
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["model"] = _model,
                    ["content"] = new JsonArray(),
                    ["stop_reason"] = null,
                    ["stop_sequence"] = null,
                    ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 },
                },
            };
            return Event("message_start", start.ToJsonString(Json));
        }

        // Ein Stück des Stroms; liefert die zu schreibenden Ereignisse ("" = nichts). Zeichen werden über Chunkgrenzen richtig dekodiert.
        public string Feed(ReadOnlySpan<byte> chunk)
        {
            if (chunk.Length == 0) return "";
            var sb = new StringBuilder();
            var chars = new char[Encoding.UTF8.GetMaxCharCount(chunk.Length)];
            int n = _decoder.GetChars(chunk, chars, false);
            for (int i = 0; i < n; i++)
            {
                char ch = chars[i];
                if (ch == '\n') { Line(_line.ToString(), sb); _line.Clear(); }
                else if (ch != '\r') _line.Append(ch);
            }
            return sb.ToString();
        }

        // Ende des Stroms: offenen Block schließen, Abschluss und Ende melden. Mehrfach aufrufbar.
        public string Finish()
        {
            if (_finished) return "";
            if (_line.Length > 0) { var rest = new StringBuilder(); Line(_line.ToString(), rest); _line.Clear(); _finished = true; return rest.ToString() + Close(); }
            _finished = true;
            return Close();
        }

        private string Close()
        {
            var sb = new StringBuilder();
            CloseBlock(sb);
            var delta = new JsonObject
            {
                ["delta"] = new JsonObject { ["stop_reason"] = _stop, ["stop_sequence"] = null },
                // input_tokens gehört bei Anthropic in message_start; der Zielserver meldet es erst am Ende.
                // Bekannte Felder liest die App, unbekannte übergeht sie.
                ["usage"] = new JsonObject { ["output_tokens"] = _out, ["input_tokens"] = _in },
            };
            sb.Append(Event("message_delta", delta.ToJsonString(Json)));
            sb.Append(Event("message_stop", "{\"type\":\"message_stop\"}"));
            return sb.ToString();
        }

        private void Line(string line, StringBuilder sb)
        {
            if (line.Length == 0) return;
            if (line[0] == ':') { sb.Append(Event("ping", "{\"type\":\"ping\"}")); return; }   // Lebenszeichen des Zielservers weitergeben
            if (!line.StartsWith("data:", StringComparison.Ordinal)) return;
            var payload = line[5..].Trim();
            if (payload.Length == 0 || payload == "[DONE]") return;

            JsonObject? r;
            try { r = JsonNode.Parse(payload) as JsonObject; } catch { return; }
            if (r == null) return;

            if (r["usage"] is JsonObject u)
            {
                _in = LongOf(u["prompt_tokens"]) ?? _in;
                _out = LongOf(u["completion_tokens"]) ?? _out;
            }

            var choice = (r["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
            if (choice?["delta"] is JsonObject d)
            {
                string thinking = TextOf(d["reasoning_content"]);
                if (thinking.Length == 0) thinking = TextOf(d["reasoning"]);
                if (thinking.Length > 0) { OpenBlock(sb, "thinking", -1, null, null); Delta(sb, "thinking_delta", "thinking", thinking); }

                string text = StrOf(d["content"]);
                if (text.Length > 0) { OpenBlock(sb, "text", -1, null, null); Delta(sb, "text_delta", "text", text); }

                if (d["tool_calls"] is JsonArray tcs)
                    foreach (var tc in tcs)
                    {
                        if (tc is not JsonObject t) continue;
                        int idx = IntOf(t["index"]) ?? 0;
                        var fn = t["function"] as JsonObject;
                        if (!_toolBlock.ContainsKey(idx)) OpenBlock(sb, "tool", idx, StrOf(t["id"]), StrOf(fn?["name"]));
                        string args = StrOf(fn?["arguments"]);
                        if (args.Length > 0) Delta(sb, "input_json_delta", "partial_json", args);
                    }
            }

            string finish = StrOf(choice?["finish_reason"]);
            if (finish.Length > 0) _stop = StopReason(finish);
        }

        // Einen Block öffnen. Außer bei Werkzeugen bleibt ein schon offener Block derselben Art bestehen.
        private void OpenBlock(StringBuilder sb, string kind, int toolIdx, string? toolId, string? toolName)
        {
            if (kind != "tool" && _open == kind) return;
            CloseBlock(sb);
            _index++;
            _open = kind;
            JsonObject block = kind switch
            {
                "thinking" => new JsonObject { ["type"] = "thinking", ["thinking"] = "" },
                "text" => new JsonObject { ["type"] = "text", ["text"] = "" },
                _ => new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = string.IsNullOrEmpty(toolId) ? NewId("toolu_") : toolId,
                    ["name"] = string.IsNullOrEmpty(toolName) ? "tool" : toolName,
                    ["input"] = new JsonObject(),
                },
            };
            if (kind == "tool") _toolBlock[toolIdx] = _index;
            sb.Append(Event("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = _index,
                ["content_block"] = block,
            }.ToJsonString(Json)));
        }

        private void CloseBlock(StringBuilder sb)
        {
            if (_index < 0 || _open.Length == 0) return;
            sb.Append(Event("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = _index }.ToJsonString(Json)));
            _open = "";
        }

        private void Delta(StringBuilder sb, string type, string field, string value)
        {
            var d = new JsonObject { ["type"] = type, [field] = value };
            sb.Append(Event("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = _index,
                ["delta"] = d,
            }.ToJsonString(Json)));
        }

        private static string Event(string type, string data) => $"event: {type}\ndata: {data}\n\n";
    }

    // ── kleine Helfer ──

    private static string StrOf(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    // Text aus Text, Textblock oder Blockliste ziehen
    private static string TextOf(JsonNode? n)
    {
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (n is not JsonArray arr) return "";
        var sb = new StringBuilder();
        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            string type = StrOf(o["type"]);
            if (type == "text") AppendText(sb, StrOf(o["text"]));
            else if (type == "tool_result") AppendText(sb, TextOf(o["content"]));
        }
        return sb.ToString();
    }

    private static bool BoolOf(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static int? IntOf(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    private static long? LongOf(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;
}