using System.Net.Http.Headers;
using System.Text;

namespace StykkerLlm.Core;

// Der Client des Servers (S3/S4/S5): Fenster, TUI und Skripte holen den Zustand und schicken Aktionen an den Server,
// statt selbst zu messen. Schlüssel und Cookie: Fenster und TUI lesen den Schlüssel aus dem Datenordner (an den
// Benutzer gebunden, ein Browser im selben Browser kann ihn nicht), ein Browser meldet sich mit dem Zugangscode an.
public sealed class ServerClient : IDisposable
{
    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _disposed;

    public string BaseUrl { get; }
    public string Key { get; }
    public StateSnapshot? State { get; private set; }
    public bool Connected { get; private set; }
    public string? LastError { get; private set; }
    // Der Zustand hat sich geändert (Aufruf kommt aus einem Hintergrundthread – die Oberfläche muss selbst umschalten)
    public event Action<StateSnapshot>? Changed;
    // Verbindung weg oder Fehlertext (nur einmal je Zustand, damit kein Flackern entsteht)
    public event Action<string>? Lost;

    public ServerClient(string baseUrl, string key) : this(baseUrl, key, null, null) { }

    // Ein Hub fragt seine Nodes mit dem Gerätetoken ab (Kopf X-Stykker-Device) statt mit dem Schlüssel des Datenordners.
    // handler: für Tests (ohne Netz)
    private ServerClient(string baseUrl, string key, string? deviceToken, HttpMessageHandler? handler)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        Key = key;
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.BaseAddress = new Uri(BaseUrl + "/");
        if (deviceToken != null) _http.DefaultRequestHeaders.TryAddWithoutValidation(StateJson.DeviceHeader, deviceToken);
        else _http.DefaultRequestHeaders.TryAddWithoutValidation(StateJson.KeyHeader, key);
    }

    public static ServerClient ForDevice(string baseUrl, string deviceToken, HttpMessageHandler? handler = null) =>
        new(baseUrl, "", deviceToken, handler);

    // Der Schlüssel des Servers: access-key im Datenordner, an den Windows-Benutzer gebunden. Fehlt die Datei,
    // lief der Server noch nie (oder unter einem anderen Benutzer) – dann gibt es keine Verbindung.
    public const string KeyFileName = "server.key";

    public static string? ReadKey(AppPaths paths, IPlatform platform)
    {
        var file = Path.Combine(paths.Root, KeyFileName);
        if (!File.Exists(file)) return null;
        try
        {
            // Mit Benutzerbindung (Windows) ist die Datei ein geschützter Blob; ohne Bindung – alles außerhalb von
            // Windows – der Schlüssel selbst, base64-kodiert. Deshalb der Klartext-Rückfall: sonst könnte ein Server
            // unter Linux seinen eigenen Schlüssel nie wieder lesen und Fenster/TUI kämen nie herein. Ein geschützter
            // Blob darf dabei nicht als Schlüssel durchgehen (er ist Binärdaten, kein Text) – sonst bekäme ein
            // Datenordner, der zwischen den Systemen wandert, einen erfundenen Schlüssel statt „nicht lesbar“.
            var bytes = Convert.FromBase64String(File.ReadAllText(file).Trim());
            var key = platform.UnprotectForCurrentUser(bytes) ?? (LooksLikePlainKey(bytes) ? bytes : null);
            if (key == null) return null;
            var text = Encoding.UTF8.GetString(key);
            return text.Length > 0 ? text : null;
        }
        catch { return null; }
    }

    // Der Schlüssel ist base64 von 32 Zufallsbytes, also gut vierzig druckbare Zeichen – nichts anderes.
    private static bool LooksLikePlainKey(byte[] bytes)
    {
        if (bytes.Length is < 16 or > 512) return false;
        foreach (var b in bytes)
            if (b < 0x21 || b > 0x7E) return false;
        return true;
    }

    public static string WriteKey(AppPaths paths, IPlatform platform)
    {
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var blob = platform.ProtectForCurrentUser(Encoding.UTF8.GetBytes(key)) ?? Encoding.UTF8.GetBytes(key);
        Directory.CreateDirectory(paths.Root);
        AtomicFile.WriteAllText(Path.Combine(paths.Root, KeyFileName), Convert.ToBase64String(blob));
        return key;
    }

    // Läuft der Server auf diesem Port? (ein Aufruf mit kurzer Zeitgrenze, sonst ein False aus dem Fehlerbild)
    public static async Task<bool> IsRunningAsync(int port, CancellationToken ct = default)
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
            using var resp = await probe.GetAsync($"http://127.0.0.1:{port}/api/ping", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // Adresse des Servers: der Standardport 8078 auf diesem Rechner
    public static string DefaultUrl(int port = 8078) => $"http://127.0.0.1:{port}";

    // Läuft der Server (mit Schlüssel aus dem Datenordner)? Lädt dabei gleich den ersten Zustand.
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        var state = await GetStateAsync(ct).ConfigureAwait(false);
        if (state == null)
        {
            MarkLost(Strings.ServerNotRunning);
            return false;
        }
        return true;
    }

    public async Task<StateSnapshot?> GetStateAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("/api/state", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var state = StateSnapshot.Parse(await resp.Content.ReadAsStringAsync(ct));
            Publish(state);
            return state;
        }
        catch
        {
            // Der Server antwortet nicht mehr: als verloren melden, damit die Oberfläche wieder selbst misst
            // (sonst stünde der letzte Zustand still und alle Aktionen liefen ins Leere)
            MarkLost(Strings.ServerNotRunning);
            return null;
        }
    }

    public async Task<ActionResult> SendAsync(ActionRequest req, CancellationToken ct = default)
    {
        try
        {
            using var content = new StringContent(Json(req), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("/api/action", content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return ActionResult.Fail(TrimMessage(body, resp.StatusCode));
            return ParseResult(body);
        }
        catch (Exception ex)
        {
            return ActionResult.Fail(ex is HttpRequestException ? Strings.ServerNotRunning : ex.Message);
        }
    }

    public Task<ActionResult> SendAsync(string action, string? arg = null, string? arg2 = null, bool flag = false, int number = 0,
        IEnumerable<string>? ids = null, string? secret = null, CancellationToken ct = default) =>
        SendAsync(new ActionRequest
        {
            Action = action,
            Arg = arg,
            Arg2 = arg2,
            Flag = flag,
            Number = number,
            Ids = ids?.ToList() ?? new(),
            Secret = secret,
        }, ct);

    // Zustand als Stream abonnieren (Server-Sent Events); wenn der.Stream nicht klappt, alle zwei Sekunden nachfragen
    public void Start(int fallbackMs = 2000)
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await StreamOnceAsync(ct))
                {
                    if (ct.IsCancellationRequested) return;
                    await PollOnceAsync(fallbackMs, ct);
                }
            }
        });
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        Connected = false;
    }

    private async Task<bool> StreamOnceAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/stream");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentType?.MediaType != "text/event-stream") return false;
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sb = new StringBuilder();
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) return true;   // Server hat beendet: neu verbinden
                if (line.Length == 0)
                {
                    if (sb.Length > 0)
                    {
                        try { Publish(StateSnapshot.Parse(sb.ToString())); } catch { /* halber Takt: nächster kommt */ }
                        sb.Clear();
                    }
                    continue;
                }
                if (line.StartsWith("data:", StringComparison.Ordinal)) sb.Append(line[5..].TrimStart());
            }
            return true;
        }
        catch (OperationCanceledException) { return true; }
        catch { return false; }
    }

    private async Task PollOnceAsync(int everyMs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var state = await GetStateAsync(ct);
            if (state == null) MarkLost(Strings.ServerNotRunning);
            try { await Task.Delay(everyMs, ct); } catch { return; }
        }
    }

    private void Publish(StateSnapshot state)
    {
        bool wasConnected = Connected;
        State = state;
        if (!Connected) Connected = true;
        Changed?.Invoke(state);
        if (wasConnected) LastError = null;
    }

    private void MarkLost(string message)
    {
        bool was = Connected;
        Connected = false;
        State = null;
        if (was || LastError != message) { LastError = message; Lost?.Invoke(message); }
    }

    private static string Json(ActionRequest req)
    {
        var sb = new StringBuilder("{\"action\":");
        sb.Append(System.Text.Json.JsonSerializer.Serialize(req.Action));
        if (req.Arg != null) sb.Append(",\"arg\":").Append(System.Text.Json.JsonSerializer.Serialize(req.Arg));
        if (req.Arg2 != null) sb.Append(",\"arg2\":").Append(System.Text.Json.JsonSerializer.Serialize(req.Arg2));
        if (req.Name != null) sb.Append(",\"name\":").Append(System.Text.Json.JsonSerializer.Serialize(req.Name));
        if (req.Flag) sb.Append(",\"flag\":true");
        if (req.Number != 0) sb.Append(",\"number\":").Append(req.Number.ToString(Strings.Inv));
        if (req.Secret != null) sb.Append(",\"secret\":").Append(System.Text.Json.JsonSerializer.Serialize(req.Secret));
        if (req.Ids.Count > 0)
        {
            sb.Append(",\"ids\":[");
            sb.Append(string.Join(",", req.Ids.Select(id => System.Text.Json.JsonSerializer.Serialize(id))));
            sb.Append(']');
        }
        if (req.Values.Count > 0)
        {
            sb.Append(",\"values\":{");
            sb.Append(string.Join(",", req.Values.Select(kv => System.Text.Json.JsonSerializer.Serialize(kv.Key) + ":" + System.Text.Json.JsonSerializer.Serialize(kv.Value))));
            sb.Append('}');
        }
        return sb.Append('}').ToString();
    }

    private static ActionResult ParseResult(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var r = doc.RootElement;
            bool ok = r.TryGetProperty("ok", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;
            string msg = r.TryGetProperty("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String ? m.GetString()! : "";
            string? data = r.TryGetProperty("data", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String ? d.GetString() : null;
            return new ActionResult(ok, msg, data);
        }
        catch { return ActionResult.Fail(TrimMessage(body, 0)); }
    }

    private static string TrimMessage(string body, System.Net.HttpStatusCode code)
    {
        var text = body.Length > 300 ? body[..300] : body;
        return string.IsNullOrWhiteSpace(text) ? $"HTTP {(int)code}" : text;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _http.Dispose();
    }
}

// Die Server-Datei liegt neben dem Programm (Release) oder im Ausgabeordner (Entwicklung)
public static class ServerLocator
{
    public static string? Find(string? exePath = null)
    {
        string[] names = { "StykkerLLM-Server.exe", "StykkerLLM-Server", "StykkerLlm.Server" };
        foreach (var folder in Folders(exePath))
            foreach (var name in names)
            {
                var full = Path.Combine(folder, name);
                if (File.Exists(full)) return full;
            }
        return null;
    }

    private static List<string> Folders(string? exePath)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            string full;
            try { full = Path.GetFullPath(folder); } catch { return; }
            if (seen.Add(full)) list.Add(full);
        }

        Add(Path.GetDirectoryName(exePath ?? ""));
        Add(AppContext.BaseDirectory);
        Add(Directory.GetCurrentDirectory());
        // Entwicklungsaufbau: vom eigenen Ausgabeordner hoch bis zur Solution und dort in die Server-Ausgabe
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "StykkerLlm.slnx")) || Directory.Exists(Path.Combine(dir.FullName, "src", "StykkerLlm.Server")))
                {
                    foreach (var cfg in new[] { "Release", "Debug" })
                    {
                        Add(Path.Combine(dir.FullName, "src", "StykkerLlm.Server", "bin", cfg, "net10.0"));
                        Add(Path.Combine(dir.FullName, "build", "out", "publish", "app"));
                    }
                    return list;
                }
                dir = dir.Parent;
            }
        }
        return list;
    }
}
