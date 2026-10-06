using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StykkerLlm.Core;

// Ein gekoppelter Node (docs/nodes.md): Adresse, Name und das Gerätetoken, das er dem Hub gegeben hat.
// Das Token steht nur in nodes.dat (an den Windows-Benutzer gebunden wie access.dat), nie im Zustand.
public sealed class NodeEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
    public DateTime Added { get; set; }
}

internal sealed class NodeFile
{
    public List<NodeEntry> Nodes { get; set; } = new();
}

// Eine laufende Kopplung (der Hub hat angefragt, der Node zeigt keinen Code – der Hub zeigt ihn, und am Node
// gibt man ihn frei). Fenster, TUI und Web zeigen Code und Restzeit.
public sealed class NodePairing
{
    public string Url { get; init; } = "";
    public string Name { get; init; } = "";
    public string Code { get; init; } = "";
    public DateTime Expires { get; init; }
    public string Status { get; set; } = PairStatus.Pending;   // pending | approved | denied | expired | error
    public string Message { get; set; } = "";
}

// Die Verbindung zu einem Node: der Client mit Live-Strom, dazu wann er zuletzt etwas geschickt hat
public sealed class NodeLink : IDisposable
{
    internal NodeLink(NodeEntry entry, ServerClient client)
    {
        Entry = entry;
        Client = client;
        client.Changed += _ => LastSeen = DateTime.Now;
    }

    public NodeEntry Entry { get; }
    public ServerClient Client { get; }
    public DateTime? LastSeen { get; private set; }
    public StateSnapshot? State => Client.Connected ? Client.State : null;
    public bool Online => Client.Connected && Client.State != null;
    public string? Error => Online ? null : Client.LastError;

    public void Dispose() => Client.Dispose();
}

// Die Nodes dieses Hubs: gespeichert in nodes.dat, je Node ein Client mit Live-Strom (/api/stream mit Gerätetoken).
// Koppeln in beide Richtungen:
//  StartPairAsync(url)          der Hub holt beim Node einen Code und zeigt ihn; am Node gibt man ihn frei (Netflix-Weg)
//  PairWithCodeAsync(url, code) man kennt den Code des Nodes (sechs Ziffern aus seinem Fenster) und tippt ihn am Hub ein
public sealed class NodeRegistry : IDisposable
{
    public const string FileName = "nodes.dat";
    public const int DefaultWebPort = 8078;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly IPlatform _platform;
    private readonly HttpMessageHandler? _handler;
    private readonly HttpClient _http;
    private readonly object _lock = new();
    private readonly List<NodeLink> _links = new();
    private CancellationTokenSource? _pairCts;
    private bool _started;

    public string FilePath { get; }
    public NodePairing? Pairing { get; private set; }
    public event Action? Changed;

    // handler: für Tests ohne Netz (ein Fake, der wie ein Node antwortet)
    public NodeRegistry(AppPaths paths, IPlatform platform, HttpMessageHandler? handler = null)
    {
        FilePath = Path.Combine(paths.Root, FileName);
        _platform = platform;
        _handler = handler;
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(8);
        foreach (var e in Load()) _links.Add(MakeLink(e));
    }

    public IReadOnlyList<NodeLink> Nodes { get { lock (_lock) return _links.ToList(); } }

