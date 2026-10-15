using System.Text.Json;

namespace StykkerLlm.Core;

// Der Aufbau einer Anfrage ohne ihren Text: max_tokens, Zahl der Nachrichten, Namen der angebotenen Werkzeuge.
// Für proxy.csv – daran lässt sich z. B. die Sicherheitsprüfung von Claude Code von einem Gespräch unterscheiden.
public sealed record RequestShape(int MaxTokens, int Messages, string Tools)
{
    public static readonly RequestShape None = new(-1, -1, "");

    public static RequestShape Read(byte[]? body)
    {
        if (body == null || body.Length == 0) return None;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return None;
            int max = -1;
            foreach (var name in new[] { "max_tokens", "max_completion_tokens" })
                if (root.TryGetProperty(name, out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var v)) { max = v; break; }
            int messages = root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array ? msgs.GetArrayLength() : -1;
            var tools = new List<string>();
            if (root.TryGetProperty("tools", out var ts) && ts.ValueKind == JsonValueKind.Array)
                foreach (var t in ts.EnumerateArray())
                {
                    // Anthropic: {"name":…}; OpenAI: {"function":{"name":…}}
                    var n = t.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString()
                        : t.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object && f.TryGetProperty("name", out var fn) && fn.ValueKind == JsonValueKind.String ? fn.GetString()
                        : null;
                    if (!string.IsNullOrEmpty(n)) tools.Add(n);
                }
            // Viele Werkzeuge (ein Gespräch von Claude Code bietet Dutzende an): nur die Zahl und die ersten Namen
            string list = tools.Count <= 6 ? string.Join(" ", tools) : $"{tools.Count}: " + string.Join(" ", tools.Take(4)) + " …";
            return new RequestShape(max, messages, list);
        }
        catch (JsonException) { return None; }
    }
}
