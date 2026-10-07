using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StykkerLlm.Core;

// Die Verbindung Host → Server (docs/plan-hosts-gateway.md, P2): ein WebSocket, den der Host aufbaut. Textnachrichten
// mit JSON, das Feld "t" sagt, was es ist:
//   hello  (Host → Server, zuerst)     { t, name, version }
//   state  (Host → Server, jede Sek.)  { t, state }   – state ist der Zustand wie /api/state des Hosts
//   cmd    (Server → Host, ab P4)      { t, id, name, … }
//   reply  (Host → Server, ab P4)      { t, id, ok, message }
// Der Host meldet sich beim Verbindungsaufbau mit seinem Token im Kopf X-Stykker-Host.
public static class HostProtocol
{
    public const string Path = "/hosts/connect";
    public const string TokenHeader = "X-Stykker-Host";
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    public static string Hello(string name, string version) =>
        new JsonObject { ["t"] = "hello", ["name"] = name, ["version"] = version }.ToJsonString();

    public static string State(string stateJson) =>
        "{\"t\":\"state\",\"state\":" + stateJson + "}";

    // Typ einer Nachricht ("" bei Unsinn)
    public static string TypeOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";

    public static async Task SendAsync(WebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
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
