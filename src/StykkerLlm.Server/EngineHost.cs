using StykkerLlm.Core;
using StykkerLlm.Core.Eval;
using StykkerLlm.Platform.Windows;

namespace StykkerLlm.Server;

// Hält die MonitorEngine des Servers und lässt sie im Takt messen. Seit S3/S4 ist der Server die Engine:
// er schreibt immer, Fenster, TUI und Telefon sind Clients. Den Mutex des Datenordners hält nur das Fenster.
public sealed class EngineHost : IDisposable
{
    private readonly AppPaths _paths;
    // Windows: die volle Umsetzung (TCP-Tabellen, Prozesse, PDH, NVML). Überall sonst der Core-Ersatz ohne
    // Betriebssystem-Zugriff – der Server startet damit auch unter Linux, misst dann aber nur die von Hand
    // eingetragenen Server. Eine echte Linux-Umsetzung ist noch offen (src/StykkerLlm.Core/LinuxPlatform.cs.wip).
    private readonly IPlatform _platform;
    private CancellationTokenSource? _cts;
    public MonitorEngine Engine { get; }
    public bool ReadOnly => Engine.ReadOnly;
    public event Action? Ticked;

    // Zugang, Schlüssel und Aktionen (S2/S3); der Schlüssel wird beim ersten Start erzeugt und an den Benutzer gebunden
    public AccessControl Access { get; }
    public string Key { get; }
    public BenchmarkService Benchmarks { get; } = new();
    // Die Modelltests des Servers gehören in den Zustand: Fenster, TUI und Telefon lesen sonst eine leere Warteschlange
    public EvalQueue? Queue { get; set; }
    // Die gekoppelten Nodes (dieser Server als Hub, docs/nodes.md)
    public NodeRegistry Nodes { get; }

    public EngineHost(AppPaths paths, IPlatform? platform = null)
    {
        _paths = paths;
        _platform = platform ?? CreatePlatform();
        // Bewusst kein SingleInstance: den Mutex des Datenordners hält nur das Fenster (nur ein Fenster je Ordner).
        // Der Server dürfte ihn nie nehmen – sonst blockierte ein zuerst gestarteter Server das Fenster, das sich
        // lautlos beendete, und umgekehrt lief ein vom Fenster gestarteter Server nur lesend (S4: Server = Engine).
        // Ein zweiter Serverprozess wird schon in Program.cs über einen eigenen Mutex abgewiesen.
        var settings = AppSettings.Load(paths.SettingsFile);
        Engine = new MonitorEngine(_platform, paths, settings, readOnly: false);
        Engine.Notice += text => StateJson.Notice(text);
        Engine.ServerLost += lost =>
        {
            LastLost = lost;
            StateJson.Notice(Strings.LostBalloon(lost.Name));
            TryRestartAfterCrash(lost);
        };
        Access = new AccessControl(paths, _platform);
        Nodes = new NodeRegistry(paths, _platform);
        // N4: Modelle der Nodes über den eigenen Proxy anbieten ("Node/Modell"), wenn ihr Proxy im Netz erreichbar ist
        Engine.Proxies.NodeRemotes = () => NodeStateJson.FromRegistry(Nodes).List
            .Where(n => n.Online && n.ProxyUrl.Length > 0)
            .Select(n => new RemoteStykker { Name = n.Name, Url = n.ProxyUrl });
        Key = ServerClient.ReadKey(paths, _platform) ?? ServerClient.WriteKey(paths, _platform);
        Engine.IdleUnloadDue += OnIdleUnload;
    }

    // Zuletzt verschwundene Server ( Absturz, von außen beendet) für die Oberflächen
    public ServerLost? LastLost { get; private set; }

    // Die Plattform des Servers. Auf Windows die volle Umsetzung, sonst der Core-Ersatz ohne Systemzugriff
    // (das ist der Weg, auf dem der Server unter Linux läuft). „--platform basic“ erzwingt den Ersatz auch unter
    // Windows – damit lässt sich der eingeschränkte Weg prüfen, ohne ein Linux zu brauchen.
    public static IPlatform CreatePlatform(string? name = null)
    {
        name ??= Environment.GetEnvironmentVariable("STYKKER_PLATFORM");
        return !OperatingSystem.IsWindows() || string.Equals(name, "basic", StringComparison.OrdinalIgnoreCase)
            ? new BasicPlatform()
            : new WindowsPlatform();
    }

