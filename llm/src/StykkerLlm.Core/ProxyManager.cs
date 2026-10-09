using System.Text.Json;

namespace StykkerLlm.Core;

// Ein Eintrag der Auswahl „serviertes Modell": lokale Modelle (Key = Server-Key) und Cloud-Modelle (Key = Zielkennung)
public sealed record ProxyChoice(string Key, string Model, string Display, bool Cloud)
{
    // Der Wert, den die Oberfläche in der Auswahl stehen hat (lokal: „<Server-Key>|<Modell>")
    public string Value => Cloud ? Key : $"{Key}|{Model}";
}

// Verwaltet den einen „Stykker-Proxy" auf festem Port: ein-/ausschalten, das servierte Modell wählen, die erreichbaren
// Backends (je geladenes Modell) im Takt aktualisieren, die Modelle angehängter Stykker-Rechner und eingetragener
// Cloud-Anbieter einsammeln und einen fehlgeschlagenen Start sichtbar machen. Ersetzt die früheren Proxys je Server (Serverport + 1000).
public sealed class ProxyManager : IDisposable
{
    public const int DefaultPort = 17500;

    private readonly Func<IReadOnlyList<ServerWatcher>> _servers;
    private readonly AppSettings _settings;
    private readonly Action<ProxyRecord> _onRecord;
    private readonly Func<DateTime> _now;
    private readonly Func<int, RouterProxy> _factory;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ProviderKeys? _keys;
    private RouterProxy? _proxy;
    private (DateTime RetryAt, string Message)? _failed;
    private Dictionary<string, string> _names = new(StringComparer.Ordinal);

    // Zustand je angehängtem Rechner (Modelle aus dessen /v1/models)
    private sealed class RemoteState
    {
        public string[] Models = Array.Empty<string>();
        public bool Ready;
        public DateTime LastTry = DateTime.MinValue;
    }
    private readonly Dictionary<string, RemoteState> _remote = new(StringComparer.Ordinal);
    private volatile bool _remoteBusy;
    private readonly object _remoteLock = new();

    // Zustand je Cloud-Anbieter (Modelle aus dessen /models). Ohne hinterlegten Schlüssel wird gar nicht erst abgefragt:
    // der Nutzer muss den Anbieter mit Schlüssel eintragen (Opt-in, sonst geht keine Anfrage ins Netz).
    private sealed class ProviderState
    {
        public string[] Models = Array.Empty<string>();
        public bool Ready;
        public DateTime LastTry = DateTime.MinValue;
        public string Error = "";
    }
    private readonly Dictionary<string, ProviderState> _provider = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _providerBusy;
    private readonly object _providerLock = new();

    // Frühestens nach dieser Zeit wird ein fehlgeschlagener Start erneut versucht
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(30);
    // So oft werden die Modelle angehängter Rechner abgefragt
    public TimeSpan RemoteInterval { get; set; } = TimeSpan.FromSeconds(10);
    // So oft werden die Modelle der Cloud-Anbieter abgefragt (die Liste dort ändert sich selten)
    public TimeSpan ProviderInterval { get; set; } = TimeSpan.FromSeconds(60);
    // Anzahl tatsächlicher Startversuche (Tests und Diagnose)
    public int StartAttempts { get; private set; }

    public ProxyManager(Func<IReadOnlyList<ServerWatcher>> servers, AppSettings settings, Action<ProxyRecord> onRecord,
        Func<DateTime>? now = null, Func<int, RouterProxy>? factory = null, HttpClient? http = null, ProviderKeys? keys = null)
    {
        _servers = servers; _settings = settings; _onRecord = onRecord;
        _now = now ?? (() => DateTime.Now);
        _factory = factory ?? (port => new RouterProxy(port));
        _ownsHttp = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _keys = keys;
    }

    public bool Enabled => _settings.ProxyEnabled;
    public bool Running => _proxy?.Running == true;
    public bool AnyRunning => Running;
    // Der laufende Port (sonst der eingestellte)
    public int Port => _proxy?.ListenPort ?? _settings.ProxyPort;
    // „" = Auto
    public string TargetKey => _settings.ProxyTarget;
    // Modell des Ziels; „" = erstes/einziges Modell
    public string TargetModel => _settings.ProxyTargetModel;
    public bool BindLan => _settings.ProxyBindLan;
    public IReadOnlyList<RemoteStykker> Remotes => _settings.RemoteStykkers;

