namespace StykkerLlm.Core;

// Verwaltet die überwachten Server: Erkennung läuft im Hintergrund (alle 3 s), das Ergebnis wird nur im UI-Takt
// übernommen (Maintain/ApplyPending). Die Liste Servers ändert sich deshalb nie während die Oberfläche zeichnet.
// Je Server gibt es einen ServerWatcher (Schlüssel Host:Port); verschwundene Server werden erst nach zwei
// Durchläufen entfernt (kurze Lücken in der Tabelle sollen keine Karte flackern lassen).
public sealed class ServerRegistry : IDisposable
{
    private const int MissingPasses = 2;

    private readonly IPlatform _platform;
    private readonly HttpClient _http;
    private readonly ServerDiscovery _discovery;
    private readonly ClientNamer _namer;
    private readonly Library? _library;
    private IReadOnlyList<ManualServer> _manual;
    private readonly Dictionary<string, ServerWatcher> _byKey = new();
    private readonly Dictionary<string, int> _missing = new();
    private volatile NetSnapshot _net = NetSnapshot.Empty;
    private DiscoveryResult? _pending;
    private Task? _running;
    private DateTime _lastStart = DateTime.MinValue;
    private IReadOnlyList<ServerWatcher> _servers = Array.Empty<ServerWatcher>();
    private bool _disposed;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(3);
    // Wird ausgelöst, wenn sich die Liste geändert hat (nur im UI-Takt)
    public event Action? ServersChanged;
    public event Action<FinishedRequest>? RequestFinished;
    // Ein lokaler llama.cpp-Server ist verschwunden, ohne dass der Monitor ihn gestoppt hat (nur im UI-Takt)
    public event Action<ServerLost>? ServerLost;
    public IReadOnlyList<ServerWatcher> Servers => _servers;

    // Vom Monitor gestoppte Server (Schlüssel → Zeitpunkt): ihr Verschwinden ist erwartet und meldet keinen Absturz
    private readonly Dictionary<string, DateTime> _expectedStops = new();
    private static readonly TimeSpan ExpectedStopWindow = TimeSpan.FromMinutes(2);
    public void ExpectStop(string key) => _expectedStops[key] = DateTime.Now;
    public NetSnapshot Net => _net;
    public ServerDiscovery Discovery => _discovery;

    public ServerRegistry(IPlatform platform, HttpClient http, Library? library = null, IEnumerable<ManualServer>? manual = null,
        IReadOnlyList<IBackendProbe>? probes = null, ServerDiscovery? discovery = null)
    {
        _platform = platform;
        _http = http;
        _library = library;
        _manual = manual?.Where(m => Uri.TryCreate(m.Url, UriKind.Absolute, out _)).ToList() ?? new List<ManualServer>();
        _namer = new ClientNamer(platform);
        _discovery = discovery ?? new ServerDiscovery(platform, probes, http);
    }

    // Manuelle Server (Docker, WSL, anderer Rechner) neu setzen; wirkt beim nächsten Erkennungsdurchlauf
    public void SetManualServers(IEnumerable<ManualServer> manual)
    {
        _manual = manual.Where(m => Uri.TryCreate(m.Url, UriKind.Absolute, out _)).ToList();
        _lastStart = DateTime.MinValue;
    }

    // Manuellen Server sofort entfernen (Liste der übrigen manuellen Server wird neu gesetzt)
    public void RemoveManual(string key, IEnumerable<ManualServer> remaining)
    {
        _manual = remaining.Where(m => Uri.TryCreate(m.Url, UriKind.Absolute, out _)).ToList();
        if (_byKey.TryGetValue(key, out var w) && w.Info.Manual)
        {
            _byKey.Remove(key);
            _missing.Remove(key);
            w.RequestFinished -= OnRequestFinished;
            w.Dispose();
            _servers = _byKey.Values.OrderBy(x => x.Info.Port).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
            ServersChanged?.Invoke();
        }
    }

    // ── Vom Monitor gestartete Server (solange sie noch nicht lauschen oder wenn sie fehlgeschlagen sind) ──
    private IReadOnlyList<LaunchedServer> _launches = Array.Empty<LaunchedServer>();
    public IReadOnlyList<LaunchedServer> Launches => _launches;

    // So lange darf ein gestarteter Server brauchen, bis er lauscht; danach "did not start listening" mit Log-Ende
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public LaunchedServer Launch(LaunchPlan plan)
    {
        var ls = ServerLauncher.Start(plan, _platform);
        _launches = _launches.Append(ls).ToList();
        _lastStart = DateTime.MinValue;   // nächste Erkennung sofort
        return ls;
    }

    // Einträge, die zu lange nicht lauschen, als fehlgeschlagen kennzeichnen (der Prozess wird nicht beendet)
    private bool CheckLaunchTimeouts(DateTime now)
    {
        bool changed = false;
        foreach (var l in _launches)
            if (l.State == LaunchState.Starting && now - l.Started > StartTimeout) { l.MarkTimedOut(StartTimeout); changed = true; }
        return changed;
    }

    public void DismissLaunch(Guid id)
    {
        var l = _launches.FirstOrDefault(x => x.Id == id);
        if (l == null) return;
        _launches = _launches.Where(x => x.Id != id).ToList();
        l.Dispose();
    }