    // Ein Server ist so lange still, dass sein Profil ihn jetzt entladen möchte
    private async void OnIdleUnload(ServerWatcher watcher, Profile profile)
    {
        if (UnloadServer == null) return;
        try
        {
            bool ok = await UnloadServer(watcher).ConfigureAwait(false);
            StateJson.Notice(ok ? Strings.IdleUnloadDone(profile.Name)
                : Strings.IdleUnloadQueued(profile.Name, IdleUnload.MinutesOf(profile)));
        }
        catch { /* beim nächsten Takt noch einmal */ }
    }

    // Automatischer Neustart nach einem Absturz. Hier im Server, weil er Engine und Startkoordinator
    // beisitzt; die Regel selbst liegt im Core (`CrashRestartPolicy`) und ist dort ohne Fenster prüfbar.
    public CrashRestartPolicy CrashRestart { get; } = new();
    public Func<Profile, Task<bool>>? StartProfile { get; set; }   // wird von Program.cs gesetzt

    // Leerlauf-Entladen: die Engine meldet, der Server entlädt (llama.cpp: stoppen,
    // Ollama/LM Studio: Modell entladen) – er hat den Startkoordinator.
    public Func<ServerWatcher, Task<bool>>? UnloadServer { get; set; }

    private void TryRestartAfterCrash(ServerLost lost)
    {
        var profile = lost.Key.Length == 0 ? null : Engine.Library.Profiles.FirstOrDefault(p => p.Key == lost.Key);
        if (profile is not { RestartOnCrash: true }) return;                    // Standard ist: kein Neustart
        if (!CrashRestart.Allows(lost.Key, profile, DateTimeOffset.Now))        // Grenze erreicht
        {
            StateJson.Notice(Strings.RestartGaveUp(profile.Name, CrashRestart.MaxRestartsClamped));
            return;
        }
        int attempt = CrashRestart.Count(lost.Key, DateTimeOffset.Now).Used;
        int delay = CrashRestart.DelaySecondsClamped;
        _ = Task.Run(async () =>
        {
            if (delay > 0) await Task.Delay(delay * 1000);
            bool ok = false;
            try { ok = StartProfile?.Invoke(profile).GetAwaiter().GetResult() ?? false; }
            catch { }
            StateJson.Notice(ok ? Strings.RestartDone(profile.Name, attempt) : Strings.RestartFailed(profile.Name));
        });
    }

    // withCode = false für ein Gerät mit der Rolle Viewer: ohne Zugangscode kann es sich nicht als Admin anmelden.
    public string StateJsonText(int port, bool withHistory = true, bool withCode = true) =>
        StateJson.WriteText(Engine, Access, Queue, port, DateTimeOffset.Now, withHistory, withCode, Nodes);

    public void ClearLost() => LastLost = null;

    public void Start()
    {
        // Read-only ist ein Zustand, den der Nutzer nicht sehen kann, aber spürt (jede Aktion wird abgewiesen).
        // Deshalb steht er als anhaltender Hinweis in Fenster, Web, Telefon und TUI.
        if (Engine.ReadOnly) StateJson.Notice(Strings.ServerReadOnlyHint);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Nodes.Start();
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Engine.TickAsync(); Ticked?.Invoke(); } catch { }
                Access.Maintain();          // abgelaufenen Code ersetzen, alte Kopplungsanfragen aufräumen
                SyncNodeResults();
                Access.WriteThrottled();
                try { await Task.Delay(Math.Max(500, Engine.Settings.IntervalMs), ct); } catch { break; }
            }
        });
    }

    // Testergebnisse der Nodes höchstens einmal je Minute einsammeln (N3), nie zweimal gleichzeitig
    private DateTime _nextResultSync = DateTime.MinValue;
    private int _syncing;

    private void SyncNodeResults()
    {
        if (Nodes.Nodes.Count == 0 || DateTime.Now < _nextResultSync || Interlocked.Exchange(ref _syncing, 1) == 1) return;
        _nextResultSync = DateTime.Now.AddMinutes(1);
        _ = Task.Run(async () =>
        {
            try { await Nodes.SyncResultsAsync(_paths.EvalResultsDir); }
            finally { Interlocked.Exchange(ref _syncing, 0); }
        });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        Access.Write();
        Nodes.Dispose();
        try { Engine.Dispose(); } catch { }
        _platform.Dispose();
    }
}

