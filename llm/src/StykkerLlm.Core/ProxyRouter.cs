using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StykkerLlm.Core;

// Ein Backend, an das der Router weiterleiten kann. Model ist der Name, den das Backend selbst führt (alias bzw. Modellname);
// PublicModel ist der Name, unter dem der Client es sieht (bei Namensgleichheit bzw. Cloud „<Anbieter>/<Modell>"). ServerKey ist der
// besitzende lokale Server (für die Aufnahme), Remote markiert Modelle eines angehängten Rechners, Cloud die eines Anbieters im Netz,
// DisplayName dient der Anzeige. HostId: das Ziel läuft auf einem Model-Host (Url ist dort lokal, Weg über den Tunnel).
public sealed record ProxyTarget(string Key, string Url, string? Model, BackendKind Kind, bool Ready, bool Loading,
    string ServerKey = "", bool Remote = false, string? PublicModel = null, string DisplayName = "", bool Cloud = false, string HostId = "")
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
    private volatile string? _classifierKey;
    private volatile string? _visionKey;

    // So lange auf ein noch ladendes Ziel warten, statt sofort 503 zu antworten
    public TimeSpan LoadingWait { get; set; } = TimeSpan.FromSeconds(120);

    // Der API-Schlüssel eines Cloud-Ziels. Der Proxy setzt ihn selbst als Authorization-Kopfzeile; der Schlüssel des Clients
    // wird dabei nicht weitergereicht (er gehört zu stykker, nicht zum Anbieter). Der Wert verlässt diese Schicht nicht.
    public Func<ProxyTarget, string?>? AuthFor { get; set; }

    // Model-Hosts (docs/plan-hosts-gateway.md, P6): der Weg zu einem Host (Tunnel) und der Start eines Modells, das noch
    // nirgends läuft. HostStart liefert NoHostHasIt, wenn kein Host die Datei hat – dann gilt wie bisher das Standardziel.
    public Func<string, HttpClient?>? HostClient { get; set; }
    public Func<string, CancellationToken, Task<HostChoice>>? HostStart { get; set; }
    public Func<IReadOnlyList<string>>? StartableModels { get; set; }
    // So lange auf ein auf einem Host gestartetes Modell warten (Laden großer Modelle dauert)
    public TimeSpan HostStartWait { get; set; } = TimeSpan.FromSeconds(240);

    public RouterProxy(int listenPort) : base(VirtualModel, "", listenPort) { }

    // Wird im Takt der Oberfläche neu gesetzt (Server können kommen und gehen)
    // classifierKey: Ziel für die Sicherheitsprüfung von Claude Code (Modus „auto“), null = wie das Standardziel
    // visionKey: Ziel für Anfragen mit Bildern, null = wie das Standardziel
    public void SetTargets(IReadOnlyList<ProxyTarget> targets, string? defaultKey, string? classifierKey = null, string? visionKey = null)
    {
        _targets = targets.ToArray();
        _defaultKey = defaultKey;
        _classifierKey = classifierKey;
        _visionKey = visionKey;
    }

    // Das Ziel für eine Anfrage mit Bild, oder null (kein Bild, nicht gesetzt oder Ziel nicht da)
    internal ProxyTarget? VisionTarget(IReadOnlyList<ProxyTarget> targets, byte[]? body)
    {
        var key = _visionKey;
        if (string.IsNullOrEmpty(key) || !HasImage(body)) return null;
        return targets.FirstOrDefault(x => x.Key == key);
    }

    // Enthält eine Nachricht ein Bild? Anthropic: Block „type: image“; OpenAI: „type: image_url“. Nur die Typen werden gelesen.
    internal static bool HasImage(byte[]? body)
    {
        if (body == null || body.Length == 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array) return false;
            foreach (var m in msgs.EnumerateArray())
                if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("content", out var c) && ContainsImage(c)) return true;
            return false;
        }
        catch (JsonException) { return false; }
    }

    // Bilder stecken auch in Werkzeugergebnissen (tool_result → content); darum rekursiv über Blocklisten
    private static bool ContainsImage(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array) return false;
        foreach (var b in content.EnumerateArray())
        {
            if (b.ValueKind != JsonValueKind.Object) continue;
            if (b.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is "image" or "image_url") return true;
            if (b.TryGetProperty("content", out var inner) && ContainsImage(inner)) return true;
        }
        return false;
    }

    // Ein Claude-Modellname, der nicht claude-stykker ist (claude-sonnet-…, claude-haiku-…): so fragt Claude Code im Modus
    // „auto“ seine Sicherheitsprüfung an. Die Sitzung selbst läuft über claude-stykker.
    internal static bool IsOtherClaudeModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) && model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
        && !model.Equals(ClaudeDesktop.ModelAlias, StringComparison.OrdinalIgnoreCase);

    // Das Ziel der Sicherheitsprüfung für diese Anfrage, oder null (keine Prüfung, nicht gesetzt oder Ziel nicht da).
    // Eine Prüfung ist eine Anfrage an einen anderen Claude-Namen, oder – so fragt Claude Code hinter einem Proxy an –
    // eine Anfrage unter dem Namen des Gesprächs, die wie die Prüfung aufgebaut ist (IsSafetyCheck).
    internal ProxyTarget? ClassifierTarget(IReadOnlyList<ProxyTarget> targets, string? wanted, byte[]? body = null)
    {
        var key = _classifierKey;
        if (string.IsNullOrEmpty(key) || !(IsOtherClaudeModel(wanted) || IsSafetyCheck(body))) return null;
        return targets.FirstOrDefault(x => x.Key == key);
    }

    // Die Sicherheitsprüfung von Claude Code (Modus „auto“): keine Werkzeuge und das Stoppzeichen „</block>“, mit dem sie ihr
    // Urteil abschließt. Gelesen werden nur diese beiden Felder, nie der Text.
    internal static bool IsSafetyCheck(byte[]? body)
    {
        if (body == null || body.Length == 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0) return false;
            if (!root.TryGetProperty("stop_sequences", out var stops) || stops.ValueKind != JsonValueKind.Array) return false;
            foreach (var s in stops.EnumerateArray())
                if (s.ValueKind == JsonValueKind.String && s.GetString() == "</block>") return true;
            return false;
        }
        catch (JsonException) { return false; }
    }

    protected override async Task<ProxyRoute> RouteAsync(string method, string target, IReadOnlyList<(string Name, string Value)> headers, byte[]? body, CancellationToken ct)
    {
        var targets = _targets;
        int q = target.IndexOf('?');
        string path = q >= 0 ? target[..q] : target;

        // Modellliste zusammenfassen (der Client sieht so alle Backends auf einmal)
        if (method == "GET" && (path.Equals("/v1/models", StringComparison.OrdinalIgnoreCase) || path.Equals("/models", StringComparison.OrdinalIgnoreCase)))
        {
            // Claude Desktop (Anthropic-Client) bekommt nur, was sofort antwortet: laufende lokale Server und die gewählten
            // Cloud-Modelle – keine Modelldateien, die erst starten müssten. Andere Clients sehen die Liste wie bisher.
            bool anthropic = headers.Any(h => h.Name.Equals("anthropic-version", StringComparison.OrdinalIgnoreCase));
            return anthropic
                ? Local(200, ModelsJson(targets.Where(t => t.Cloud || t.Ready).ToList()))
                : Local(200, ModelsJson(targets, StartableModels?.Invoke()));
        }

        // Die Claude-App schickt /v1/messages und erwartet Anthropic-Ereignisse zurück: übersetzen statt durchreichen
        if (method == "POST" && AnthropicBridge.IsMessagesPath(path))
            return await RouteAnthropicAsync(targets, headers, body, ct).ConfigureAwait(false);

        var (t, error, errorStatus) = await ResolveAsync(targets, body, ct).ConfigureAwait(false);
        if (t == null) return Local(errorStatus, ErrorJson(error!));

        // model auf den Namen setzen, den das Ziel erwartet (bei „stykker" → Modell des Ziels)
        var rewritten = t.Model != null ? RewriteModel(body, t.Model) : body;
        var own = t.Cloud && AuthFor?.Invoke(t) is { Length: > 0 } key
            ? new[] { ("Authorization", "Bearer " + key) } : null;
        if (t.HostId.Length == 0) return new ProxyRoute(t.Url, t.RouteKey, rewritten, Headers: own);
        var via = HostClient?.Invoke(t.HostId);
        return via == null ? Local(503, ErrorJson(Strings.HostNotConnected)) : new ProxyRoute(t.Url, t.RouteKey, rewritten, Headers: own, Client: via);
    }

    // Das Ziel einer Anfrage bestimmen: nach model, sonst das Standardziel, dazu die Zustandsprüfungen.
    // Bei einem Fehler kommt statt des Ziels die Meldung (und der Status) für die Antwort des Proxys zurück.
    private async Task<(ProxyTarget? Target, string? Error, int Status)> ResolveAsync(ProxyTarget[] targets, byte[]? body, CancellationToken ct)
    {
        string? wanted = ReadModel(body);
        ProxyTarget? t = null;
        if (!string.IsNullOrWhiteSpace(wanted) && !wanted.Equals(VirtualModel, StringComparison.OrdinalIgnoreCase))
            t = targets.FirstOrDefault(x => Matches(x, wanted));
        t ??= ClassifierTarget(targets, wanted, body);
        t ??= VisionTarget(targets, body);
        if (t == null && HostStart != null && !string.IsNullOrWhiteSpace(wanted) && !wanted.Equals(VirtualModel, StringComparison.OrdinalIgnoreCase))
        {
            // Läuft nirgends: auf einem Host mit der Datei und genug Grafikspeicher starten und warten, bis es bereit ist
            var choice = await HostStart(wanted, ct).ConfigureAwait(false);
            if (choice.Error != null) return (null, choice.Error, 503);
            if (choice.Ok)
            {
                t = await WaitForModelAsync(wanted, ct).ConfigureAwait(false);
                if (t == null) return (null, Strings.HostStartTimeout(choice.File.Name, choice.HostName), 503);
            }
        }
        t ??= DefaultTarget(targets);   // stykker, leer, unbekannt oder nicht gefunden → Standardziel

        if (t == null) return (null, Strings.ProxyNoTarget, 503);
        if (t.Cloud && AuthFor?.Invoke(t) is not { Length: > 0 }) return (null, Strings.ProxyProviderNoKey, 503);
        if (t.Loading) t = await WaitForReadyAsync(t.Key, t, ct).ConfigureAwait(false);
        if (t == null || !t.Ready) return (null, t != null && t.Cloud ? Strings.ProxyProviderOfflineProxy : Strings.ProxyTargetOffline, 503);
        if (t.Loading) return (null, Strings.ProxyTargetLoading, 503);   // immer noch am Laden (Zeit abgelaufen)
        return (t, null, 200);
    }

    // ── Anthropic-Brücke (die Claude-App) ──

    // Eine Anfrage in Anthropic-Form: Ziel bestimmen, nach OpenAI übersetzen, weiterleiten und die Antwort wieder als
    // Anthropic-Ereignisse ausgeben. Die Antwort schreibt der Proxy selbst (auch im Strom), weil er umformen muss;
    // die Aufzeichnung läuft über den Mitschreiber wie bei jeder anderen Anfrage.
    private async Task<ProxyRoute> RouteAnthropicAsync(ProxyTarget[] targets, IReadOnlyList<(string Name, string Value)> headers, byte[]? body, CancellationToken ct)
    {
        string requested = ReadModel(body) ?? VirtualModel;
        var (t, error, status) = await ResolveAsync(targets, body, ct).ConfigureAwait(false);
        if (t == null) return AnthropicError(status, error!);

        var openAi = AnthropicBridge.ToOpenAi(body ?? Array.Empty<byte>(), t.Model ?? "", noThinking: IsSafetyCheck(body));
        var own = t.Cloud && AuthFor?.Invoke(t) is { Length: > 0 } key ? new[] { ("Authorization", "Bearer " + key) } : null;
        HttpClient? via = null;
        if (t.HostId.Length > 0)
        {
            via = HostClient?.Invoke(t.HostId);
            if (via == null) return AnthropicError(503, Strings.HostNotConnected);
        }

        // Das Ziel bekommt die Kopfzeilen des Clients (ohne Verbindungs-Kopfzeilen); den Authorization-Kopf eines
        // Cloud-Ziels setzt der Proxy selbst, der des Clients gehört zu stykker.
        var send = new List<(string Name, string Value)>();
        foreach (var (name, value) in headers)
        {
            if (RequestProxy.HopHeaders.Contains(name)) continue;
            if (own != null && name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            send.Add((name, value));
        }

        string url = NetAddr.JoinUpstream(t.Url, "/v1/chat/completions");
        return new ProxyRoute("", t.RouteKey, null,
            LocalStream: (net, tap, token) => BridgeAsync(net, url, send, openAi, requested, own, via, tap, token));
    }

    private static ProxyRoute AnthropicError(int status, string message)
        => new("", VirtualModel, null, "application/json; charset=utf-8", AnthropicBridge.ErrorJson(status, message), status);

    // Weiterleiten und umformen: Anthropic-Bytes an das Ziel, die Antwort als Anthropic-Ereignisse an den Client.
    // Schreibt Kopfzeilen und Rumpf selbst; die Bytes des Ziels bekommt der Mitschreiber, damit die Anfrage gezählt wird.
    private async Task<int> BridgeAsync(Stream net, string url, IReadOnlyList<(string Name, string Value)> headers, byte[] body,
        string model, IReadOnlyList<(string Name, string Value)>? own, HttpClient? client, ProxyTap tap, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        foreach (var (name, value) in headers)
        {
            if (RequestProxy.ContentHeaders.Contains(name)) req.Content.Headers.TryAddWithoutValidation(name, value);
            else req.Headers.TryAddWithoutValidation(name, value);
        }
        if (own != null)
            foreach (var (name, value) in own) req.Headers.TryAddWithoutValidation(name, value);

        HttpResponseMessage resp;
        try
        {
            using var hdr = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hdr.CancelAfter(HeaderTimeout);
            resp = await (client ?? UpstreamClient).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, hdr.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // der Client ist weg
        catch (OperationCanceledException)
        {
            await WriteJsonAsync(net, 504, AnthropicBridge.ErrorJson(504, Strings.ProxyAnthropicTimeout), ct).ConfigureAwait(false);
            return 504;
        }
        catch (Exception)
        {
            await WriteJsonAsync(net, 502, AnthropicBridge.ErrorJson(502, Strings.ProxyAnthropicUnreachable), ct).ConfigureAwait(false);
            return 502;
        }

        using (resp)
        {
            int status = (int)resp.StatusCode;
            if (status != 200)
            {
                // Fehler unverändert weiterreichen: die App erkennt daran z. B. einen zu kleinen Kontext und verdichtet
                string text = await ReadStartAsync(resp, 8192, ct).ConfigureAwait(false);
                int code = status is 400 or 401 or 403 or 404 or 413 or 429 or 529 ? status : 502;
                string message = ErrorMessage(text) ?? resp.ReasonPhrase ?? "error";
                await WriteJsonAsync(net, code, AnthropicBridge.ErrorJson(code, message), ct).ConfigureAwait(false);
                return code;
            }

            bool sse = resp.Content.Headers.ContentType?.MediaType?.Contains("event-stream", StringComparison.OrdinalIgnoreCase) == true;
            if (!sse)
            {
                // Ziel streamt nicht: das ganze JSON einmal übersetzen und als Ereignisfolge ausgeben
                var json = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (json.Length > 0) tap.Feed(json, DateTime.Now);
                string events = AnthropicBridge.MessageAsStream(AnthropicBridge.ToAnthropic(json, model));
                if (events.Length == 0)
                {
                    await WriteJsonAsync(net, 200, json, ct).ConfigureAwait(false);
                    return 200;
                }
                await WriteStreamHeadAsync(net, ct).ConfigureAwait(false);
                await WriteTextAsync(net, events, ct).ConfigureAwait(false);
                return 200;
            }

            await WriteStreamHeadAsync(net, ct).ConfigureAwait(false);
            var stream = new AnthropicBridge.AnthropicStream(model);
            await WriteTextAsync(net, stream.Begin(), ct).ConfigureAwait(false);
            try
            {
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buf = new byte[16 * 1024];
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                while (true)
                {
                    idle.CancelAfter(IdleTimeout);
                    int n;
                    try { n = await src.ReadAsync(buf, idle.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }   // still geworden: der Strom endet hier
                    if (n <= 0) break;
                    tap.Feed(buf.AsSpan(0, n), DateTime.Now);
                    string events = stream.Feed(buf.AsSpan(0, n));
                    if (events.Length > 0) await WriteTextAsync(net, events, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* Ziel bricht ab: der Abschluss unten beendet den Strom sauber */ }
            await WriteTextAsync(net, stream.Finish(), ct).ConfigureAwait(false);
            return 200;
        }
    }

    // Die Kopfzeilen eines Ereignisstroms. Kein Content-Length: der Rumpf endet mit der Verbindung (Connection: close).
    private static Task WriteStreamHeadAsync(Stream net, CancellationToken ct)
        => net.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n"), ct).AsTask();

    private static async Task WriteJsonAsync(Stream net, int status, byte[] body, CancellationToken ct)
    {
        var head = $"HTTP/1.1 {status} {ReasonText(status)}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await net.WriteAsync(Encoding.Latin1.GetBytes(head), ct).ConfigureAwait(false);
        if (body.Length > 0) await net.WriteAsync(body, ct).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(Stream net, string text, CancellationToken ct)
    {
        if (text.Length > 0) await net.WriteAsync(Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
    }

    private static string ReasonText(int status) => status switch
    {
        200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 413 => "Payload Too Large",
        429 => "Too Many Requests", 500 => "Internal Server Error", 502 => "Bad Gateway", 503 => "Service Unavailable",
        504 => "Gateway Timeout", 529 => "Overloaded", _ => "Error",
    };

    // Die ersten Bytes einer Fehlerantwort (begrenzt) – mehr braucht die Meldung nicht
    private static async Task<string> ReadStartAsync(HttpResponseMessage resp, int limit, CancellationToken ct)
    {
        try
        {
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buf = new byte[limit];
            int got = 0, n;
            while (got < limit && (n = await s.ReadAsync(buf.AsMemory(got), ct).ConfigureAwait(false)) > 0) got += n;
            return Encoding.UTF8.GetString(buf, 0, got);
        }
        catch { return ""; }
    }

    // Meldung aus einem Fehlerobjekt des Ziels ({"error":{"message":…}} bzw. {"message":…}), sonst der kurze Text selbst
    private static string? ErrorMessage(string text)
    {
        if (text.Length == 0 || text.Length > 400) return text.Length > 400 ? null : "";
        try
        {
            if (JsonNode.Parse(text) is JsonObject o)
            {
                if (o["error"] is JsonObject e && e["message"] is JsonValue em && em.TryGetValue<string>(out var m)) return m;
                if (o["message"] is JsonValue vm && vm.TryGetValue<string>(out var m2)) return m2;
                if (o["error"] is JsonValue ev && ev.TryGetValue<string>(out var m3)) return m3;
            }
        }
        catch { }
        return text;
    }

    // Auf ein auf einem Host gestartetes Modell warten, bis es als bereites Ziel auftaucht
    private async Task<ProxyTarget?> WaitForModelAsync(string wanted, CancellationToken ct)
    {
        var end = DateTime.UtcNow + HostStartWait;
        while (DateTime.UtcNow < end)
        {
            var cur = _targets.FirstOrDefault(x => Matches(x, wanted));
            if (cur is { Ready: true, Loading: false }) return cur;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return null;
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

    // Die Modellliste in einer Form, die OpenAI- und Anthropic-Clients beide lesen: je Eintrag „object“/„owned_by“ (OpenAI)
    // und „type“/„display_name“/„created_at“ (Anthropic), dazu „has_more“, „first_id“, „last_id“. Claude Desktop füllt
    // damit seine Modellauswahl (modelDiscoveryEnabled); der erste Eintrag ist dort das Standardmodell.
    private static string ModelsJson(IReadOnlyList<ProxyTarget> targets, IReadOnlyList<string>? startable = null)
    {
        var entries = new List<(string Id, string Display, string Owner)>
        {
            // Der Name, den die Claude-App als Standard ansprach: geht an das Standardziel des Proxys
            (ClaudeDesktop.ModelAlias, "Stykker · current target", "stykker"),
            (VirtualModel, "Stykker · current target (stykker)", "stykker"),
        };
        var seen = new HashSet<string>(entries.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
            if (t.EffectiveModel is { Length: > 0 } m && seen.Add(m))
                entries.Add((m, string.IsNullOrWhiteSpace(t.DisplayName) ? m : t.DisplayName, "stykker-proxy"));
        // dazu die Modelldateien der Hosts: sie starten bei der ersten Anfrage
        foreach (var m in startable ?? Array.Empty<string>())
            if (!string.IsNullOrEmpty(m) && seen.Add(m)) entries.Add((m, m, "stykker-proxy"));

        var sb = new StringBuilder();
        sb.Append("{\"object\":\"list\",\"data\":[");
        for (int i = 0; i < entries.Count; i++)
        {
            var (id, display, owner) = entries[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":\"").Append(JsonEscape(id)).Append("\",\"object\":\"model\",\"owned_by\":\"").Append(owner)
              .Append("\",\"type\":\"model\",\"display_name\":\"").Append(JsonEscape(display)).Append("\",\"created_at\":\"1970-01-01T00:00:00Z\"}");
        }
        sb.Append("],\"has_more\":false,\"first_id\":\"").Append(JsonEscape(entries[0].Id))
          .Append("\",\"last_id\":\"").Append(JsonEscape(entries[^1].Id)).Append("\"}");
        return sb.ToString();
    }

    private static string ErrorJson(string message)
    {
        // Das Fehlerobjekt bedient OpenAI- und Anthropic-Clients (beide lesen "error" mit "type" und "message")
        return "{\"type\":\"error\",\"error\":{\"type\":\"service_unavailable\",\"message\":\"" + JsonEscape(message) + "\"}}";
    }

    private static string JsonEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