    // UI-Takt: fertiges Ergebnis übernehmen, bei Bedarf einen neuen Durchlauf anstoßen. true = Liste hat sich geändert.
    public bool Maintain(DateTime now)
    {
        if (_disposed) return false;
        bool changed = ApplyPending();
        changed |= CheckLaunchTimeouts(now);
        if (_running is { IsCompleted: false } || now - _lastStart < Interval) return changed;
        _lastStart = now;
        _running = RunPassAsync();
        return changed;
    }

    // Sofort einen Durchlauf abwarten und übernehmen (erster Takt, Tests, Snapshot)
    public async Task<bool> RefreshNowAsync()
    {
        if (_disposed) return false;
        if (_running is not { IsCompleted: false })
        {
            _lastStart = DateTime.Now;
            _running = RunPassAsync();
        }
        await _running;
        return ApplyPending();
    }

    private async Task RunPassAsync()
    {
        try
        {
            var r = await Task.Run(() => _discovery.RunAsync());
            _net = r.Net;
            Interlocked.Exchange(ref _pending, r);
        }
        catch { /* der nächste Durchlauf versucht es neu */ }
    }

    private bool ApplyPending()
    {
        var r = Interlocked.Exchange(ref _pending, null);
        if (r == null) return false;

        var merged = new Dictionary<string, ServerInfo>();
        foreach (var s in r.Servers) merged[s.Key] = s;
        foreach (var m in _manual)
        {
            var uri = new Uri(m.Url);
            var host = uri.DnsSafeHost;
            var key = NetAddr.Key(host, uri.Port);
            if (merged.ContainsKey(key)) continue;   // schon als lokaler Prozess erkannt
            merged[key] = new ServerInfo
            {
                Key = key, Host = host, Port = uri.Port, Manual = true, DetectedBy = "manual", ManualName = m.Name, ManualLog = m.Log,
                Mode = ServerMode.Normal, Backend = BackendProbes.KindFromName(m.Kind) ?? BackendKind.LlamaCpp,
            };
        }

        bool changed = false;
        var lost = new List<ServerLost>();
        foreach (var k in _expectedStops.Where(e => DateTime.Now - e.Value > ExpectedStopWindow).Select(e => e.Key).ToList()) _expectedStops.Remove(k);
        foreach (var (key, info) in merged)
        {
            _missing.Remove(key);
            if (_byKey.TryGetValue(key, out var w)) { w.UpdateInfo(info); continue; }
            w = new ServerWatcher(info, _http, _platform, () => _net, _namer);
            w.RequestFinished += OnRequestFinished;
            _byKey[key] = w;
            changed = true;
        }
        foreach (var key in _byKey.Keys.Where(k => !merged.ContainsKey(k)).ToList())
        {
            int n = _missing.GetValueOrDefault(key) + 1;
            if (n < MissingPasses) { _missing[key] = n; continue; }
            _missing.Remove(key);
            var w = _byKey[key];
            _byKey.Remove(key);
            bool expected = _expectedStops.Remove(key, out var at) && DateTime.Now - at < ExpectedStopWindow;
            string end = expected ? "clean" : "outside";
            if (!expected && w.Kind == BackendKind.LlamaCpp && !w.Info.Manual && w.Info.Pid != null)
            {
                var tail = CrashAnalysis.ReadTail(w.Log);
                var cause = CrashAnalysis.Guess(tail);
                end = cause != null ? "crashed" : "lost";
                lost.Add(new ServerLost(w.Name, w.Info.Url, w.Info.Pid, w.Log, tail, cause, DateTime.Now, w.ProfileKey));
            }
            if (w.Kind == BackendKind.LlamaCpp && w.Info.Pid != null) _library?.RecordEnd(w.Info, end, DateTime.Now);
            w.RequestFinished -= OnRequestFinished;
            w.Dispose();
            changed = true;
        }
        // Gestartete Server, die jetzt lauschen, sind keine "starting"-Einträge mehr
        foreach (var l in _launches.Where(l => (l.State == LaunchState.Starting || l.StillRunning) && merged.Values.Any(s => s.Pid == l.Pid)).ToList())
        {
            DismissLaunch(l.Id);
            changed = true;
        }
        if (changed)
        {
            _servers = _byKey.Values.OrderBy(w => w.Info.Port).ThenBy(w => w.Key, StringComparer.Ordinal).ToList();
            ServersChanged?.Invoke();
        }
        foreach (var l in lost) ServerLost?.Invoke(l);
        return changed;
    }

    private void OnRequestFinished(FinishedRequest f) => RequestFinished?.Invoke(f);

    // UI-Takt nach dem Abfragen: Verlauf und Statistik der Bibliothek fortführen, verzögert speichern
    public void ObserveLibrary(DateTime now)
    {
        if (_library == null) return;
        foreach (var w in _servers)
        {
            if (w.Info.Pid == null || w.Kind != BackendKind.LlamaCpp) continue;
            _library.Observe(w.Info, new Observation(w.Current, w.VramGb, w.ModelFileGb, w.Props?.NCtx, w.GeneratedTotal), now);
        }
        _library.SaveIfDirty(TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var l in _launches) l.Dispose();
        _launches = Array.Empty<LaunchedServer>();
        foreach (var w in _byKey.Values) { w.RequestFinished -= OnRequestFinished; w.Dispose(); }
        _byKey.Clear();
        _servers = Array.Empty<ServerWatcher>();
        _library?.Save();
    }
}
