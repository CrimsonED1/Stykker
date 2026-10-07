namespace StykkerLlm.Core;

// Anhaltender Hinweis für die Oberfläche (z. B. „Ziel-Kontext zu klein"): bleibt sichtbar, bis der Nutzer ihn bestätigt.
// LogFile ist gesetzt, wenn die Logdatei des betroffenen Servers weiterhilft (Knopf „Open log").
public sealed record MonitorNotice(string Text, bool Alarm = false, string? LogFile = null);

// Der Messtakt ohne Oberfläche: Erkennung, Abfragen der Server, Bibliothek, Proxys, Aufnahmen, GPU- und Systemwerte, Alarme und
// Aufräumen (Logs, Aufnahmen, Bibliothek im Nur-Lesen-Modus). Die Oberfläche (MAUI) ruft TickAsync im Takt auf,
// zeichnet danach den Zustand und hört auf die Ereignisse Notice und Alarm. Die Plattform gehört dem Aufrufer.
public sealed class MonitorEngine : IDisposable
{
    private const double VramWarnGb = 1.0;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<DateTime> _now;
    private bool _discovered, _polling, _vramLow, _disposed;
    private DateTime _lastGpuRead = DateTime.MinValue, _lastProcQuery = DateTime.MinValue, _lastPrune = DateTime.MinValue, _lastRecover = DateTime.MinValue;
    private volatile bool _procBusy;
    private volatile List<(string Name, double Gb)> _vramTop = new();

    public IPlatform Platform { get; }
    public AppPaths Paths { get; }
    public AppSettings Settings { get; }
    public Library Library { get; }
    public ServerRegistry Registry { get; }
    public RecordingManager Recorder { get; }
    public ProxyManager Proxies { get; }
    public RequestLog Csv { get; }
    // Hinweis für die Oberfläche, wenn der Pfad des Anfrageprotokolls außerhalb des Datenordners lag und ersetzt wurde; sonst null
    public string? CsvLogNote { get; }

    public IReadOnlyList<ServerWatcher> Servers => Registry.Servers;
    public GpuSample? Gpu { get; private set; }
    public SystemSample? Sys { get; private set; }
    public IReadOnlyList<(string Name, double Gb)> VramTop => _vramTop;
    private volatile List<(string Name, double Gb)> _ramTop = new();
    // größte RAM-Belegungen je Programm (alle 5 s, Hintergrund-Task)
    public IReadOnlyList<(string Name, double Gb)> RamTop => _ramTop;
    // GPU-Auslastung je Prozess in Prozent (alle 5 s, Hintergrund-Task) – für die Liste in den GPU-Details
    private volatile List<(string Name, double Percent)> _gpuUtilTop = new();
    public IReadOnlyList<(string Name, double Percent)> GpuUtilTop => _gpuUtilTop;
    public int Ticks { get; private set; }
    // Ausnahme des letzten Takts (der nächste Takt versucht es neu); null = alles gut
    public Exception? LastError { get; private set; }
    // GPU-Werte in jedem Takt lesen (Testläufe mit Bild); Aufnahmen tun das ohnehin
    public bool ForceGpuEveryTick { get; set; }
    public bool GpuEveryTick => ForceGpuEveryTick || Recorder.Active;
    // Kurze Meldung für die Oberfläche (z. B. "Recording saved …")
    public event Action<string>? Notice;
    // Etwas, das die Aufmerksamkeit des Nutzers verdient (Taskleiste blinken lassen): "low VRAM", "reply truncated", "request cancelled"
    public event Action<string>? Alarm;
    // Anhaltender Hinweis: bleibt in der Oberfläche stehen, bis DismissNotice() gerufen wird
    public MonitorNotice? CurrentNotice { get; private set; }
    public void DismissNotice() => CurrentNotice = null;
    // Anhaltenden Hinweis setzen (Proxy-Hinweis, Skript, Prüfung); null leert ihn
    public void SetNotice(string text, string? logFile = null) => CurrentNotice = new MonitorNotice(text, LogFile: logFile);
    // Die Liste der Aufnahmen hat sich geändert (Aufnahme beendet, gelöscht)
    public event Action? RecordingsChanged;
    // Ein lokaler Server ist verschwunden, ohne vom Monitor gestoppt worden zu sein (Absturz, Fenster zu, von außen beendet)
    public event Action<ServerLost>? ServerLost;
    /// <summary>Ein Server ist so lange still, dass sein Profil ihn jetzt entladen möchte.</summary>
    public event Action<ServerWatcher, Profile>? IdleUnloadDue;

