using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StykkerLlm.Core;

// Ein Backend, an das der Router weiterleiten kann. Model ist der Name, den das Backend selbst führt (alias bzw. Modellname);
// PublicModel ist der Name, unter dem der Client es sieht (bei Namensgleichheit bzw. Cloud „<Anbieter>/<Modell>"). ServerKey ist der
// besitzende lokale Server (für die Aufnahme), Remote markiert Modelle eines angehängten Rechners, Cloud die eines Anbieters im Netz,
// DisplayName dient der Anzeige.
public sealed record ProxyTarget(string Key, string Url, string? Model, BackendKind Kind, bool Ready, bool Loading,
    string ServerKey = "", bool Remote = false, string? PublicModel = null, string DisplayName = "", bool Cloud = false)
{
    // Name, unter dem das Modell angeboten wird (Adressierung durch den Client)
    public string? EffectiveModel => string.IsNullOrEmpty(PublicModel) ? Model : PublicModel;
    // Kennung für die Zuordnung der Aufnahme: der lokale Server, sonst die Ziel-Kennung
    public string RouteKey => ServerKey.Length > 0 ? ServerKey : Key;

    // Zielkennung eines Cloud-Modells – sie trägt das Modell schon, deshalb wird kein Modell nachgetragen
    public static string CloudKey(string baseUrl, string model) => $"provider:{baseUrl}|{model}";
    public static bool IsCloudKey(string key) => key.StartsWith("provider:", StringComparison.Ordinal);
}

// Der „Stykker-Proxy": genau ein Proxy auf einem festen Port. Er beantwortet /v1/models selbst (alle Backends zusammen plus den
// virtuellen Eintrag „stykker") und leitet Generierungs-Anfragen nach dem Feld „model" an das passende Backend weiter. „stykker",
// ein leeres oder unbekanntes Modell geht an das Standardziel. Ist das Ziel offline, kommt 503 mit JSON-Fehler; lädt das Ziel
// gerade sein Modell, wartet der Proxy bis zu LoadingWait darauf.
public class RouterProxy : RequestProxy
{
    public const string VirtualModel = "stykker";

    private volatile ProxyTarget[] _targets = Array.Empty<ProxyTarget>();
    private volatile string? _defaultKey;

    // So lange auf ein noch ladendes Ziel warten, statt sofort 503 zu antworten
    public TimeSpan LoadingWait { get; set; } = TimeSpan.FromSeconds(120);

    // Der API-Schlüssel eines Cloud-Ziels. Der Proxy setzt ihn selbst als Authorization-Kopfzeile; der Schlüssel des Clients
    // wird dabei nicht weitergereicht (er gehört zu stykker, nicht zum Anbieter). Der Wert verlässt diese Schicht nicht.
    public Func<ProxyTarget, string?>? AuthFor { get; set; }

    public RouterProxy(int listenPort) : base(VirtualModel, "", listenPort) { }

    // Wird im Takt der Oberfläche neu gesetzt (Server können kommen und gehen)
    public void SetTargets(IReadOnlyList<ProxyTarget> targets, string? defaultKey)
    {
        _targets = targets.ToArray();
        _defaultKey = defaultKey;
    }

    protected override async Task<ProxyRoute> RouteAsync(string method, string target, IReadOnlyList<(string Name, string Value)> headers, byte[]? body, CancellationToken ct)
    {
        var targets = _targets;
        int q = target.IndexOf('?');
        string path = q >= 0 ? target[..q] : target;

        // Modellliste zusammenfassen (der Client sieht so alle Backends auf einmal)
        if (method == "GET" && (path.Equals("/v1/models", StringComparison.OrdinalIgnoreCase) || path.Equals("/models", StringComparison.OrdinalIgnoreCase)))
            return Local(200, ModelsJson(targets));

        string? wanted = ReadModel(body);
        ProxyTarget? t = null;
        if (!string.IsNullOrWhiteSpace(wanted) && !wanted.Equals(VirtualModel, StringComparison.OrdinalIgnoreCase))
            t = targets.FirstOrDefault(x => Matches(x, wanted));
        t ??= DefaultTarget(targets);   // stykker, leer, unbekannt oder nicht gefunden → Standardziel

        if (t == null) return Local(503, ErrorJson(Strings.ProxyNoTarget));
        if (t.Cloud && AuthFor?.Invoke(t) is not { Length: > 0 }) return Local(503, ErrorJson(Strings.ProxyProviderNoKey));
        if (t.Loading) t = await WaitForReadyAsync(t.Key, t, ct).ConfigureAwait(false);
        if (t == null || !t.Ready) return Local(503, ErrorJson(t != null && t.Cloud ? Strings.ProxyProviderOfflineProxy : Strings.ProxyTargetOffline));
        if (t.Loading) return Local(503, ErrorJson(Strings.ProxyTargetLoading));   // immer noch am Laden (Zeit abgelaufen)

        // model auf den Namen setzen, den das Ziel erwartet (bei „stykker" → Modell des Ziels)
        var rewritten = t.Model != null ? RewriteModel(body, t.Model) : body;
        var own = t.Cloud && AuthFor?.Invoke(t) is { Length: > 0 } key
            ? new[] { ("Authorization", "Bearer " + key) } : null;
        return new ProxyRoute(t.Url, t.RouteKey, rewritten, Headers: own);
    }