    // Model-Hosts (docs/plan-hosts-gateway.md, P6): ihre laufenden Server sind Ziele, Anfragen gehen durch den Tunnel
    public Func<IEnumerable<HostServers>>? HostSources { get; set; }
    public Func<string, HttpClient?>? HostClient { get; set; }
    public Func<string, CancellationToken, Task<HostChoice>>? HostStart { get; set; }
    public Func<IReadOnlyList<string>>? StartableModels { get; set; }

    private List<RemoteStykker> AllRemotes() => _settings.RemoteStykkers.ToList();
    // Die eingetragenen Cloud-Anbieter (ohne Schlüssel – der steht in ProviderKeys und verlässt diese Schicht nicht)
    public IReadOnlyList<ProxyProvider> Providers => _settings.ProxyProviders;
    public RouterProxy? Proxy => _proxy;

    // Verständlicher Hinweis aus dem Proxy (z. B. „Ziel-Kontext zu klein"). Die Oberfläche zeigt ihn wie einen Hinweis (Notice).
    public event Action<string>? Hint;

    // Für Aufnahmen: läuft der Proxy, kann er diesen Server erreichen (bekommt dann Proxy-Details)
    public bool IsRunningFor(string key) => Running && _servers().Any(s => s.Key == key);
    public RouterProxy? Get(string? key = null) => Running ? _proxy : null;
    public IReadOnlyCollection<RequestProxy> All => _proxy != null ? new RequestProxy[] { _proxy } : Array.Empty<RequestProxy>();

    // Anzeigename eines Ziels (Server bzw. „Rechner · Modell"), für die Aufnahmen entfernt bedienter Anfragen
    public string DisplayName(string key) => _names.TryGetValue(key, out var n) ? n : key;

    // Letzter Fehler beim Einschalten (mit Sekunden bis zum nächsten Versuch), sonst null. key bleibt zur Anzeige-Kompatibilität.
    public string? FailureFor(string key, out int secondsToRetry)
    {
        secondsToRetry = 0;
        if (_failed is not { } f) return null;
        secondsToRetry = Math.Max(0, (int)Math.Ceiling((f.RetryAt - _now()).TotalSeconds));
        return f.Message;
    }

    // Vom Nutzer: global ein- oder ausschalten. Gibt true zurück, wenn der Proxy danach läuft; Fehlermeldung in error.
    public bool Toggle(out string? error)
    {
        error = null;
        if (Running) { Stop(); _settings.ProxyEnabled = false; _settings.Save(); return false; }
        if (!TryStart(out error)) { _failed = null; return false; }   // der Nutzer sieht die Meldung sofort; kein stilles Wiederholen
        _settings.ProxyEnabled = true; _settings.Save();
        return true;
    }

    // Serviertes Modell wählen: key = Server-Key, model = Modellname ("" = erstes/einziges Modell); key "" = Auto.
    // Bei einem Cloud-Ziel trägt key schon das Modell (Zielkennung), dann wird kein Modell nachgetragen.
    public void SetTarget(string key, string? model = null)
    {
        _settings.ProxyTarget = key ?? "";
        _settings.ProxyTargetModel = _settings.ProxyTarget.Length == 0 || ProxyTarget.IsCloudKey(_settings.ProxyTarget) ? "" : (model ?? "");
        _settings.Save();
        PushTargets();
    }

    // Ein Eintrag der Auswahl (lokal oder Cloud) als Ziel setzen – die Oberfläche kennt nur den Wert der Auswahl
    public void SetChoice(ProxyChoice c) => SetTarget(c.Key, c.Cloud ? null : c.Model);