    // Leerlauf-Entladen: merkt sich, wann jeder Server zuletzt gearbeitet hat, und meldet die,
    // deren Profil nach den eingestellten Minuten nichts mehr zu tun hat. Das Stoppen/Entladen macht der Aufrufer –
    // die Engine weiß nicht, wie man einen Server beendet.
    private void CheckIdleUnload(IReadOnlyList<ServerWatcher> servers, DateTime now)
    {
        foreach (var s in servers)
        {
            if (IdleUnload.IsBusy(s)) { s.MarkBusy(now); continue; }
            var profile = FindProfileFor(s);
            if (profile == null || IdleUnload.MinutesOf(profile) <= 0) continue;
            if (IdleUnload.Due(profile, s, now)) IdleUnloadDue?.Invoke(s, profile);
        }
    }

    // Welches gespeicherte Profil gehört zu diesem Server? Über den Bibliotheksschlüssel (Programm + Argumente);
    // hat der Startkoordinator etwas ergänzt, hilft der Port.
    private Profile? FindProfileFor(ServerWatcher s)
    {
        if (s.Info.Program is { Length: > 0 } program)
        {
            var key = Library.MakeKey(program, s.Info.Args);
            var byKey = Library.Profiles.FirstOrDefault(p => p.Key == key);
            if (byKey != null) return byKey;
        }
        return s.Info.Port is int port ? Library.Profiles.FirstOrDefault(p => p.Port == port) : null;
    }

    // Simulation != null: Engine des Simulators (eigene Daten, simulierte Plattform); Starten echter Programme ist dann ausgeschlossen
    public SimWorld? Simulation { get; }
    // Nur beobachten, nichts schreiben (zweite Oberfläche neben der GUI, z. B. "stykker status"): Bibliothek als Momentaufnahme,
    // kein requests.csv, keine Proxys, kein Aufräumen, keine Aufnahmen
    public bool ReadOnly { get; }
    public bool IsSimulated => Simulation != null;

    public MonitorEngine(IPlatform platform, AppPaths paths, AppSettings settings, HttpClient? http = null, Library? library = null, Func<DateTime>? now = null,
        SimWorld? simulation = null, Func<int, RouterProxy>? proxyFactory = null, bool readOnly = false)
    {
        Platform = platform; Paths = paths; Settings = settings; Simulation = simulation; ReadOnly = readOnly;
        _now = now ?? (() => DateTime.Now);
        _ownsHttp = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        Library = library ?? (readOnly ? Library.LoadSnapshot(paths.LibraryFile) : Library.Load(paths.LibraryFile));
        // Startbestätigung: Schlüssel aus confirm.key (DPAPI), sonst gilt keine gespeicherte Bestätigung (siehe ConfirmKey)
        if (library == null) Library.ConfirmKey = ConfirmKey.LoadOrCreate(paths.ConfirmKeyFile, platform, create: !readOnly && simulation == null);
        var limits = settings.Limits;
        Recorder = new RecordingManager(paths.RecordingsDir);
        Recorder.ApplyLimits(limits);
        Registry = new ServerRegistry(platform, _http, Library, settings.ManualServers) { StartTimeout = settings.StartTimeout };
        Registry.RequestFinished += OnRequestFinished;
        Registry.ServerLost += l => ServerLost?.Invoke(l);
        var csvPath = paths.ResolveCsvLog(settings.CsvLog, out var csvNote);
        CsvLogNote = csvNote;
        // Ungültigen Pfad einmal bereinigen (Standard = Datenordner), sonst erscheint der Hinweis bei jedem Start
        if (csvNote != null && !readOnly) { settings.CsvLog = null; try { settings.Save(); } catch { } }
        Csv = new RequestLog(readOnly ? null : csvPath) { MaxBytes = limits.MaxCsvBytes };
        Proxies = new ProxyManager(() => Registry.Servers, settings, OnProxyRecord, factory: proxyFactory, http: _http,
            keys: readOnly ? null : new ProviderKeys(paths.Root, platform));
        // Hinweise des Proxys (z. B. „Ziel-Kontext zu klein") als anhaltenden Hinweis setzen (bleibt bis „Dismiss")
        Proxies.Hint += text =>
        {
            var key = Proxies.ServedKey();
            var log = key == null ? null : Registry.Servers.FirstOrDefault(s => s.Key == key)?.Log;
            SetNotice(text, log);
            Notice?.Invoke(text);
        };

        // Aufräumen nach Abstürzen (Hintergrund): Temp-Dateien, Aufnahmen ohne Ende-Zeile. Nicht im Nur-Lesen-Modus: dort
        // läuft vielleicht die GUI daneben, und ihre gerade laufende Aufnahme hat noch keine Ende-Zeile.
        if (!readOnly) _ = Task.Run(async () =>
        {
            AtomicFile.CleanTemp(paths.Root, TimeSpan.FromHours(1));
            AtomicFile.CleanTemp(paths.RecordingsDir, TimeSpan.FromHours(1));
            if (await Recorder.RepairAsync() > 0) RecordingsChanged?.Invoke();
        });
    }

