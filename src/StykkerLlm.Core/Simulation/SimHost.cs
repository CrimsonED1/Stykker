namespace StykkerLlm.Core;

// Ein Proxy, der nur auf dem Papier existiert: kein Socket, keine Weiterleitung. Er zählt und meldet, was die simulierten Server
// "durch ihn" erledigen, damit Proxy-Knopf, Aufnahme (Denken, Werkzeuge, Zeit bis zum ersten Token) wie im Betrieb zu sehen sind.
public sealed class SimRouterProxy : RouterProxy
{
    private bool _on;

    public SimRouterProxy(int listenPort) : base(listenPort) { }

    public override bool Running => _on;
    public override void Start() => _on = true;
    public override void Dispose() { _on = false; base.Dispose(); }
    public void Observe(ProxyRecord rec) { if (_on) Report(rec); }
}

// Alles, was der Simulator braucht, an einem Ort: Welt, Plattform, HTTP-Seite, eigener Datenordner und eine eigene MonitorEngine.
// Die Engine arbeitet mit einem temporären Datenbereich (Bibliothek, Aufnahmen, Logs, CSV), nie mit den Daten des Nutzers.
public sealed class SimHost : IDisposable
{
    private readonly Timer? _timer;
    private readonly bool _ownsDir;
    private bool _disposed;

    public SimWorld World { get; }
    public SimPlatform Platform { get; }
    public HttpClient Http { get; }
    public AppPaths Paths { get; }
    public AppSettings Settings { get; }
    public MonitorEngine Engine { get; }

    // dataDir null = neuer temporärer Ordner (wird beim Beenden gelöscht); sonst dieser Ordner (bleibt, z. B. für Bilder der Dokumentation)
    public SimHost(IEnumerable<SimServerSpec> specs, string? theme = null, string? dataDir = null, int? seed = null, bool autoStep = true)
    {
        _ownsDir = dataDir == null;
        string root = dataDir ?? Path.Combine(Path.GetTempPath(), "StykkerLLM-Simulation", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        Paths = new AppPaths(root);
        Settings = AppSettings.Load(Paths.SettingsFile);
        if (theme != null) Settings.Theme = theme;
        World = new SimWorld(seed) { LogDir = Paths.LogsDir };
        Platform = new SimPlatform(World);
        Http = new HttpClient(new SimHandler(World)) { Timeout = TimeSpan.FromSeconds(5) };
        foreach (var s in specs) World.Add(s);
        Engine = new MonitorEngine(Platform, Paths, Settings, Http, simulation: World,
            proxyFactory: port => new SimRouterProxy(port));
        Engine.BenchClient = () => new HttpClient(new SimHandler(World)) { Timeout = Timeout.InfiniteTimeSpan };
        World.Observed += (key, rec) =>
        {
            if (Engine.Proxies.Proxy is SimRouterProxy p) p.Observe(rec);
        };
        if (autoStep) _timer = new Timer(_ => { try { World.Step(DateTime.Now); } catch { } }, null, 250, 250);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        World.Stop();
        try { Engine.Dispose(); } catch { }
        try { Http.Dispose(); } catch { }
        if (_ownsDir)
        {
            try { Directory.Delete(Paths.Root, true); } catch { /* ein Virenscanner hält vielleicht noch eine Datei: bleibt im Temp-Ordner */ }
        }
    }
}