    public NodeLink? Find(string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;
        lock (_lock)
            return _links.FirstOrDefault(l => l.Entry.Id == idOrName)
                ?? _links.FirstOrDefault(l => l.Entry.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
    }

    // Live-Ströme starten (der Server ruft das einmal; Tests dürfen es lassen und fragen selbst ab)
    public void Start()
    {
        lock (_lock)
        {
            _started = true;
            foreach (var l in _links) l.Client.Start();
        }
    }

    private NodeLink MakeLink(NodeEntry e)
    {
        var client = ServerClient.ForDevice(e.Url, e.Token, _handler);
        var link = new NodeLink(e, client);
        client.Changed += _ => Changed?.Invoke();
        client.Lost += _ => Changed?.Invoke();
        return link;
    }

    // "192.168.178.52", "alexpc:8078", "http://alexpc:8078/" → "http://192.168.178.52:8078"
    public static string NormalizeUrl(string? input)
    {
        var s = (input ?? "").Trim().TrimEnd('/');
        if (s.Length == 0) return "";
        if (!s.Contains("://", StringComparison.Ordinal)) s = "http://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Host.Length == 0) return "";
        int port = u.IsDefaultPort && !s.Contains(":" + u.Port, StringComparison.Ordinal) ? DefaultWebPort : u.Port;
        return $"{u.Scheme}://{u.Host}:{port}";
    }

    // ── Koppeln ──

    // Läuft dort ein Stykker-Server? Liefert seinen Rechnernamen (oder null)
    private async Task<string?> PingAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url + "/api/ping", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (J.Str(doc.RootElement, "app") != "StykkerLLM-Server") return null;
            return J.Str(doc.RootElement, "name") ?? new Uri(url).Host;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException) { return null; }
    }

    // Netflix-Weg: beim Node einen Code holen; der Hub zeigt ihn, am Node gibt man ihn frei. Danach fragt der Hub
    // im Hintergrund nach, bis das Token kommt (oder der Code abläuft / abgelehnt wird).
    public async Task<ActionResult> StartPairAsync(string? address, CancellationToken ct = default)
    {
        var url = NormalizeUrl(address);
        if (url.Length == 0) return ActionResult.Fail(Strings.NodeAddressLabel);
        var name = await PingAsync(url, ct).ConfigureAwait(false);
        if (name == null) return ActionResult.Fail(Strings.NodeNotReachable);
        try
        {
            using var resp = await _http.PostAsJsonAsync(url + "/pair/request",
                new Dictionary<string, string> { ["name"] = Environment.MachineName, ["kind"] = PairKinds.Hub }, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (!resp.IsSuccessStatusCode || !J.Bool(r, "ok"))
                return ActionResult.Fail(J.Str(r, "message") ?? (resp.StatusCode == System.Net.HttpStatusCode.Forbidden ? Strings.NodeNotReachable : Strings.NodePairFailed));
            var id = J.Str(r, "id") ?? "";
            var secret = J.Str(r, "secret") ?? "";
            var code = J.Str(r, "code") ?? "";
            var seconds = J.Int(r, "seconds");
            var pairing = new NodePairing
            {
                Url = url, Name = name, Code = code, Expires = DateTime.Now.AddSeconds(seconds > 0 ? seconds : AccessControl.CodeLifetime.TotalSeconds),
            };
            CancelPairing();
            var cts = new CancellationTokenSource();
            lock (_lock) { Pairing = pairing; _pairCts = cts; }
            Changed?.Invoke();
            _ = Task.Run(() => PollPairingAsync(pairing, id, secret, cts.Token));
            return new ActionResult(true, Strings.NodePairShowCode(AccessControl.Pretty(code)), code);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return ActionResult.Fail(Strings.NodeNotReachable);
        }
    }

    private async Task PollPairingAsync(NodePairing pairing, string id, string secret, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                using var resp = await _http.PostAsJsonAsync(pairing.Url + "/pair/poll",
                    new Dictionary<string, string> { ["id"] = id, ["secret"] = secret }, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                var status = J.Str(doc.RootElement, "status") ?? PairStatus.Unknown;
                if (status == PairStatus.Approved && J.Str(doc.RootElement, "token") is { Length: > 0 } token)
                {
                    pairing.Status = PairStatus.Approved;
                    pairing.Message = Strings.NodePairDone;
                    Add(pairing.Url, pairing.Name, token);   // meldet Changed
                    return;
                }
                if (status is PairStatus.Denied or PairStatus.Expired or PairStatus.Unknown)
                {
                    pairing.Status = status == PairStatus.Denied ? PairStatus.Denied : PairStatus.Expired;
                    pairing.Message = status == PairStatus.Denied ? Strings.PairDenied : Strings.PairExpired;
                    Changed?.Invoke();
                    return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException) { /* Node kurz weg: weiter fragen */ }
            if (DateTime.Now > pairing.Expires.AddSeconds(30))
            {
                pairing.Status = PairStatus.Expired;
                pairing.Message = Strings.PairExpired;
                Changed?.Invoke();
                return;
            }
        }
    }

    public void CancelPairing()
    {
        CancellationTokenSource? old;
        lock (_lock) { old = _pairCts; _pairCts = null; Pairing = null; }
        try { old?.Cancel(); } catch (ObjectDisposedException) { }
        old?.Dispose();
    }

    // Andersherum: den Code des Nodes kennt man (sechs Ziffern aus seinem Fenster) – sofort koppeln
    public async Task<ActionResult> PairWithCodeAsync(string? address, string? code, CancellationToken ct = default)
    {
        var url = NormalizeUrl(address);
        if (url.Length == 0) return ActionResult.Fail(Strings.NodeAddressLabel);
        var name = await PingAsync(url, ct).ConfigureAwait(false);
        if (name == null) return ActionResult.Fail(Strings.NodeNotReachable);
        try
        {
            using var resp = await _http.PostAsJsonAsync(url + "/pair/token",
                new Dictionary<string, string> { ["code"] = AccessControl.CleanCode(code), ["name"] = Environment.MachineName, ["kind"] = PairKinds.Hub }, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var token = J.Str(doc.RootElement, "token");
            if (!resp.IsSuccessStatusCode || string.IsNullOrEmpty(token))
                return ActionResult.Fail(J.Str(doc.RootElement, "message") ?? Strings.NodePairFailed);
            var e = Add(url, J.Str(doc.RootElement, "name") ?? name, token);
            return new ActionResult(true, Strings.NodePairDone, e.Id);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return ActionResult.Fail(Strings.NodeNotReachable);
        }
    }

    // Eintragen (dieselbe Adresse ersetzt den alten Eintrag – neu gekoppelt heißt neues Token)
    public NodeEntry Add(string url, string name, string token)
    {
        var entry = new NodeEntry { Id = Guid.NewGuid().ToString("N")[..8], Name = string.IsNullOrWhiteSpace(name) ? new Uri(url).Host : name.Trim(), Url = url, Token = token, Added = DateTime.Now };
        NodeLink link;
        List<NodeLink> gone;
        lock (_lock)
        {
            gone = _links.Where(l => l.Entry.Url.Equals(url, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var g in gone) { _links.Remove(g); entry.Id = g.Entry.Id; }
            link = MakeLink(entry);
            _links.Add(link);
            if (_started) link.Client.Start();
        }
        foreach (var g in gone) g.Dispose();
        Save();
        Changed?.Invoke();
        return entry;
    }

    // Entkoppeln: dem Node Bescheid geben (er streicht das Token), dann hier vergessen. Ist der Node gerade weg,
    // bleibt dort ein toter Eintrag in der Geräteliste – den kann man am Node entfernen.
    public async Task<bool> RemoveAsync(string? idOrName, CancellationToken ct = default)
    {
        var link = Find(idOrName);
        if (link == null) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, link.Entry.Url + "/pair/forget");
            req.Headers.TryAddWithoutValidation(StateJson.DeviceHeader, link.Entry.Token);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var _ = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* Node weg: nur hier vergessen */ }
        lock (_lock) _links.Remove(link);
        link.Dispose();
        Save();
        Changed?.Invoke();
        return true;
    }

    public bool Rename(string? idOrName, string? name)
    {
        var link = Find(idOrName);
        if (link == null || string.IsNullOrWhiteSpace(name)) return false;
        link.Entry.Name = name.Trim().Length > 40 ? name.Trim()[..40] : name.Trim();
        Save();
        Changed?.Invoke();
        return true;
    }

    // Aktion auf einem Node ausführen (Start, Stop, VRAM frei, Test einreihen …)
    public async Task<ActionResult> SendAsync(string? idOrName, ActionRequest req, CancellationToken ct = default)
    {
        var link = Find(idOrName);
        if (link == null) return ActionResult.Fail(Strings.NodeUnknown);
        return await link.Client.SendAsync(req, ct).ConfigureAwait(false);
    }

    // ── Ergebnisse der Tests einsammeln (N3) ──

    // Ordner für die Läufe eines Nodes unter den eigenen Ergebnissen: eval-results/nodes/<Name>/
    public static string ResultsFolder(string resultsDir, string nodeName)
    {
        var safe = new string(nodeName.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ' ' ? '_' : c).ToArray());
        return Path.Combine(resultsDir, "nodes", safe.Length == 0 ? "node" : safe);
    }

    // Läufe, die hier noch fehlen, von jedem erreichbaren Node holen. Liefert die Zahl neuer Dateien.
    // Nur Dateinamen aus der Liste des Nodes, und nur einfache Namen (kein Pfad) – der Node kann hier nichts
    // außerhalb seines Ordners schreiben.
    public async Task<int> SyncResultsAsync(string resultsDir, CancellationToken ct = default)
    {
        int added = 0;
        foreach (var link in Nodes.Where(l => l.Online))
        {
            var folder = ResultsFolder(resultsDir, link.Entry.Name);
            try
            {
                using var list = new HttpRequestMessage(HttpMethod.Get, link.Entry.Url + "/api/eval/runs");
                list.Headers.TryAddWithoutValidation(StateJson.DeviceHeader, link.Entry.Token);
                using var resp = await _http.SendAsync(list, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    var name = e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
                    if (!IsRunFileName(name) || File.Exists(Path.Combine(folder, name))) continue;
                    using var get = new HttpRequestMessage(HttpMethod.Get, link.Entry.Url + "/api/eval/runs/" + Uri.EscapeDataString(name));
                    get.Headers.TryAddWithoutValidation(StateJson.DeviceHeader, link.Entry.Token);
                    using var file = await _http.SendAsync(get, ct).ConfigureAwait(false);
                    if (!file.IsSuccessStatusCode) continue;
                    var text = await file.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    Directory.CreateDirectory(folder);
                    AtomicFile.WriteAllText(Path.Combine(folder, name), text);
                    added++;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException) { /* nächstes Mal */ }
        }
        return added;
    }

    public static bool IsRunFileName(string name) =>
        name.Length is > 5 and < 200 && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains("..", StringComparison.Ordinal);

    // ── Datei ──

    private List<NodeEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var raw = File.ReadAllText(FilePath).Trim();
            string json;
            if (raw.StartsWith("{", StringComparison.Ordinal)) json = raw;
            else
            {
                var blob = Convert.FromBase64String(raw);
                json = Encoding.UTF8.GetString(_platform.UnprotectForCurrentUser(blob) ?? blob);
            }
            var file = JsonSerializer.Deserialize<NodeFile>(json, Json);
            return file?.Nodes?.Where(n => n.Url.Length > 0 && n.Token.Length > 0).ToList() ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or UnauthorizedAccessException) { return new(); }
    }

    private void Save()
    {
        string json;
        lock (_lock) json = JsonSerializer.Serialize(new NodeFile { Nodes = _links.Select(l => l.Entry).ToList() }, Json);
        try
        {
            var protectedBytes = _platform.ProtectForCurrentUser(Encoding.UTF8.GetBytes(json));
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(FilePath, protectedBytes == null ? json : Convert.ToBase64String(protectedBytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* dann gilt die Liste nur für diese Sitzung */ }
    }

    public void Dispose()
    {
        CancelPairing();
        lock (_lock)
        {
            foreach (var l in _links) l.Dispose();
            _links.Clear();
        }
        _http.Dispose();
    }
}
