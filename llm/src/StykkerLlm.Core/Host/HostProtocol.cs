using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StykkerLlm.Core;

// Die Verbindung Host → Server (docs/plan-hosts-gateway.md): ein WebSocket, den der Host aufbaut. Textnachrichten mit
// JSON, das Feld "t" sagt, was es ist. Der Host meldet sich beim Aufbau mit seinem Token im Kopf X-Stykker-Host.
//   hello      Host → Server   { t, name, version }                      zuerst
//   state      Host → Server   { t, state }                              jede Sekunde, state wie /api/state des Hosts
//   cmd        Server → Host   { t, id, name, args }                     Befehl (start, stop, unload, models)
//   reply      Host → Server   { t, id, ok, message, data }              Antwort auf cmd
//   http       Server → Host   { t, id, method, url, headers, body }     Anfrage an einen Modellserver des Hosts (Tunnel)
//   http-head  Host → Server   { t, id, status, headers }                Kopf der Antwort
//   http-body  Host → Server   { t, id, data }                           ein Stück der Antwort (base64), gestreamt
//   http-end   Host → Server   { t, id, error? }                         Ende (mit Fehler, wenn es keine Antwort gab)
//   http-cancel Server → Host  { t, id }                                 Anfrage abbrechen (Client weg)
public static class HostProtocol
{
    public const string Path = "/hosts/connect";
    public const string TokenHeader = "X-Stykker-Host";
    public const int MaxMessageBytes = 8 * 1024 * 1024;
    public const int BodyChunk = 16 * 1024;

    public static string Hello(string name, string version) =>
        new JsonObject { ["t"] = "hello", ["name"] = name, ["version"] = version }.ToJsonString();

    public static string State(string stateJson) => "{\"t\":\"state\",\"state\":" + stateJson + "}";

    public static string Cmd(string id, string name, JsonObject? args = null) =>
        new JsonObject { ["t"] = "cmd", ["id"] = id, ["name"] = name, ["args"] = args ?? new JsonObject() }.ToJsonString();

    public static string Reply(string id, bool ok, string message, JsonNode? data = null) =>
        new JsonObject { ["t"] = "reply", ["id"] = id, ["ok"] = ok, ["message"] = message, ["data"] = data }.ToJsonString();

    public static string Http(string id, string method, string url, IEnumerable<KeyValuePair<string, string>> headers, byte[]? body)
    {
        var h = new JsonObject();
        foreach (var (k, v) in headers) h[k] = v;
        return new JsonObject
        {
            ["t"] = "http", ["id"] = id, ["method"] = method, ["url"] = url, ["headers"] = h,
            ["body"] = body is { Length: > 0 } ? Convert.ToBase64String(body) : null,
        }.ToJsonString();
    }

    public static string HttpHead(string id, int status, IEnumerable<KeyValuePair<string, string>> headers)
    {
        var h = new JsonObject();
        foreach (var (k, v) in headers) h[k] = v;
        return new JsonObject { ["t"] = "http-head", ["id"] = id, ["status"] = status, ["headers"] = h }.ToJsonString();
    }

    public static string HttpBody(string id, ReadOnlySpan<byte> data) =>
        "{\"t\":\"http-body\",\"id\":" + JsonSerializer.Serialize(id) + ",\"data\":\"" + Convert.ToBase64String(data) + "\"}";

    public static string HttpEnd(string id, string? error = null) =>
        new JsonObject { ["t"] = "http-end", ["id"] = id, ["error"] = error }.ToJsonString();

    public static string HttpCancel(string id) => new JsonObject { ["t"] = "http-cancel", ["id"] = id }.ToJsonString();

    // Typ einer Nachricht ("" bei Unsinn)
    public static string TypeOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";

    public static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static Dictionary<string, string> Headers(JsonElement root)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
            foreach (var p in h.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) d[p.Name] = p.Value.GetString() ?? "";
        return d;
    }

    // Senden mit Sperre: auf einem WebSocket darf immer nur einer gleichzeitig senden
    public static async Task SendAsync(WebSocket ws, string text, CancellationToken ct, SemaphoreSlim? gate = null)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (gate != null) await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false); }
        finally { gate?.Release(); }
    }

    // Eine ganze Textnachricht lesen (auch über mehrere Rahmen). null = Verbindung zu (oder zu groß).
    public static async Task<string?> ReceiveAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > MaxMessageBytes) return null;
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
    }
}

// Antwort des Hosts auf einen Befehl
public sealed record HostReply(bool Ok, string Message, JsonElement? Data = null);