    // Die letzten Beobachtungen des Proxys (für die Liste „Letzte Anfragen“: Werkzeuge, Denken, Client), neueste zuletzt
    private readonly Queue<ProxyRecord> _recentProxy = new();
    public IReadOnlyList<ProxyRecord> RecentProxy() { lock (_recentProxy) return _recentProxy.ToList(); }

    private void OnProxyRecord(ProxyRecord px)
    {
        lock (_recentProxy)
        {
            _recentProxy.Enqueue(px);
            while (_recentProxy.Count > 64) _recentProxy.Dequeue();
        }
        // Beobachtung des Proxys an alle laufenden Aufnahmen (Sitzungen sperren intern, der Aufruf kommt von einem Hintergrund-Thread)
        var w = Registry.Servers.FirstOrDefault(x => x.Key == px.ServerKey);
        Recorder.OnProxyRecord(px, w?.Name ?? Proxies.DisplayName(px.ServerKey), w?.Model ?? "");
    }

    private void OnRequestFinished(FinishedRequest f)
    {
        Csv.Append(f);
        Recorder.OnLogRequest(f, f.ServerKey, f.Model);
        if (f.Status != ReqStatus.Done) Alarm?.Invoke(f.Status == ReqStatus.Truncated ? "reply truncated" : "request cancelled");
    }

    private static async Task SafePollAsync(ServerWatcher s)
    {
        try { await s.PollAsync(); } catch { }
    }

    // Ein Takt (die Oberfläche ruft ihn jede Sekunde auf). Läuft nie doppelt: ein noch laufender Takt lässt den nächsten ausfallen.
    public async Task TickAsync()
    {
        if (_polling || _disposed) return;
        _polling = true;
        try
        {
            var now = _now();
            // Erkennung: der erste Takt wartet auf den ersten Durchlauf, danach läuft sie im Hintergrund (alle 3 s)
            if (!_discovered) { _discovered = true; await Registry.RefreshNowAsync(); }
            else Registry.Maintain(now);
            var servers = Registry.Servers;
            await Task.WhenAll(servers.Select(SafePollAsync));
            Registry.ObserveLibrary(now);
            CheckIdleUnload(servers, now);
            if (!ReadOnly) Proxies.Maintain();
            // NVML-Aufrufe sind die teuersten Messungen: im Leerlauf nur alle 2 s (bei Aufnahme jede Sekunde)
            if (Gpu == null || GpuEveryTick || (now - _lastGpuRead).TotalSeconds >= 1.9) { Gpu = Platform.ReadGpu(); _lastGpuRead = now; }
            Sys = Platform.ReadSystem();
            if (Recorder.Active)
                foreach (var s in Recorder.Tick(servers, Gpu, Sys)) _ = StopRecordingAsync(s, limitReached: true);
            if (!_procBusy && (now - _lastProcQuery).TotalSeconds >= 5)
            {
                _lastProcQuery = now;
                _procBusy = true;   // Platzhalter-Abfrage (PDH) nicht auf dem UI-Thread
                _ = Task.Run(() =>
                {
                    try { if (Platform.ReadGpuTop(Math.Clamp(Settings.GpuTopCount > 0 ? Settings.GpuTopCount : 8, 3, 32)) is { } top) _vramTop = top.ToList(); }
                    catch { }
                    try { _gpuUtilTop = Platform.ReadGpuUtilTop(64)?.ToList() ?? new(); }
                    catch { }
                    try { _ramTop = Platform.ReadRamTop(12)?.ToList() ?? new(); }
                    catch { }
                    finally { _procBusy = false; }
                });
            }
            CheckVram();
            Csv.Flush();   // nachschreiben, falls die Datei vorher gesperrt war
            if (!ReadOnly) Housekeeping(now);
            Ticks++;
            LastError = null;
        }
        catch (Exception ex)
        {
            // Eine kaputte Antwort oder ein Messfehler darf nie einen Fehlerdialog auslösen; der nächste Takt versucht es neu
            LastError = ex;
        }
        finally { _polling = false; }
    }