    // Der Wert aus der Oberfläche: „<Server-Key>|<Modell>" oder eine Cloud-Zielkennung. Die TUI schickt nur den
    // Server-Key (dann bleibt das Modell wie bisher offen). Leer = Auto.
    public void SetChoiceValue(string value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) { SetTarget(""); return; }
        if (ProxyTarget.IsCloudKey(v)) { SetTarget(v); return; }
        int bar = v.IndexOf('|');
        if (bar > 0) { SetTarget(v[..bar], v[(bar + 1)..]); return; }
        SetTarget(v);
    }

    // Und umgekehrt: der Wert, den die Auswahl für das gerade eingestellte Ziel zeigen muss ("" = Auto)
    public string ChoiceValue()
    {
        if (_settings.ProxyTarget.Length == 0) return "";
        if (ProxyTarget.IsCloudKey(_settings.ProxyTarget)) return _settings.ProxyTarget;
        return $"{_settings.ProxyTarget}|{_settings.ProxyTargetModel}";
    }

    // Angehängten Rechner hinzufügen/entfernen (Name + Basis-URL seines Stykker-Proxys)
    public void AddRemote(string name, string url)
    {
        url = (url ?? "").Trim().TrimEnd('/');
        if (url.Length == 0) return;
        _settings.RemoteStykkers.RemoveAll(r => RemoteKey(r).Equals(url, StringComparison.OrdinalIgnoreCase));
        _settings.RemoteStykkers.Add(new RemoteStykker { Name = (name ?? "").Trim(), Url = url });
        _settings.Save();
        RefreshRemotes();
        PushTargets();
    }

    public void RemoveRemote(string url)
    {
        var key = (url ?? "").Trim().TrimEnd('/');
        _settings.RemoteStykkers.RemoveAll(r => RemoteKey(r).Equals(key, StringComparison.OrdinalIgnoreCase));
        lock (_remoteLock) _remote.Remove(key);
        _settings.Save();
        PushTargets();
    }

    // ── Cloud-Anbieter (Nr. 46) ──

    // Anbieter eintragen: Name, Basis-URL und Schlüssel. Der Schlüssel wird DPAPI-geschützt abgelegt (ProviderKeys) und
    // erst dann wird der Anbieter abgefragt – ohne ihn geht keine Anfrage ins Netz (Opt-in je Anbieter).
    public void AddProvider(string name, string url, string key, out string? error)
    {
        error = null;
        url = ProviderKeys.Normalize(url);
        name = (name ?? "").Trim();
        if (url.Length == 0 || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { error = Strings.ProxyProviderUrlInvalid; return; }
        if (string.IsNullOrWhiteSpace(key)) { error = Strings.ProxyProviderKeyMissing; return; }
        if (name.Length == 0) name = ProviderLabel(url);
        _keys?.Set(url, key);
        _settings.ProxyProviders.RemoveAll(p => ProviderKeys.Normalize(p.BaseUrl).Equals(url, StringComparison.OrdinalIgnoreCase));
        _settings.ProxyProviders.Add(new ProxyProvider { Name = name, BaseUrl = url });
        _settings.Save();
        lock (_providerLock) _provider.Remove(url);
        RefreshProviders();
        PushTargets();
    }

    // Schlüssel ersetzen (der Anbieter bleibt eingetragen); leer = Schlüssel entfernen, dann wird er nicht mehr abgefragt
    public void SetProviderKey(string url, string key, out string? error)
    {
        error = null;
        url = ProviderKeys.Normalize(url);
        if (!_settings.ProxyProviders.Any(p => ProviderKeys.Normalize(p.BaseUrl).Equals(url, StringComparison.OrdinalIgnoreCase)))
        { error = Strings.ProxyProviderUnknown; return; }
        _keys?.Set(url, key);
        lock (_providerLock) _provider.Remove(url);
        RefreshProviders();
        PushTargets();
    }

    // Anbieter mit seinem Schlüssel wieder entfernen
    public void RemoveProvider(string url)
    {
        var key = ProviderKeys.Normalize(url);
        _settings.ProxyProviders.RemoveAll(p => ProviderKeys.Normalize(p.BaseUrl).Equals(key, StringComparison.OrdinalIgnoreCase));
        _keys?.Remove(key);
        lock (_providerLock) _provider.Remove(key);
        // War gerade ein Modell dieses Anbieters das Ziel, wird wieder automatisch gewählt (lokal)
        if (ProxyTarget.IsCloudKey(_settings.ProxyTarget) && _settings.ProxyTarget.StartsWith($"provider:{key}|", StringComparison.OrdinalIgnoreCase)) SetTarget("");
        else PushTargets();
        _settings.Save();
    }

    public bool HasProviderKey(string url) => _keys?.Has(url) == true;

    // Der Anzeigename, wenn der Nutzer keinen angegeben hat: die Host-Adresse der Basis-URL
    internal static string ProviderLabel(string url)
    {
        try { return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url; }
        catch { return url; }
    }

    // Die Anbieter für die Anzeige: Name, URL, ob ein Schlüssel hinterlegt ist, ob die Modelle kamen, und der letzte Fehler.
    // Der Schlüssel selbst steht nicht in dieser Liste.
    // Die angehängten Stykker-Rechner mit ihrem letzten Stand (erreichbar, angebotene Modelle)
    public IReadOnlyList<(string Name, string Url, bool Ready, string[] Models)> RemoteStates()
        => _settings.RemoteStykkers.Select(r =>
        {
            var url = RemoteKey(r);
            RemoteState? st;
            lock (_remoteLock) st = _remote.TryGetValue(url, out var cur) ? cur : null;
            return (string.IsNullOrWhiteSpace(r.Name) ? r.Url : r.Name, url, st?.Ready == true, st?.Models ?? Array.Empty<string>());
        }).ToList();

    public IReadOnlyList<(string Name, string Url, bool HasKey, bool Ready, string[] Models, string Error)> ProviderStates()
        => _settings.ProxyProviders.Select(p =>
        {
            var url = ProviderKeys.Normalize(p.BaseUrl);
            ProviderState? st;
            lock (_providerLock) st = _provider.TryGetValue(url, out var cur) ? cur : null;
            return (string.IsNullOrWhiteSpace(p.Name) ? ProviderLabel(url) : p.Name, url, _keys?.Has(url) == true, st?.Ready == true, st?.Models ?? Array.Empty<string>(), st?.Error ?? "");
        }).ToList();

    // Proxy auch im LAN anbieten (oder zurück auf Loopback) – bindet neu
    public void SetBindLan(bool on)
    {
        if (_settings.ProxyBindLan == on) return;
        _settings.ProxyBindLan = on;
        _settings.Save();
        Restart();
    }

    private bool TryStart(out string? error)
    {
        error = null;
        StartAttempts++;
        var proxy = _factory(_settings.ProxyPort);
        proxy.BindLan = _settings.ProxyBindLan;
        proxy.Recorded += _onRecord;
        proxy.UpstreamError += OnUpstreamError;
        // Der Schlüssel eines Cloud-Ziels kommt hier herein und verlässt den Router nur als Authorization-Kopfzeile
        if (_keys != null) proxy.AuthFor = t => t.Cloud ? _keys.Get(ProviderBaseUrl(t.Key)) : null;
        proxy.HostClient = HostClient;
        proxy.HostStart = HostStart;
        proxy.StartableModels = StartableModels;
        try { proxy.Start(); }
        catch (Exception ex)
        {
            proxy.Dispose();
            error = Strings.ProxyStartFailed(ex.Message);
            _failed = (_now() + RetryInterval, ex.Message);
            return false;
        }
        _proxy?.Dispose();
        _proxy = proxy;
        _failed = null;
        PushTargets();
        RefreshRemotes();
        RefreshProviders();
        return true;
    }

    // Die Basis-URL aus einer Cloud-Zielkennung („provider:<Basis-URL>|<Modell>")
    internal static string ProviderBaseUrl(string targetKey)
    {
        if (!ProxyTarget.IsCloudKey(targetKey)) return "";
        int bar = targetKey.IndexOf('|');
        return bar > 0 ? targetKey["provider:".Length..bar] : "";
    }

    private void Stop()
    {
        _proxy?.Dispose();
        _proxy = null;
        _failed = null;
    }

    // Neu starten, damit ein geänderter Port/ein geändertes Ziel sofort greift
    public void Restart()
    {
        if (!_settings.ProxyEnabled) return;
        Stop();
        Maintain();
    }

    // Im UI-Takt: eingeschaltet → sicherstellen, dass der Proxy läuft und die Ziele aktuell sind; ausgeschaltet → ihn beenden.
    public void Maintain()
    {
        if (!_settings.ProxyEnabled)
        {
            if (_proxy != null) Stop();
            return;
        }
        if (_proxy == null)
        {
            if (_failed is { } f && _now() < f.RetryAt) return;   // nicht jede Sekunde neu versuchen
            TryStart(out _);
            return;
        }
        RefreshRemotes();
        PushTargets();
    }

    // ── Modelle angehängter Rechner einsammeln ──

    private static string RemoteKey(RemoteStykker r) => r.Url.TrimEnd('/');

    // Höchstens alle RemoteInterval und nie doppelt gleichzeitig abfragen
    private void RefreshRemotes()
    {
        var remotes = AllRemotes();
        if (remotes.Count == 0) { lock (_remoteLock) _remote.Clear(); return; }
        if (_remoteBusy) return;
        var now = _now();
        bool due = remotes.Any(r =>
        {
            lock (_remoteLock) return !_remote.TryGetValue(RemoteKey(r), out var st) || (now - st.LastTry) >= RemoteInterval;
        });
        if (!due) return;
        _remoteBusy = true;
        _ = Task.Run(async () =>
        {
            try { await RefreshRemotesAsync().ConfigureAwait(false); }
            catch { }
            finally { _remoteBusy = false; }
        });
    }

    private async Task RefreshRemotesAsync()
    {
        foreach (var r in AllRemotes())
        {
            var key = RemoteKey(r);
            var st = new RemoteState { LastTry = _now() };
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var json = await _http.GetStringAsync(key + "/v1/models", cts.Token).ConfigureAwait(false);
                st.Models = ParseModelIds(json);
                st.Ready = st.Models.Length > 0;
            }
            catch { st.Ready = false; }
            lock (_remoteLock) _remote[key] = st;
        }
        PushTargets();   // die neuen Modelle sofort anbieten
    }

    // {"data":[{"id":"…"}]} → Modellnamen (tolerant; andere Backends/Versionen liefern zusätzliche Felder)
    internal static string[] ParseModelIds(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
                return d.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
                    .Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToArray();
        }
        catch { }
        return Array.Empty<string>();
    }

    // ── Modelle der Cloud-Anbieter einsammeln ──

    // Höchstens alle ProviderInterval, nie doppelt gleichzeitig und nur für Anbieter mit hinterlegtem Schlüssel
    private void RefreshProviders()
    {
        var providers = _settings.ProxyProviders;
        if (providers.Count == 0 || _keys == null) { lock (_providerLock) _provider.Clear(); return; }
        if (_providerBusy) return;
        var now = _now();
        bool due = providers.Any(p =>
        {
            var url = ProviderKeys.Normalize(p.BaseUrl);
            if (!_keys.Has(url)) return false;   // ohne Schlüssel wird nicht abgefragt: nichts verlässt diesen Rechner
            lock (_providerLock) return !_provider.TryGetValue(url, out var st) || (now - st.LastTry) >= ProviderInterval;
        });
        if (!due) return;
        _providerBusy = true;
        _ = Task.Run(async () =>
        {
            try { await RefreshProvidersAsync().ConfigureAwait(false); }
            catch { }
            finally { _providerBusy = false; }
        });
    }

    // Die Modelle live beim Anbieter holen – der einzige Ort, der dafür einen Schlüssel verwendet
    private async Task RefreshProvidersAsync()
    {
        foreach (var p in _settings.ProxyProviders.ToList())
        {
            var url = ProviderKeys.Normalize(p.BaseUrl);
            var st = new ProviderState { LastTry = _now() };
            var key = _keys?.Get(url);
            if (string.IsNullOrEmpty(key)) { st.Error = Strings.ProxyProviderKeyMissing; lock (_providerLock) _provider[url] = st; continue; }
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url + "/models");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) st.Error = Strings.ProxyProviderModelError((int)resp.StatusCode);
                else
                {
                    st.Models = ParseModelIds(json);
                    st.Ready = st.Models.Length > 0;
                    if (!st.Ready) st.Error = Strings.ProxyProviderNoModels;
                }
            }
            catch (Exception ex) { st.Error = Strings.ProxyProviderOffline(ex.Message); }
            lock (_providerLock) _provider[url] = st;
        }
        PushTargets();   // die neuen Modelle sofort anbieten
    }

    // ── Ziele zusammenstellen und dem Router geben ──

    private void PushTargets()
    {
        if (_proxy == null) return;
        var targets = BuildTargets();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in targets) names[t.Key] = t.DisplayName.Length > 0 ? t.DisplayName : t.Key;
        _names = names;
        _proxy.SetTargets(targets, DefaultKey(targets));
    }

    // Alle Ziele: je geladenes Modell eines lokalen Servers eines, dazu die Modelle der angehängten Rechner und der
    // eingetragenen Cloud-Anbieter (letztere nur, solange sie erreichbar waren und ein Schlüssel hinterlegt ist)
    private List<ProxyTarget> BuildTargets()
    {
        var targets = new List<ProxyTarget>();
        foreach (var s in _servers())
            foreach (var t in LocalTargets(s))
                targets.Add(t);

        // Server der Model-Hosts: nach den lokalen (bei gleichem Namen gewinnt dieser PC)
        IEnumerable<HostServers> hosts;
        try { hosts = HostSources?.Invoke()?.ToList() ?? new List<HostServers>(); }
        catch (InvalidOperationException) { hosts = new List<HostServers>(); }
        foreach (var h in hosts)
            foreach (var rs in h.Servers)
                foreach (var t in HostTargets(h, rs))
                    targets.Add(t);

        // Namensgleichheit: lokale Modelle zuerst; entfernte bekommen „<Rechner>/<Modell>"
        var used = new HashSet<string>(targets.Select(t => t.Model).Where(m => !string.IsNullOrEmpty(m))!, StringComparer.OrdinalIgnoreCase);
        foreach (var r in AllRemotes())
        {
            var url = RemoteKey(r);
            string name = string.IsNullOrWhiteSpace(r.Name) ? r.Url : r.Name;
            RemoteState? st;
            lock (_remoteLock) st = _remote.TryGetValue(url, out var cur) ? cur : null;
            foreach (var m in st?.Models ?? Array.Empty<string>())
            {
                string pub = used.Contains(m) ? $"{name}/{m}" : m;
                used.Add(pub);
                targets.Add(new ProxyTarget($"remote:{url}|{m}", url, m, BackendKind.LmStudio, st?.Ready == true, false,
                    ServerKey: "", Remote: true, PublicModel: pub, DisplayName: $"{name} · {m}"));
            }
        }

        // Cloud-Anbieter: der Namensraum „<Anbieter>/<Modell>" gilt immer, damit zwei Anbieter nie dasselbe Modell anbieten
        foreach (var p in _settings.ProxyProviders.ToList())
        {
            var url = ProviderKeys.Normalize(p.BaseUrl);
            string name = string.IsNullOrWhiteSpace(p.Name) ? ProviderLabel(url) : p.Name;
            ProviderState? st;
            lock (_providerLock) st = _provider.TryGetValue(url, out var cur) ? cur : null;
            foreach (var m in st?.Models ?? Array.Empty<string>())
                targets.Add(new ProxyTarget(ProxyTarget.CloudKey(url, m), url, m, BackendKind.LmStudio, st?.Ready == true, false,
                    ServerKey: "", Remote: true, PublicModel: $"{name}/{m}", DisplayName: $"{name} · {m}", Cloud: true));
        }
        return targets;
    }

    // Die Ziele eines Servers auf einem Model-Host. Angeboten wird der Name der Modelldatei (ohne Pfad und .gguf).
    internal static IEnumerable<ProxyTarget> HostTargets(HostServers h, RemoteServer rs)
    {
        string prefix = $"host:{h.HostId}|{rs.Key}|";
        if (rs.Backend == "llama.cpp")
        {
            var model = !string.IsNullOrEmpty(rs.Model) && rs.Model != "–" ? rs.Model : rs.Name;
            yield return new ProxyTarget(prefix + model, rs.Url, model, BackendKind.LlamaCpp, rs.Online && !rs.Loading, rs.Loading,
                Remote: true, PublicModel: ServerInfo.ModelName(model), DisplayName: $"{h.Name} · {rs.Name}", HostId: h.HostId);
            yield break;
        }
        foreach (var m in rs.Models.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            yield return new ProxyTarget(prefix + m, rs.Url, m, BackendKind.LmStudio, rs.Online, false,
                Remote: true, DisplayName: $"{h.Name} · {m}", HostId: h.HostId);
    }

    // Die Ziele eines lokalen Servers: llama.cpp führt ein Modell, Ollama/LM Studio je geladenes Modell eines
    private static IEnumerable<ProxyTarget> LocalTargets(ServerWatcher s)
    {
        if (s.Kind == BackendKind.LlamaCpp)
        {
            var model = ModelOf(s);
            // Anzeige kurz halten: llama.cpp liefert als „model"-ID oft den vollen Dateipfad (der bleibt zum Routen nötig).
            // Die Kontextgröße mit anzeigen: macht den häufigen „Anfrage > Ziel-Kontext"-Fehler sofort sichtbar.
            var ctx = s.Props?.NCtx is int n && n > 0 ? $" · ctx {n / 1024}k" : "";
            yield return new ProxyTarget($"{s.Key}|{model}", s.Url, model, s.Kind, s.Online, s.Loading,
                ServerKey: s.Key, DisplayName: s.Name + ctx);
            yield break;
        }
        var models = s.Models.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (models.Count == 0)
        {
            // Nichts geladen: als Standardziel (Pfade ohne Modell) weiterhin brauchbar
            yield return new ProxyTarget($"{s.Key}|", s.Url, null, s.Kind, s.Online, false, ServerKey: s.Key, DisplayName: s.Name);
            yield break;
        }
        foreach (var m in models)
            yield return new ProxyTarget($"{s.Key}|{m}", s.Url, m, s.Kind, s.Online, false,
                ServerKey: s.Key, DisplayName: $"{s.Name} · {m}");
    }

    private static string? ModelOf(ServerWatcher s)
    {
        if (s.Kind == BackendKind.LlamaCpp)
            return !string.IsNullOrEmpty(s.Model) && s.Model != "–" ? s.Model : s.Name;
        var name = s.Models.Select(m => m.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n));
        return name ?? (string.IsNullOrEmpty(s.Model) || s.Model == "–" ? null : s.Model);
    }

    // Das Standardziel: das ausdrücklich gewählte Ziel (Server+Modell oder Cloud-Zielkennung), sonst Auto.
    // Auto nimmt nie ein Cloud-Modell: dafür müsste der Nutzer den Anbieter erst eingetragen haben.
    private string? DefaultKey(IReadOnlyList<ProxyTarget> targets)
    {
        var want = _settings.ProxyTarget;
        if (!string.IsNullOrEmpty(want))
        {
            var exact = targets.FirstOrDefault(t => t.Key == want);   // Cloud-Ziel: die Kennung trägt das Modell
            if (exact != null) return exact.Key;
            var model = _settings.ProxyTargetModel;
            var match = targets.FirstOrDefault(t => t.ServerKey == want && t.Model != null
                && (model.Length == 0 || t.Model.Equals(model, StringComparison.OrdinalIgnoreCase)));
            if (match != null) return match.Key;
        }
        // Auto = der erste laufende llama.cpp-Server, sonst der erste erreichbare lokale Server, sonst ein angehängter Rechner
        var local = targets.Where(t => !t.Remote && !t.Cloud).ToList();
        var llama = local.Where(t => t.Kind == BackendKind.LlamaCpp && t.Ready).ToList();
        var pick = llama.FirstOrDefault() ?? local.FirstOrDefault(t => t.Ready) ?? local.FirstOrDefault();
        return pick?.Key ?? targets.FirstOrDefault(t => !t.Cloud)?.Key;
    }

    // Auswahl für das servierte Modell: die aktiven lokalen Modelle (Server-Key + Modellname) mit Anzeigename
    public IReadOnlyList<(string ServerKey, string Model, string Display)> ServeChoices()
        => BuildTargets().Where(t => !t.Remote && t.Model != null)
            .Select(t => (t.ServerKey, t.Model!, t.DisplayName)).ToList();

    // Dieselbe Auswahl für die Oberfläche, nur mit Cloud-Modellen („<Anbieter>/<Modell>") dahinter; die Fenster liest sie
    // aus dem Zustand, das Web aus der Engine – beide bekommen dieselbe Liste.
    public IReadOnlyList<ProxyChoice> Choices()
    {
        var targets = BuildTargets();
        return targets.Where(t => t.Model != null && !t.Remote)
            .Select(t => new ProxyChoice(t.ServerKey, t.Model!, t.DisplayName, false))
            .Concat(targets.Where(t => t.Cloud && t.Model != null)
                .Select(t => new ProxyChoice(t.Key, t.EffectiveModel ?? t.Model!, Strings.ProxyProviderTarget(t.DisplayName), true)))
            .ToList();
    }

    // Bedient der Proxy gerade dieses Server-Modell als virtuelles „stykker"? (für den Karten-Chip)
    public bool IsServed(string serverKey)
    {
        if (!Running) return false;
        var targets = BuildTargets();
        var t = targets.FirstOrDefault(x => x.Key == DefaultKey(targets));
        return t != null && !t.Remote && t.ServerKey == serverKey;
    }

    // Server-Key des servierten Ziels (für „Open log" im anhaltenden Hinweis); null bei einem entfernten Ziel
    public string? ServedKey()
    {
        var targets = BuildTargets();
        var t = targets.FirstOrDefault(x => x.Key == DefaultKey(targets));
        return t is { Remote: false } ? t.ServerKey : null;
    }

    // ── Fehlerantworten auswerten (Kontext zu klein) ──

    private static readonly System.Text.RegularExpressions.Regex CtxTooSmall = new(
        @"request \((\d+)\s*tokens\) exceeds the available context size \((\d+)\s*tokens\)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // „… request (21203 tokens) exceeds the available context size (4096 tokens) …" → verständlicher Text (sonst null)
    internal static string? ContextHint(string body)
    {
        if (string.IsNullOrEmpty(body) || !body.Contains("available context size", StringComparison.OrdinalIgnoreCase)) return null;
        var m = CtxTooSmall.Match(body);
        if (!m.Success) return Strings.ProxyContextTooSmallGeneric;
        return long.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var req)
            && long.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ctx)
            ? Strings.ProxyContextTooSmall(req, ctx)
            : Strings.ProxyContextTooSmallGeneric;
    }

    private string? _lastHint;
    private DateTime _lastHintAt = DateTime.MinValue;

    private void OnUpstreamError(string body)
    {
        var msg = ContextHint(body);
        if (msg == null) return;
        var now = _now();
        if (msg == _lastHint && (now - _lastHintAt).TotalSeconds < 15) return;   // ein Retry des Clients soll nicht mehrfach blinken
        _lastHint = msg; _lastHintAt = now;
        try { Hint?.Invoke(msg); } catch { }
    }

    // Server, an die der Proxy gerade weiterleiten kann (für Anzeige, z. B. „target: <Server>")
    public string TargetName()
    {
        var targets = BuildTargets();
        var key = DefaultKey(targets);
        if (key == null) return Strings.ProxyTargetNone;
        return DisplayNameOrKey(targets, key);
    }

    private static string DisplayNameOrKey(IReadOnlyList<ProxyTarget> targets, string key)
    {
        var t = targets.FirstOrDefault(x => x.Key == key);
        return t == null ? key : (t.DisplayName.Length > 0 ? t.DisplayName : key);
    }

    public void Dispose()
    {
        Stop();
        if (_ownsHttp) _http.Dispose();
    }
}
// Die laufenden Server eines verbundenen Model-Hosts (für die Ziele des Proxys)
public sealed record HostServers(string HostId, string Name, IReadOnlyList<RemoteServer> Servers);
