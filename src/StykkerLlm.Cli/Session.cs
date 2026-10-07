using StykkerLlm.Core;
using StykkerLlm.Platform.Windows;

namespace StykkerLlm.Cli;

public sealed class CliException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

// Engine für einen Befehl: echte Plattform oder Simulator, lesend oder schreibend.
// Lesende Befehle (status, list) laufen immer im Nur-Lesen-Modus und nehmen keine Sperre: so stören sie die GUI nie.
// Schreibende Befehle (start, stop, unload) brauchen den Datenordner für sich allein (wie eine zweite GUI-Instanz).
public enum SessionMode { Read, Write, Auto }

public sealed class Session : IDisposable
{
    public MonitorEngine Engine { get; }
    public LaunchCoordinator Launcher { get; }
    public ICliPrompt Prompt { get; }
    // Läuft der Server für diesen Datenordner, ist er die Quelle: der Zustand kommt von ihm, Aktionen gehen an ihn (S5)
    public ServerClient? Remote { get; private set; }
    public bool UsesServer => Remote?.State != null;
    public StateSnapshot? Server => Remote?.State;
    // Nichts schreiben (lesende Befehle, oder die App hat den Datenordner): start/stop/unload sind dann gesperrt
    public bool ReadOnly => Engine.ReadOnly;
    // Ohne Betriebssystem-Zugriff (noch keine Linux-Plattform): nur von Hand eingetragene Server
    public bool Limited { get; }
    public bool Simulated => _sim != null;
    private readonly int _intervalMs;
    private readonly SimHost? _sim;
    private readonly IPlatform? _platform;
    private SingleInstance? _single;
    // Die interaktive TUI hält den Server am Leben (ServerHolds), solange sie offen ist
    private Timer? _hold;
    private readonly string _holdId = "tui:" + Environment.ProcessId;

    public Session(CliArgs a, bool write) : this(a, write ? SessionMode.Write : SessionMode.Read) { }

    // Auto (interaktive Oberfläche): schreibend, wenn der Datenordner frei ist, sonst nur lesend neben der offenen App
    public Session(CliArgs a, SessionMode mode, ICliPrompt? prompt = null)
    {
        Prompt = prompt ?? new ConsolePrompt(a.Yes);
        bool write = mode != SessionMode.Read;
        _intervalMs = a.IntervalMs;
        if (a.Sim)
        {
            // eigener temporärer Datenordner, wird beim Beenden gelöscht; startet nie echte Programme
            _sim = new SimHost(SimServerSpec.Defaults(), a.Theme);
            _sim.World.Start();
            Engine = _sim.Engine;
        }
        else
        {
            var paths = a.DataDir != null ? new AppPaths(Path.GetFullPath(a.DataDir)) : AppPaths.Default();
            // Läuft der Server für diesen Datenordner, misst er und wir fragen ihn nur (nur eine Engine im System)
            if (!a.Local) Remote = ServerCommand.TryConnect(a);
            if (write)
            {
                _single = new SingleInstance(SingleInstance.NameFor(paths.Root));
                if (!_single.IsFirst)
                {
                    _single.Dispose();
                    _single = null;
                    if (mode == SessionMode.Auto) write = false;
                    else if (!UsesServer) throw new CliException(1, "StykkerLLM is already running with this data folder. Use the window for this, " +
                        "or close it first. (Read-only commands like 'status' and 'list' work alongside it.)");
                }
            }
            Directory.CreateDirectory(paths.Root);
            var settings = AppSettings.Load(paths.SettingsFile);
            if (OperatingSystem.IsWindows()) _platform = new WindowsPlatform();
            else { _platform = new BasicPlatform(); Limited = true; }
            // Mit Server keine eigene Engine zum Schreiben – nur lesend mitmessen, wenn ausdrücklich gewünscht (--local)
            Engine = new MonitorEngine(_platform, paths, settings, readOnly: !write || UsesServer);
            if (Remote != null && mode == SessionMode.Auto)
                _hold = new Timer(_ => { _ = Remote.HoldAsync(_holdId); }, null, 0, 5000);
        }
        Launcher = new LaunchCoordinator(Engine, Prompt);
    }

    // Einige Takte messen: der erste erkennt die Server, ab dem zweiten gibt es Token/s
    // pauseFirst: auch vor dem ersten Takt warten (fortlaufendes Messen: ein Takt je Intervall)
    public async Task TickAsync(int ticks, CancellationToken ct = default, bool pauseFirst = false)
    {
        // Läuft der Server, liefert er den Zustand; ein zweiter Messtakt daneben wäre doppelte Arbeit
        if (UsesServer)
        {
            for (int i = 0; i < ticks && !ct.IsCancellationRequested; i++)
            {
                if (i > 0 || pauseFirst) await Task.Delay(_intervalMs, ct).ConfigureAwait(false);
                await Remote!.GetStateAsync(ct).ConfigureAwait(false);
            }
            return;
        }
        for (int i = 0; i < ticks && !ct.IsCancellationRequested; i++)
        {
            if (i > 0 || pauseFirst) await Task.Delay(_intervalMs, ct).ConfigureAwait(false);
            await Engine.TickAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_sim != null) _sim.Dispose();
        else
        {
            try { Engine.Dispose(); } catch { }
            _platform?.Dispose();
        }
        if (_hold != null)
        {
            _hold.Dispose();
            try { Remote?.ReleaseAsync(_holdId).Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        }
        Remote?.Dispose();
        _single?.Dispose();
    }
}