    // Alarm: VRAM unter der Schwelle (einmal pro Unterschreitung, mit Hysterese)
    private void CheckVram()
    {
        if (Gpu == null) return;
        if (!_vramLow && Gpu.MemFreeGb < VramWarnGb) { _vramLow = true; Alarm?.Invoke("low VRAM"); }
        else if (_vramLow && Gpu.MemFreeGb > VramWarnGb + 0.3) _vramLow = false;
    }

    // Selten: Bibliothek im Nur-Lesen-Modus erneut versuchen (alle 30 s), alte Logs und Aufnahmen aufräumen (alle 10 min)
    private void Housekeeping(DateTime now)
    {
        if (Library.ReadOnly && now - _lastRecover > TimeSpan.FromSeconds(30))
        {
            _lastRecover = now;
            if (Library.TryRecover()) Notice?.Invoke(Strings.LibraryAvailableAgain);
        }
        if (now - _lastPrune < TimeSpan.FromMinutes(10)) return;
        _lastPrune = now;
        var limits = Settings.Limits;
        var keep = Registry.Launches.Select(l => l.LogFile).Concat(Registry.Servers.Select(s => s.Info.LogFile ?? "")).ToList();
        _ = Task.Run(async () =>
        {
            ServerLauncher.PruneLogs(Paths.LogsDir, limits.MaxLogBytes, keep, limits.MaxLogFiles);
            if (await Recorder.PruneAsync() > 0) RecordingsChanged?.Invoke();
        });
    }

    // ── Aufnahme ──

    // Aufnahme eines Servers starten; null mit Begründung in problem, wenn es nicht geht
    public RecordingSession? StartRecording(ServerWatcher s, out string? problem)
    {
        problem = null;
        if (ReadOnly) { problem = Strings.ReadOnlyEngine; return null; }
        if (Recorder.IsRecordingGlobal) { problem = Strings.GlobalRecordingRunning; return null; }
        var session = Recorder.Start(s.Key, Servers, Gpu?.Name ?? "", Proxies.IsRunningFor(s.Key));
        AfterRecordingStarted();
        return session;
    }

    // Aufnahme aller Server starten; einzelne laufende Aufnahmen gehen darin auf (werden zuvor beendet)
    public async Task<RecordingSession?> StartRecordingAllAsync()
    {
        if (ReadOnly) { Notice?.Invoke(Strings.ReadOnlyEngine); return null; }
        if (Servers.Count == 0) { Notice?.Invoke(Strings.NothingToRecord); return null; }
        foreach (var one in Recorder.Sessions.ToList()) await StopRecordingAsync(one, quiet: true);
        var session = Recorder.Start("all", Servers, Gpu?.Name ?? "", Proxies.AnyRunning);
        AfterRecordingStarted();
        return session;
    }

    private void AfterRecordingStarted()
    {
        _lastGpuRead = DateTime.MinValue;   // frische GPU-Werte schon für den ersten Messpunkt
        _ = Recorder.PruneAsync().ContinueWith(t => { if (t.Result > 0) RecordingsChanged?.Invoke(); }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    // Aufnahme beenden und speichern, ohne die Oberfläche zu blockieren
    public async Task<RecordingSummary?> StopRecordingAsync(RecordingSession s, bool quiet = false, bool limitReached = false)
    {
        var sum = await Recorder.StopAsync(s);
        if (!quiet)
            Notice?.Invoke(limitReached ? Strings.RecordingLimitReached((int)(Recorder.MaxSessionBytes / (1024 * 1024)))
                : sum != null ? Strings.RecordingSaved(sum.Requests, Fmt.Dur(sum.DurationSec)) : Strings.RecordingFailed);
        RecordingsChanged?.Invoke();
        return sum;
    }

    // ── Von Hand hinzugefügte Server ──

    public void AddManualServer(ManualServer m)
    {
        Settings.ManualServers.RemoveAll(x => x.Url.TrimEnd('/').Equals(m.Url, StringComparison.OrdinalIgnoreCase));
        Settings.ManualServers.Add(m);
        Settings.Save();
        Registry.SetManualServers(Settings.ManualServers);
        _ = Registry.RefreshNowAsync();
    }

    public void RemoveManualServer(ServerWatcher s)
    {
        Settings.ManualServers.RemoveAll(m => Uri.TryCreate(m.Url, UriKind.Absolute, out var u) && NetAddr.Key(u.DnsSafeHost, u.Port) == s.Key);
        Settings.Save();
        Registry.RemoveManual(s.Key, Settings.ManualServers);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Csv.Flush();
        Recorder.Dispose();
        Proxies.Dispose();
        Registry.Dispose();   // speichert auch die Bibliothek
        if (_ownsHttp) _http.Dispose();
    }
}