    // Ein Modellname passt auf das angebotene Modell oder auf den Namen, den das Backend selbst führt
    private static bool Matches(ProxyTarget x, string wanted) =>
        (x.Model != null && x.Model.Equals(wanted, StringComparison.OrdinalIgnoreCase))
        || (x.PublicModel != null && x.PublicModel.Equals(wanted, StringComparison.OrdinalIgnoreCase));

    // Ein noch ladendes Ziel: bis LoadingWait warten, bis der Beobachter es als bereit meldet. Platzhalter-Zustand der Oberfläche
    // wird bei jedem Takt ersetzt, deshalb hier die jeweils aktuelle Fassung nachschlagen.
    private async Task<ProxyTarget?> WaitForReadyAsync(string key, ProxyTarget initial, CancellationToken ct)
    {
        var end = DateTime.UtcNow + LoadingWait;
        var t = initial;
        while (DateTime.UtcNow < end)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            var cur = _targets.FirstOrDefault(x => x.Key == key);
            if (cur == null) return null;   // Ziel ist verschwunden
            t = cur;
            if (cur.Ready || !cur.Loading) return cur;
        }
        return t;
    }

    private ProxyTarget? DefaultTarget(IReadOnlyList<ProxyTarget> targets)
    {
        var key = _defaultKey;
        if (!string.IsNullOrEmpty(key))
        {
            var exact = targets.FirstOrDefault(x => x.Key == key);
            if (exact != null) return exact;
        }
        // Ohne gesetztes Ziel: nur lokale Modelle, dann die angehängten Stykker-Rechner. Ein Cloud-Modell wird nie automatisch
        // gewählt – es verlässt den Rechner und muss ausdrücklich als Ziel eingetragen sein (Nutzerentscheidung zu Nr. 46).
        var local = targets.Where(x => !x.Remote && !x.Cloud).ToList();
        return local.FirstOrDefault(x => x.Kind == BackendKind.LlamaCpp && x.Ready)
            ?? local.FirstOrDefault(x => x.Ready)
            ?? local.FirstOrDefault()
            ?? targets.FirstOrDefault(x => !x.Cloud);
    }

    // „model" aus dem Anfragetext (nur dieses eine Feld; niemals der restliche Inhalt)
    internal static string? ReadModel(byte[]? body)
    {
        if (body == null || body.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : null;
        }
        catch { return null; }
    }

    // Nur das Feld „model" ersetzen, alles andere unverändert übernehmen
    internal static byte[] RewriteModel(byte[]? body, string model)
    {
        if (body == null || body.Length == 0) return Array.Empty<byte>();
        try
        {
            if (JsonNode.Parse(body) is JsonObject obj)
            {
                obj["model"] = model;
                return Encoding.UTF8.GetBytes(obj.ToJsonString());
            }
        }
        catch { }
        return body;
    }

    private static ProxyRoute Local(int status, string json) => new("", VirtualModel, null, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), status);

    private static string ModelsJson(IReadOnlyList<ProxyTarget> targets)
    {
        var sb = new StringBuilder();
        sb.Append("{\"object\":\"list\",\"data\":[");
        sb.Append("{\"id\":\"").Append(VirtualModel).Append("\",\"object\":\"model\",\"owned_by\":\"stykker\"}");
        foreach (var m in targets.Select(t => t.EffectiveModel).Where(m => !string.IsNullOrEmpty(m)).Distinct(StringComparer.OrdinalIgnoreCase))
            sb.Append(",{\"id\":\"").Append(JsonEscape(m!)).Append("\",\"object\":\"model\",\"owned_by\":\"stykker-proxy\"}");
        sb.Append("]}");
        return sb.ToString();
    }

    private static string ErrorJson(string message)
    {
        // Das Fehlerobjekt bedient OpenAI- und Anthropic-Clients (beide lesen "error" mit "type" und "message")
        return "{\"type\":\"error\",\"error\":{\"type\":\"service_unavailable\",\"message\":\"" + JsonEscape(message) + "\"}}";
    }

    private static string JsonEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
