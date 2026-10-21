using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using StykkerStarter.Core;
using StykkerStarter.Core.Config;
using StykkerStarter.Core.Copy;
using StykkerStarter.Core.Crash;
using StykkerStarter.Core.Processes;
using StykkerStarter.Core.Ready;
using StykkerStarter.Core.Source;
using StykkerStarter.Core.Start;
using StykkerStarter.Core.Update;
using StykkerStarter.Core.Versions;
using StykkerStarter.Core.Web;
using StykkerStarter.Platform.Windows;

namespace StykkerStarter.App;

// Der Zustand des Starters und alle Vorgänge. Alles läuft nacheinander auf einem eigenen Thread (einer Warteschlange),
// damit Kopieren, Umschalten, Prüfen und Einstellungen einander nicht in die Quere kommen. Die Oberfläche bekommt
// nur fertige JSON-Zustände; der Tray bekommt eine Momentaufnahme der Programme. Befehle, die nicht warten dürfen
// (Startfolge abbrechen, Vorgang als „wartet“ melden), gehen an Felder und den letzten Zustand, nicht durch die Schlange.
internal sealed class Model : IDisposable
{
    private const int CloseGraceMs = 10_000;                       // die Frist des höflichen Schließens
    private static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadyEvery = TimeSpan.FromSeconds(1);

    private readonly DataLayout _layout;
    private readonly StarterLog _log;
    private readonly bool _defaultData;
    private readonly int? _checkEverySeconds;
    private readonly string _exe;
    private readonly Action<string> _sink;               // JSON an die Seite
    private readonly Action _trayChanged;                // Tooltip und Menü neu
    private readonly Action<string, string> _announce;   // Sprechblase am Tray: (Titel, Text)
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly WindowsProcessSystem _processes = new();
    private readonly FileVersionReader _versions = new();
    private readonly NetworkReadyProbe _probe = new();
    private readonly ProgramService _service;
    private readonly StarterEngine _engine;
    private readonly CrashWatch _crash = new();
    private readonly ReadyTracker _ready = new();
    private readonly Dictionary<string, CheckResult> _checks = new();
    private readonly HashSet<string> _announced = new();
    private readonly Dictionary<string, long> _sizes = new();
    private readonly ConcurrentDictionary<string, BusyState> _busy = new();   // laufende und wartende Vorgänge je Programm

    private StarterConfig _config = new();
    private CancellationTokenSource? _cancel;
    private CopyView? _copy;
    private OpView? _last;
    private int _opSeq;        // zählt die abgeschlossenen Vorgänge; die Seite erkennt daran das Ende
    private string? _notice;
    private DateTime? _lastChecked;
    private DateTime _nextCheck;
    private System.Threading.Timer? _timer;
    private System.Threading.Timer? _startupTimer;
    private System.Threading.Timer? _fallbackTimer;
    private System.Threading.Timer? _watchTimer;
    private System.Threading.Timer? _readyTimer;
    private int _startupScheduled;
    private int _watchQueued;
    private int _readyQueued;
    private bool _startupDone;                 // nur auf dem Thread der Warteschlange
    private DateTime? _startupAt;              // Ende der laufenden Pause (UTC), nur auf dem Thread der Warteschlange
    private Queue<ProgramEntry>? _startupQueue;   // die restliche Startfolge (nur Thread der Warteschlange)
    private int _startupStep;                      // macht alte Timer-Schritte wirkungslos
    private string? _waitedFor;                    // die Pause vor diesem Programm ist schon abgelaufen
    private volatile string? _startupNext;     // Name des nächsten Programms in der Startfolge
    private volatile bool _startNow;           // „Start now“ oder „Start all“: die Pausen entfallen
    private volatile bool _skipStartup;        // „Skip“: die Startfolge entfällt
    private volatile bool _stopping;
    private DateTime _copyPublished = DateTime.MinValue;

    private volatile ProgramView[] _programs = Array.Empty<ProgramView>();
    private volatile PageState? _state;        // der letzte fertige Zustand (unveränderlich), für Befehle aus dem Fenster

    public Model(DataLayout layout, StarterLog log, bool defaultData, int? checkEverySeconds, string exe,
        Action<string> sink, Action trayChanged, Action<string, string> announce)
    {
        _layout = layout;
        _log = log;
        _defaultData = defaultData;
        _checkEverySeconds = checkEverySeconds;
        _exe = exe;
        _sink = sink;
        _trayChanged = trayChanged;
        _announce = announce;
        _service = new ProgramService(layout, _processes, new WindowsProgramLauncher(), log);
        _engine = new StarterEngine(layout, _versions, _service, log);
        _thread = new Thread(Loop) { IsBackground = true, Name = "Stykker Starter worker" };
    }

    // Die Programme für den Tray (Momentaufnahme, nur gelesen)
    public IReadOnlyList<ProgramView> Programs => _programs;

    public void Start()
    {
        _thread.Start();
        Enqueue(() =>
        {
            _config = LoadConfig();
            _log.Write($"start: starter {Version()}, data folder {_layout.Root}, starter.json {_layout.ConfigFile}");
            _log.Write($"config: {_config.Programs.Count} program(s)");
            foreach (var p in _config.Programs)
            {
                var broken = ProgramCheck.BrokenReason(p);
                _log.Write($"config: program {p.Id}: name \"{p.Name}\", source \"{p.SourceExe}\", copyToLocal {p.CopyToLocal}" +
                           (broken == null ? "" : $", BROKEN: {broken}"));
            }
            Reschedule();
            _watchTimer = new System.Threading.Timer(_ => WatchTick(), null, WatchEvery, WatchEvery);
            _readyTimer = new System.Threading.Timer(_ => ReadyTick(), null, ReadyEvery, ReadyEvery);
            Publish();
        });
    }

    // Erst das Fenster, dann die Programme: die Seite meldet sich (oder der Tray ist allein), und nach der Pause aus den
    // Einstellungen starten die Programme und die erste Prüfung. Läuft nur einmal, auch wenn mehrere Auslöser kommen.
    public void BeginStartup()
    {
        if (Interlocked.Exchange(ref _startupScheduled, 1) != 0) return;
        Enqueue(() =>
        {
            if (_startupDone) return;
            var delay = TimeSpan.FromSeconds(_config.Settings.StartupDelaySeconds);
            _log.Write($"start: programs start in {delay.TotalSeconds:0} s");
            _startupNext = ProgramList.Startable(_config).FirstOrDefault(WillStartAtStartup)?.Name;
            _startupAt = DateTime.UtcNow + delay;   // die Seite zählt davon herunter
            _startupTimer = new System.Threading.Timer(_ => Enqueue(RunStartup), null, delay, Timeout.InfiniteTimeSpan);
            Publish();
        });
    }

    // Falls die Seite nie fertig wird (WebView2 fehlt): die Programme starten trotzdem
    public void BeginStartupFallback(TimeSpan after)
    {
        _fallbackTimer = new System.Threading.Timer(_ => BeginStartup(), null, after, Timeout.InfiniteTimeSpan);
    }

    // Die Startfolge: nach der Pause vor dem Start des Ganzen geht es in der Reihenfolge von starter.json; vor jedem
    // Programm die eigene Pause. Die Pausen laufen über einen Timer, nicht auf dem Thread der Warteschlange: Stop,
    // Update usw. kommen auch während einer Pause sofort dran.
    private void RunStartup()
    {
        if (_startupDone || _stopping) return;
        _startupDone = true;
        _startupTimer?.Dispose();
        if (_skipStartup)
        {
            _log.Write("start: programs not started (skipped)");
            _notice = "Programs not started";
            FinishStartup();
            return;
        }
        _log.Write(_startNow ? "start: programs started at once (start now)" : "start: programs start in order");
        _startupQueue = new Queue<ProgramEntry>(ProgramList.Startable(_config));
        StartupStep();
    }

    // Startet Programme, bis eines vor sich eine Pause hat; dann kommt der nächste Schritt über den Timer
    private void StartupStep()
    {
        _startupTimer?.Dispose();
        _startupStep++;
        while (_startupQueue is { Count: > 0 } queue)
        {
            if (_stopping || _skipStartup) { queue.Clear(); break; }
            var program = queue.Peek();
            if (!WillStartAtStartup(program)) { queue.Dequeue(); continue; }
            if (program.StartDelaySeconds > 0 && !_startNow && _waitedFor != program.Id)
            {
                _waitedFor = program.Id;
                _startupNext = Label(program);
                var delay = TimeSpan.FromSeconds(program.StartDelaySeconds);
                _startupAt = DateTime.UtcNow + delay;
                var step = _startupStep;
                _startupTimer = new System.Threading.Timer(_ => Enqueue(() => { if (step == _startupStep) StartupStep(); }),
                    null, delay, Timeout.InfiniteTimeSpan);
                Publish();
                return;
            }
            queue.Dequeue();
            _startupAt = null;
            StartAtStartup(program);
            Publish();
        }
        if (_skipStartup) _notice = "Programs not started";
        FinishStartup();
    }

    private void FinishStartup()
    {
        _startupQueue = null;
        _startupAt = null;
        _startupNext = null;
        SaveConfig();
        CheckAll();
    }

    // „Start now“ auf der Seite: nicht auf die Pausen warten
    public void StartupNow()
    {
        _startNow = true;
        Enqueue(() => { if (_startupQueue != null) StartupStep(); else RunStartup(); });
    }

    // „Skip“ auf der Seite: die Startfolge entfällt diesmal (auch mitten in der Folge)
    public void SkipStartup()
    {
        _skipStartup = true;
        Enqueue(() => { if (_startupQueue != null) StartupStep(); else RunStartup(); });
    }

    // Ein Programm, das die Startfolge startet: ohne Kopie, wenn es nicht läuft; mit Kopie, wenn es nicht läuft und mit
    // dem Starter startet, oder wenn es noch keine Kopie hat (dann wird kopiert und gestartet)
    private bool WillStartAtStartup(ProgramEntry program)
    {
        if (!program.CopyToLocal) return program.StartWithStarter && !_service.IsRunning(program);
        return program.Current == null || (program.StartWithStarter && _service.RunningProcesses(program).Count == 0);
    }

    private void StartAtStartup(ProgramEntry program)
    {
        try
        {
            if (program.CopyToLocal && program.Current == null)
            {
                var outcome = InstallNow(program, manual: false);
                _log.Write($"install {program.Id} at start: {outcome.Kind}, {outcome.Message}");
                if (program.Copies.Count == 0) _config.Programs.Remove(program);
            }
            else
            {
                var started = StartNow(program, manual: false);
                _log.Write($"start {program.Id} at start: {(started.Ok ? "ok" : "failed")}, {started.Message}");
            }
        }
        catch (Exception ex) when (ex is SourceUnreachableException or InvalidOperationException or IOException)
        {
            _log.Write($"install {program.Id} at start: {ex.Message}");
        }
    }

    // Beenden mit „die Programme auch stoppen“: jedes laufende Programm wird gestoppt (höflich, nach 10 s hart),
    // danach ruft der Starter done. Neue Starts und Prüfungen gibt es ab jetzt nicht mehr.
    public void StopAllThen(Action done)
    {
        StopEngine();
        Enqueue(() =>
        {
            _startupDone = true;
            _startupAt = null;
            _notice = "Stopping the programs…";
            Publish();
            foreach (var program in _config.Programs.ToList())
            {
                if (ProgramCheck.BrokenReason(program) != null || !_service.IsRunning(program)) continue;
                NoteStopped(program);
                var report = _service.Stop(program);
                _log.Write($"quit: stop {program.Id}: {report.Closed.Count} closed, {report.Killed.Count} ended, {report.Left.Count} left");
            }
            done();
        });
    }

    // Was das Schließen des Fensters macht: "ask", "tray" oder "quit" (Einstellung, auch aus dem Dialog gemerkt)
    public string CloseAction => _config.Settings.CloseAction;

    public void RememberCloseAction(string action) => Enqueue(() =>
    {
        _config.Settings.CloseAction = action;
        SaveConfig();
        Publish();
    });

    public void Enqueue(Action action)
    {
        try { _queue.Add(action); }
        catch (InvalidOperationException) { }   // schon beendet
    }

    // Für den Tray (der Thread des Menüs): dieselben Vorgänge wie aus der Seite
    public void RequestUpdate(string id) { Queue(id); Enqueue(() => UpdateProgram(id)); }
    public void RequestStart(string id) { Queue(id); Enqueue(() => StartProgram(id)); }
    public void RequestStop(string id) { Queue(id); Enqueue(() => StopProgram(id)); }
    public void RequestRestart(string id) { Queue(id); Enqueue(() => RestartProgram(id)); }

    public void RequestCheck() => Enqueue(CheckAll);

    private void Loop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex)
            {
                _log.Write($"error: {ex.GetType().Name}: {ex.Message}");
                _notice = "Something went wrong: " + ex.Message;
                Publish();
            }
        }
    }

    // Beim Beenden: die Timer hören auf, eine laufende Kopie oder Prüfung wird abgebrochen, und es kommt keine neue dazu
    public void StopEngine()
    {
        _stopping = true;
        _timer?.Dispose();
        _timer = null;
        _startupTimer?.Dispose();
        _fallbackTimer?.Dispose();
        _watchTimer?.Dispose();
        _readyTimer?.Dispose();
        _cancel?.Cancel();
    }

    public void Dispose()
    {
        StopEngine();
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    // Befehle aus der Seite (JSON). Läuft auf dem Fensterthread; die Arbeit geht in die Warteschlange.
    public void Command(JsonElement message)
    {
        var cmd = Str(message, "cmd");
        var id = Str(message, "id");
        switch (cmd)
        {
            case "ready": Enqueue(Publish); break;
            case "check": Enqueue(CheckAll); break;
            case "startNow": StartupNow(); break;
            case "skipStartup": SkipStartup(); break;
            case "startAll": Enqueue(StartAll); break;
            case "stopAll": Enqueue(StopAll); break;
            case "update": Queue(id); Enqueue(() => UpdateProgram(id)); break;
            case "start": Queue(id); Enqueue(() => StartProgram(id)); break;
            case "stop": Queue(id); Enqueue(() => StopProgram(id)); break;
            case "restart": Queue(id); Enqueue(() => RestartProgram(id)); break;
            case "rollback": Queue(id); Enqueue(() => RollbackProgram(id)); break;
            case "remove": Enqueue(() => RemoveProgram(id)); break;
            case "move":
            {
                var index = Int(message, "index", 0);
                Enqueue(() => MoveProgram(id, index));
                break;
            }
            case "edit": Enqueue(() => EditProgram(message)); break;
            case "cleanup": Enqueue(CleanupAll); break;
            case "cancel": _cancel?.Cancel(); break;
            case "inspect":
            {
                var path = Str(message, "path");
                var args = Str(message, "args");
                Enqueue(() => Inspect(path, args));
                break;
            }
            case "add":
                Enqueue(() => AddProgram(new NewProgram(
                    Str(message, "path"), Str(message, "name"), Str(message, "args"),
                    Bool(message, "startWithStarter", true), Bool(message, "updateWithoutAsking", false), Bool(message, "keepPrevious", true),
                    Bool(message, "copyToLocal", true), Bool(message, "restartOnCrash", false),
                    Int(message, "startDelaySeconds", 0), Str(message, "readyCheck"))));
                break;
            case "settings":
                Enqueue(() => SaveSettings(message));
                break;
        }
    }

    // ---------- Vorgänge (laufen auf dem Thread der Warteschlange) ----------

    private void CheckAll()
    {
        if (_stopping) return;
        _lastChecked = DateTime.Now;
        bool changed = false;
        foreach (var program in _config.Programs.ToList())
        {
            if (ProgramCheck.BrokenReason(program) != null) continue;
            var result = _engine.Check(program, _config.Settings);
            _checks[program.Id] = result;
            changed |= result.Kind == CheckKind.Updated;   // ohne Kopie: der neue Stand steht in der Datei
            if (result.Kind == CheckKind.Updated && program.CopyToLocal) _service.Cleanup(program, _config.Settings.VersionsKept);
            if (result.Kind == CheckKind.UpdateAvailable && result.SourceVersion is { } v && _announced.Add($"{program.Id}:{v}"))
                _announce("Update available", $"{program.Name}: {result.Message}. Open the Starter to update.");
        }
        if (changed) SaveConfig();
        Reschedule();
        Publish();
    }

    private void UpdateProgram(string id) => Operate(id, "updating", stops: true, program =>
    {
        var cts = new CancellationTokenSource();
        _cancel = cts;
        CheckResult result;
        try
        {
            result = _engine.Check(program, _config.Settings, new CopyProgressSink(p => ReportCopy(program.Name, p)), cts.Token, forceUpdate: true);
        }
        finally
        {
            _cancel = null;
            _copy = null;
        }
        _checks[program.Id] = result;
        if (result.Kind == CheckKind.Updated && program.CopyToLocal) _service.Cleanup(program, _config.Settings.VersionsKept);
        _last = OpView.FromCheck($"Update {program.Name}", result);
        _opSeq++;
        _notice = result.Message;
        SaveConfig();
    });

    private void StartProgram(string id) => Operate(id, "starting", stops: false, program =>
    {
        var (ok, message) = StartOrInstall(program, manual: true);
        SaveConfig();
        _notice = ok ? $"{program.Name} is running" : $"{program.Name} did not start: {message}";
    });

    private void StopProgram(string id) => Operate(id, "stopping", stops: true, program =>
    {
        var report = _service.Stop(program);
        int count = report.Closed.Count + report.Killed.Count;
        _notice = report.AllGone
            ? $"{program.Name} stopped{(count > 0 ? $" ({count} process{(count == 1 ? "" : "es")})" : "")}"
            : $"{program.Name}: {report.Left.Count} process(es) still running";
    });

    private void RestartProgram(string id) => Operate(id, "restarting", stops: true, program =>
    {
        var report = _service.Stop(program);
        var outcome = report.AllGone ? StartNow(program, manual: true) : new StartOutcome(false, null, "the old version did not stop");
        SaveConfig();
        _notice = outcome.Ok ? $"{program.Name} restarted" : $"{program.Name} did not restart: {outcome.Message}";
    });

    private void RollbackProgram(string id) => Operate(id, "rollback", stops: true, program =>
    {
        if (!program.CopyToLocal)
        {
            _notice = $"{program.Name} runs from its source folder; there is no earlier copy";
            return;
        }
        var outcome = _service.Rollback(program);
        if (outcome.Kind == UpdateKind.WentBack) NoteStarted(program, manual: true);
        _last = OpView.FromRollback(program.Name, outcome);
        _opSeq++;
        _notice = outcome.Kind == UpdateKind.WentBack ? $"{program.Name}: {outcome.Message}" : outcome.Message;
        SaveConfig();
    });

    // Ein Vorgang für ein Programm: „Busy“ setzen, ausführen, danach löschen und neu melden. stops: der Vorgang kann
    // Prozesse beenden; dann gilt das Programm vorher als gewollt gestoppt (kein Absturz), und läuft es danach wieder,
    // erwartet der Starter es wieder.
    private void Operate(string id, string kind, bool stops, Action<ProgramEntry> body)
    {
        var program = Runnable(id);
        if (program == null)
        {
            _busy.TryRemove(id, out _);
            Publish();
            return;
        }
        bool grace = stops && _service.IsRunning(program);
        _busy[id] = new BusyState(kind, NowMs(), grace ? CloseGraceMs : null);
        Publish();
        if (stops) NoteStopped(program);
        try
        {
            body(program);
        }
        finally
        {
            if (stops && _service.IsRunning(program)) NoteStarted(program, manual: false);
            _busy.TryRemove(id, out _);
            Publish();
        }
    }

    // „Wartet“ sofort anzeigen: der Befehl steht in der Warteschlange. Läuft aus dem Fensterthread, daher nur der letzte
    // fertige Zustand, mit dem Vorgang darüber gelegt.
    private void Queue(string id)
    {
        _busy[id] = new BusyState("queued", NowMs(), null);
        if (_state is { } state) Emit(state with { Programs = Overlay(state.Programs) });
    }

    // Startet das Programm von Hand: fehlt die Kopie, wird erst kopiert (und gestartet); sonst startet es
    private (bool Ok, string Message) StartOrInstall(ProgramEntry program, bool manual)
    {
        if (program.CopyToLocal && program.Current == null)
        {
            var outcome = InstallNow(program, manual);
            return (outcome.Kind == UpdateKind.Updated, outcome.Message);
        }
        var started = StartNow(program, manual);
        return (started.Ok, started.Message);
    }

    // Startet die laufende Kopie; ohne Kopie die Quelle (dabei merkt sich der Starter den Stand, mit dem es startet).
    // manual: von Hand gestartet (das setzt die Absturz-Zähler zurück); sonst vom Starter selbst (Start, Neustart).
    private StartOutcome StartNow(ProgramEntry program, bool manual)
    {
        var outcome = program.CopyToLocal ? _service.StartCurrent(program) : _engine.StartInPlace(program);
        if (!program.CopyToLocal) SaveConfig();
        if (outcome.Ok) NoteStarted(program, manual);
        return outcome;
    }

    // Erste Kopie und Start (oder nur die Kopie, wenn das Programm dann nicht startet). Setzt „kopiert“ als Vorgang.
    private UpdateOutcome InstallNow(ProgramEntry program, bool manual)
    {
        var cts = new CancellationTokenSource();
        _cancel = cts;
        _busy[program.Id] = new BusyState("copying", NowMs(), null);
        UpdateOutcome outcome;
        try
        {
            outcome = _engine.Install(program, new CopyProgressSink(p => ReportCopy(program.Name, p)), cts.Token);
        }
        catch (SourceUnreachableException ex)
        {
            outcome = new UpdateOutcome(UpdateKind.Failed, "the program cannot be read: " + ex.Message, Array.Empty<int>(), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException)
        {
            outcome = new UpdateOutcome(UpdateKind.Failed, ex.Message, Array.Empty<int>(), null);
        }
        finally
        {
            _cancel = null;
            _copy = null;
            _busy.TryRemove(program.Id, out _);
        }
        if (outcome.Kind == UpdateKind.Updated) NoteStarted(program, manual);
        return outcome;
    }

    // Nur kopieren, ohne zu starten (Bearbeiten: Kopie einschalten, das Programm läuft nicht)
    private UpdateOutcome CopyNow(ProgramEntry program)
    {
        var cts = new CancellationTokenSource();
        _cancel = cts;
        try
        {
            return _engine.CopyOnly(program, new CopyProgressSink(p => ReportCopy(program.Name, p)), cts.Token);
        }
        catch (SourceUnreachableException ex)
        {
            return new UpdateOutcome(UpdateKind.Failed, "the program cannot be read: " + ex.Message, Array.Empty<int>(), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException)
        {
            return new UpdateOutcome(UpdateKind.Failed, ex.Message, Array.Empty<int>(), null);
        }
        finally
        {
            _cancel = null;
            _copy = null;
        }
    }

    // Der Starter weiß: das Programm läuft (oder sollte laufen). Absturz-Beobachtung und Bereit-Prüfung folgen dem.
    private void NoteStarted(ProgramEntry program, bool manual)
    {
        _crash.Started(program.Id, manual);
        if (ReadyRule.Parse(program.ReadyCheck) != null) _ready.Started(program.Id, DateTime.UtcNow);
    }

    // Gewollt gestoppt (Stop, Stop all, Update, Beenden): kein Absturz, und die Bereit-Prüfung endet
    private void NoteStopped(ProgramEntry program)
    {
        _crash.Intend(program.Id);
        _ready.Stopped(program.Id);
    }

    private void StartAll()
    {
        int started = 0;
        foreach (var id in ProgramList.Startable(_config).Select(p => p.Id).ToList())
        {
            var program = Find(id);
            if (program == null || _service.IsRunning(program)) continue;
            Operate(id, "starting", stops: false, p =>
            {
                var (ok, _) = StartOrInstall(p, manual: true);
                if (ok) started++;
            });
        }
        SaveConfig();
        _notice = started == 0 ? "Nothing to start" : $"Started {started} program(s)";
        Publish();
    }

    private void StopAll()
    {
        int stopped = 0;
        foreach (var id in ProgramList.Startable(_config).Select(p => p.Id).ToList())
        {
            var program = Find(id);
            if (program == null || !_service.IsRunning(program)) continue;
            Operate(id, "stopping", stops: true, p =>
            {
                if (_service.Stop(p).AllGone) stopped++;
            });
        }
        _notice = stopped == 0 ? "No program was running" : $"Stopped {stopped} program(s)";
        Publish();
    }

    private void MoveProgram(string id, int index)
    {
        if (!ProgramList.Move(_config, id, index)) return;
        SaveConfig();
        Publish();
    }

    // Bearbeiten: die Regeln (Problem), dann übernehmen. Die Programmdatei bleibt; ein neuer Name oder neue Argumente
    // gelten beim nächsten Start. Kopie an oder aus: läuft das Programm, wird nicht umgeschaltet.
    private void EditProgram(JsonElement m)
    {
        var id = Str(m, "id");
        var program = Runnable(id);
        if (program == null) return;
        var changes = new ProgramChanges(
            Name: Str(m, "name"),
            Arguments: Str(m, "args"),
            StartWithStarter: Bool(m, "startWithStarter", program.StartWithStarter),
            UpdateWithoutAsking: Bool(m, "updateWithoutAsking", program.UpdateWithoutAsking),
            KeepPrevious: Bool(m, "keepPrevious", program.KeepPrevious),
            CopyToLocal: Bool(m, "copyToLocal", program.CopyToLocal),
            RestartOnCrash: Bool(m, "restartOnCrash", program.RestartOnCrash),
            StartDelaySeconds: Int(m, "startDelaySeconds", program.StartDelaySeconds),
            ReadyCheck: Str(m, "readyCheck"));

        if (ProgramEdit.Problem(_config, program, changes) is { } problem)
        {
            _notice = problem;
            Publish();
            return;
        }
        bool copyChanged = changes.CopyToLocal != program.CopyToLocal;
        bool readyChanged = (changes.ReadyCheck ?? "").Trim() != program.ReadyCheck.Trim();
        if (copyChanged && _service.IsRunning(program))
        {
            _notice = $"Stop {Label(program)} before changing its copy setting";
            Publish();
            return;
        }

        ProgramEdit.Apply(program, changes);
        SaveConfig();
        string message = $"{Label(program)} saved";
        if (copyChanged && program.CopyToLocal)
        {
            _busy[id] = new BusyState("copying", NowMs(), null);
            Publish();
            try { message = CopyNow(program).Message; }
            finally { _busy.TryRemove(id, out _); }
            SaveConfig();
        }
        if (readyChanged && _service.IsRunning(program)) NoteStarted(program, manual: false);
        _notice = message;
        Publish();
    }

    private void RemoveProgram(string id)
    {
        var program = Find(id);
        if (program == null) return;
        if (_service.IsRunning(program))
        {
            _notice = $"Stop {Label(program)} before removing it";
            Publish();
            return;
        }
        // Eine Id, die kein einfacher Ordnername ist, trifft nie einen Ordner (ein kaputter Eintrag hat keinen)
        if (_layout.TryProgramDir(program.Id) is { } dir) VersionCopier.DeleteTree(dir);
        _config.Programs.Remove(program);
        _checks.Remove(program.Id);
        _ready.Stopped(program.Id);
        SaveConfig();
        _notice = $"{Label(program)} removed; its local copies are deleted";
        Publish();
    }

    private void CleanupAll()
    {
        int removed = 0;
        foreach (var program in _config.Programs.Where(p => ProgramCheck.BrokenReason(p) == null))
            removed += _service.Cleanup(program, _config.Settings.VersionsKept).Removed.Count;
        SaveConfig();
        _notice = removed == 0 ? "Nothing to remove" : $"Removed {removed} unused {(removed == 1 ? "copy" : "copies")}";
        Publish();
    }

    private void Inspect(string path, string args)
    {
        if (!IsProgramPath(path))
        {
            Reply(new { type = "error", text = "Choose a .exe file" });
            return;
        }
        try
        {
            var tree = SourceTree.Read(path);
            var version = _versions.Read(path);
            var duplicate = ProgramCheck.FindDuplicate(_config, path, args);
            Reply(new
            {
                type = "found",
                path,
                name = Path.GetFileNameWithoutExtension(path),
                version = version ?? "no file version (the content decides)",
                files = tree.Files.Count,
                bytes = tree.TotalBytes,
                largest = tree.Files.OrderByDescending(f => f.Size).Select(f => f.RelativePath).FirstOrDefault() ?? "",
                copyTo = Path.Combine(_layout.Root, "<id>", version ?? "<hash>"),
                sourceFolder = tree.Folder,
                builtAt = tree.BuiltAtText,
                duplicateOf = duplicate?.Name,
            });
        }
        catch (SourceUnreachableException ex)
        {
            Reply(new { type = "error", text = "The program cannot be read: " + ex.Message });
        }
    }

    private void AddProgram(NewProgram input)
    {
        _log.Write($"add: received path \"{input.Path}\", name \"{input.Name}\", copyToLocal {input.CopyToLocal}");
        if (!IsProgramPath(input.Path) || !File.Exists(input.Path))
        {
            _log.Write("add: rejected, not an existing .exe file");
            _notice = "Choose an existing .exe file";
            Publish();
            return;
        }
        if (ProgramCheck.FindDuplicate(_config, input.Path, input.Args) is { } duplicate)
        {
            _log.Write($"add: rejected, already in the list as {duplicate.Id}");
            _notice = $"{duplicate.Name} is already in the list";
            Publish();
            return;
        }
        if (ReadyRule.Problem(input.ReadyCheck) is { } readyProblem)
        {
            _notice = "The ready check: " + readyProblem;
            Publish();
            return;
        }
        if (input.StartDelaySeconds is < 0 or > ProgramEdit.MaxStartDelaySeconds)
        {
            _notice = "The start delay must be 0 to 120 seconds";
            Publish();
            return;
        }

        var name = string.IsNullOrWhiteSpace(input.Name) ? Path.GetFileNameWithoutExtension(input.Path) : input.Name.Trim();
        var program = new ProgramEntry
        {
            Id = UniqueId(name),
            Name = name,
            SourceExe = Path.GetFullPath(input.Path),
            Arguments = (input.Args ?? "").Trim(),
            StartWithStarter = input.StartWithStarter,
            UpdateWithoutAsking = input.UpdateWithoutAsking,
            KeepPrevious = input.KeepPrevious,
            CopyToLocal = input.CopyToLocal,
            RestartOnCrash = input.RestartOnCrash,
            StartDelaySeconds = input.StartDelaySeconds,
            ReadyCheck = (input.ReadyCheck ?? "").Trim(),
        };
        _config.Programs.Add(program);
        SaveConfig();

        if (!program.CopyToLocal)
        {
            // Ohne Kopie: nichts zu kopieren, das Programm startet aus dem Quellordner. Der Eintrag bleibt auch, wenn
            // der Start scheitert; die Liste zeigt dann den Stand.
            var started = StartNow(program, manual: true);
            _log.Write($"add {program.Id}: runs from the source folder, {(started.Ok ? "started" : "not started")}: {started.Message}");
            _last = OpView.FromInPlaceStart(name, started);
            _opSeq++;
            _notice = started.Ok ? $"{name} runs from the source folder" : started.Message;
            SaveConfig();
            Publish();
            return;
        }

        var outcome = InstallNow(program, manual: true);
        if (program.Copies.Count == 0) _config.Programs.Remove(program);   // nichts kopiert: nichts aufnehmen
        _log.Write($"add {program.Id}: {outcome.Kind}, {outcome.Message}" + (program.Copies.Count == 0 ? " (nothing kept)" : ""));
        _last = OpView.FromInstall(program.Name, outcome);
        _opSeq++;
        _notice = outcome.Message;
        SaveConfig();
        Publish();
    }

    private void SaveSettings(JsonElement message)
    {
        var s = _config.Settings;
        s.CheckIntervalMinutes = Math.Clamp(Int(message, "checkMinutes", s.CheckIntervalMinutes), 1, 1440);
        s.StartWithWindows = Bool(message, "startWithWindows", s.StartWithWindows);
        s.AskBeforeUpdate = Bool(message, "askBeforeUpdate", s.AskBeforeUpdate);
        s.UpdateStoppedWithoutAsking = Bool(message, "updateStoppedWithoutAsking", s.UpdateStoppedWithoutAsking);
        s.VersionsKept = Math.Clamp(Int(message, "versionsKept", s.VersionsKept), 1, 5);
        s.StartupDelaySeconds = Math.Clamp(Int(message, "startupDelaySeconds", s.StartupDelaySeconds), 0, 60);
        var close = Str(message, "closeAction");
        if (close is "ask" or "tray" or "quit") s.CloseAction = close;
        SaveConfig();
        Reschedule();

        // Der Autostart gehört nur dem Standard-Datenordner: Probeläufe schreiben nie in die Registry
        if (_defaultData)
        {
            try
            {
                Autostart.Set(s.StartWithWindows, _exe);
                _notice = s.StartWithWindows ? "The Starter starts with Windows" : "The Starter does not start with Windows";
            }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or IOException)
            {
                _notice = "The autostart setting could not be written: " + ex.Message;
            }
        }
        else
        {
            _notice = "Settings saved (autostart is not changed with a test data folder)";
        }
        Publish();
    }

    // ---------- Beobachtung: Absturz und „bereit“ ----------

    // Der Fünf-Sekunden-Takt: jede Beobachtung läuft einmal auf der Warteschlange; kommt schon eine, wartet der Takt
    private void WatchTick()
    {
        if (Interlocked.Exchange(ref _watchQueued, 1) != 0) return;
        Enqueue(() =>
        {
            Interlocked.Exchange(ref _watchQueued, 0);
            WatchPrograms();
        });
    }

    // Nur die Prozessliste (keine Quellprüfung): Absturz erkennen, Neustart einleiten, und bei laufenden Programmen die
    // Bereit-Prüfung wiederholen
    private void WatchPrograms()
    {
        if (_stopping) return;
        var now = DateTime.UtcNow;
        bool changed = false;
        foreach (var program in ProgramList.Startable(_config).ToList())
        {
            bool running = _service.IsRunning(program);
            var decision = _crash.Observe(program.Id, running, program.StartWithStarter, program.RestartOnCrash, now);
            changed |= decision.Action is not CrashAction.None;
            switch (decision.Action)
            {
                case CrashAction.Crashed:
                    _log.Write($"crash {program.Id}: stopped unexpectedly, no restart");
                    _announce("Stykker Starter", $"{Label(program)} stopped unexpectedly");
                    break;
                case CrashAction.GaveUp:
                    _log.Write($"crash {program.Id}: {CrashWatch.MaxRestarts} restarts in {CrashWatch.Window.TotalMinutes:0} min; not restarted");
                    _announce("Stykker Starter", $"{Label(program)} keeps crashing – not restarted");
                    break;
                case CrashAction.Restart:
                    var started = StartNow(program, manual: false);
                    _log.Write($"crash {program.Id}: restart {(started.Ok ? "ok" : "failed")}, {started.Message}");
                    if (started.Ok) _announce("Stykker Starter", $"{Label(program)} stopped unexpectedly – restarted");
                    break;
            }

            if (!running)
            {
                _ready.Stopped(program.Id);
                continue;
            }
            if (ReadyRule.Parse(program.ReadyCheck) is { } target)
            {
                var before = _ready.StateOf(program.Id);
                changed |= _ready.Check(program.Id, target, _probe, now) != before;
            }
        }
        if (changed) Publish();
    }

    // Der Sekunden-Takt: nur wartende Programme (nach dem Start) werden geprüft, bis sie bereit sind oder die Frist abläuft
    private void ReadyTick()
    {
        if (Interlocked.Exchange(ref _readyQueued, 1) != 0) return;
        Enqueue(() =>
        {
            Interlocked.Exchange(ref _readyQueued, 0);
            ReadyWaiting();
        });
    }

    private void ReadyWaiting()
    {
        if (_stopping) return;
        var now = DateTime.UtcNow;
        bool changed = false;
        foreach (var program in ProgramList.Startable(_config).ToList())
        {
            if (ReadyRule.Parse(program.ReadyCheck) is not { } target) continue;
            if (_ready.StateOf(program.Id) != ReadyState.Waiting || !_service.IsRunning(program)) continue;
            changed |= _ready.Check(program.Id, target, _probe, now) != ReadyState.Waiting;
        }
        if (changed) Publish();
    }

    // ---------- Zustand und Anzeige ----------

    private void ReportCopy(string name, CopyProgress p)
    {
        _copy = new CopyView(name, p);
        var now = DateTime.UtcNow;
        if ((now - _copyPublished).TotalMilliseconds < 150 && p.FilesDone < p.FilesTotal) return;
        _copyPublished = now;
        Publish();
    }

    // Zustand an die Seite und die Momentaufnahme für den Tray (läuft auf dem Thread der Warteschlange)
    public void Publish()
    {
        var programs = _config.Programs.Select(ProgramSnapshot).ToArray();
        _programs = programs;
        var s = _config.Settings;
        var interval = _checkEverySeconds.HasValue ? $"{_checkEverySeconds} s" : $"{s.CheckIntervalMinutes} min";
        var state = new PageState(
            Type: "state",
            Programs: Overlay(programs),
            Settings: new SettingsView(s.CheckIntervalMinutes, s.StartWithWindows, s.AskBeforeUpdate, s.UpdateStoppedWithoutAsking,
                s.VersionsKept, s.CloseAction, _defaultData, s.StartupDelaySeconds),
            DataDir: _layout.Root,
            CheckedText: _lastChecked is { } c ? c.ToString("HH:mm") : null,
            CheckEvery: interval,
            NextCheckText: _nextCheck == default ? null : _nextCheck.ToString("HH:mm"),
            LocalBytes: LocalBytes(),
            CleanupBytes: CleanupBytes(),
            Copy: _copy?.ToDto(),
            Last: _last,
            OpSeq: _opSeq,
            Notice: _notice,
            StartupLeftMs: _startupAt is { } at ? Math.Max(0, (int)(at - DateTime.UtcNow).TotalMilliseconds) : null,
            StartupNext: _startupNext);
        _state = state;
        Emit(state);
        _trayChanged();
    }

    private void Emit(PageState state) => _sink(PageJson.Serialize(state));

    // Die laufenden Vorgänge in die Programme legen (Busy, BusySince, BusyGraceMs)
    private ProgramView[] Overlay(IEnumerable<ProgramView> programs) => programs.Select(p =>
        _busy.TryGetValue(p.Id, out var b)
            ? p with { Busy = b.Kind, BusySince = b.SinceMs, BusyGraceMs = b.GraceMs }
            : p with { Busy = null, BusySince = null, BusyGraceMs = null }).ToArray();

    private ProgramView ProgramSnapshot(ProgramEntry p)
    {
        var running = _service.RunningProcesses(p);
        var check = _checks.GetValueOrDefault(p.Id);
        bool isRunning = running.Count > 0;
        // Die Version, die gerade läuft: die Kopie, oder ohne Kopie der Stand beim letzten Start
        string? runningVersion = p.CopyToLocal ? p.Current?.Version : p.StartedVersion;
        bool sourceChanged = check?.Kind is CheckKind.UpdateAvailable or CheckKind.ChangedSinceStart;
        return new ProgramView(
            p.Id, p.Name, p.SourceExe, p.Arguments,
            Local: p.Current?.Version,
            SourceVersion: check?.SourceVersion,
            Reachable: check?.Kind != CheckKind.Unreachable,
            UpdateVersion: check?.Kind == CheckKind.UpdateAvailable ? check.SourceVersion : null,
            Running: isRunning,
            Pids: running.Select(r => r.Pid).ToArray(),
            Folder: _service.CurrentDir(p),
            HasCopy: p.Current != null,
            StartWithStarter: p.StartWithStarter,
            UpdateWithoutAsking: p.UpdateWithoutAsking,
            KeepPrevious: p.KeepPrevious,
            CopyToLocal: p.CopyToLocal,
            ChangedSinceStart: check?.Kind == CheckKind.ChangedSinceStart,
            Broken: ProgramCheck.BrokenReason(p),
            UpdateIsNewBuild: sourceChanged && check?.SourceVersion is { } sv && VersionPlanner.IsNewBuild(sv, runningVersion),
            SourceBuiltAt: check?.SourceBuiltAt,
            Previous: _service.PreviousCopy(p)?.Version,
            CrashState: _crash.StateOf(p.Id),
            Restarts: _crash.RestartsIn(p.Id, DateTime.UtcNow),
            Ready: !isRunning || ReadyRule.Parse(p.ReadyCheck) == null ? null : _ready.StateOf(p.Id) switch
            {
                ReadyState.Ready => "ready",
                ReadyState.Timeout => "timeout",
                _ => "waiting",
            },
            RestartOnCrash: p.RestartOnCrash,
            StartDelaySeconds: p.StartDelaySeconds,
            ReadyCheck: p.ReadyCheck,
            HeldAfterRollback: p.HeldAfterRollback);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private long LocalBytes() => _config.Programs
        .Where(p => DataLayout.IsPlainName(p.Id))
        .SelectMany(p => p.Copies.Select(c => SizeOf(p.Id, c.Folder)))
        .Sum();

    // Was „Remove unused copies“ frei machen würde: nicht die laufende Kopie und nichts, was noch läuft
    private long CleanupBytes()
    {
        long total = 0;
        foreach (var p in _config.Programs.Where(q => DataLayout.IsPlainName(q.Id)))
        {
            foreach (var copy in p.Copies.Where(c => !p.CopyToLocal || c.Folder != p.CurrentFolder))
            {
                var dir = _layout.VersionDir(p.Id, copy.Folder);
                if (FolderProcesses.RunningFrom(_processes, dir, Environment.ProcessId).Count == 0)
                    total += SizeOf(p.Id, copy.Folder);
            }
        }
        return total;
    }

    private long SizeOf(string programId, string folder)
    {
        var dir = _layout.VersionDir(programId, folder);
        if (_sizes.TryGetValue(dir, out var cached)) return cached;
        long size = 0;
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { size += new FileInfo(file).Length; } catch (IOException) { }
        }
        _sizes[dir] = size;   // Kopien ändern sich nach dem Kopieren nicht mehr
        return size;
    }

    private void Reschedule()
    {
        var period = _checkEverySeconds.HasValue
            ? TimeSpan.FromSeconds(_checkEverySeconds.Value)
            : TimeSpan.FromMinutes(_config.Settings.CheckIntervalMinutes);
        _nextCheck = DateTime.Now + period;
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => Enqueue(CheckAll), null, period, period);
    }

    private StarterConfig LoadConfig()
    {
        try { return ConfigStore.Load(_layout); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            _log.Write($"config: could not be read ({ex.Message}); starting without programs, the file is kept");
            _notice = "starter.json could not be read; it is kept as it is";
            return new StarterConfig();
        }
    }

    private void SaveConfig()
    {
        try { ConfigStore.Save(_layout, _config); }
        catch (IOException ex) { _log.Write($"config: not saved: {ex.Message}"); }
    }

    private void Reply(object payload) => _sink(PageJson.Serialize(payload));

    private ProgramEntry? Find(string id) => _config.Find(id);

    // Ein kaputter Eintrag läuft nie: der Starter sagt, warum; entfernen lässt er sich
    private ProgramEntry? Runnable(string id)
    {
        var program = Find(id);
        if (program == null) return null;
        if (ProgramCheck.BrokenReason(program) is not { } reason) return program;
        _notice = $"{Label(program)} is a broken entry ({reason}); remove it";
        Publish();
        return null;
    }

    private static string Label(ProgramEntry program) => string.IsNullOrWhiteSpace(program.Name) ? program.Id : program.Name;

    private string UniqueId(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-').ToArray());
        slug = string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length == 0) slug = "program";
        var id = slug;
        for (int n = 2; _config.Find(id) != null || Directory.Exists(_layout.ProgramDir(id)); n++)
            id = $"{slug}-{n}";
        return id;
    }

    // Die Version der App (Assembly, InformationalVersion), für das Protokoll
    private static string Version() =>
        typeof(Model).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?";

    private static bool IsProgramPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name, bool fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

    private static int Int(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;

    private sealed record NewProgram(string Path, string Name, string Args, bool StartWithStarter, bool UpdateWithoutAsking, bool KeepPrevious,
        bool CopyToLocal, bool RestartOnCrash, int StartDelaySeconds, string ReadyCheck);

    private sealed class CopyProgressSink(Action<CopyProgress> report) : IProgress<CopyProgress>
    {
        public void Report(CopyProgress value) => report(value);
    }

    // Ein laufender oder wartender Vorgang (Kind: queued, starting, stopping, restarting, updating, copying, rollback)
    private sealed record BusyState(string Kind, long SinceMs, int? GraceMs);
}

// Der Zustand für die Seite (camelCase in der JSON-Ausgabe); unveränderlich, damit der Fensterthread ihn lesen kann
internal sealed record PageState(
    string Type,
    ProgramView[] Programs,
    SettingsView Settings,
    string DataDir,
    string? CheckedText,
    string CheckEvery,
    string? NextCheckText,
    long LocalBytes,
    long CleanupBytes,
    object? Copy,
    OpView? Last,
    int OpSeq,
    string? Notice,
    int? StartupLeftMs,
    string? StartupNext);

internal sealed record SettingsView(
    int CheckMinutes,
    bool StartWithWindows,
    bool AskBeforeUpdate,
    bool UpdateStoppedWithoutAsking,
    int VersionsKept,
    string CloseAction,
    bool AutostartApplies,
    int StartupDelaySeconds);

// Der laufende Kopiervorgang für die Seite
internal sealed record CopyView(string Name, CopyProgress P)
{
    public object ToDto() => new
    {
        name = Name,
        percent = Math.Round(P.Percent, 1),
        filesDone = P.FilesDone,
        filesTotal = P.FilesTotal,
        bytesDone = P.BytesDone,
        bytesTotal = P.BytesTotal,
        speed = $"{Megabytes(P.BytesPerSecond):0.0} MB/s",
        left = P.Remaining is { } r ? (r.TotalSeconds < 60 ? $"about {Math.Ceiling(r.TotalSeconds)} s" : $"about {Math.Ceiling(r.TotalMinutes)} min") : "–",
        current = P.CurrentFile ?? "",
    };

    private static double Megabytes(double bytes) => bytes / 1_000_000.0;
}

// Ergebnis des letzten Vorgangs (Ablauf-Schritte für die Ansicht „Update“)
internal sealed record OpView(string Title, string Kind, string Message, IReadOnlyList<OpStep> Steps)
{
    public static OpView FromCheck(string title, CheckResult r)
    {
        var steps = new List<OpStep> { new("New version found", r.SourceVersion is null ? "fail" : "done", r.SourceVersion ?? r.Message) };
        var o = r.Outcome;
        switch (r.Kind)
        {
            case CheckKind.Updated when o is not null:
                AddOutcomeSteps(steps, o);
                return new OpView(title, o.Kind is UpdateKind.Updated or UpdateKind.Restarted or UpdateKind.Switched or UpdateKind.NothingToDo ? "ok" : "bad", o.Message, steps);
            case CheckKind.Unreachable:
                steps[0] = new OpStep("Source not reachable", "fail", r.Message);
                return new OpView(title, "bad", r.Message, steps);
            case CheckKind.UpToDate:
                return new OpView(title, "ok", r.Message, steps);
            default:
                return new OpView(title, "warn", r.Message, steps);
        }
    }

    public static OpView FromInstall(string name, UpdateOutcome o)
    {
        var steps = new List<OpStep>();
        AddOutcomeSteps(steps, o);
        return new OpView($"Copy {name}", o.Kind is UpdateKind.Updated or UpdateKind.Switched ? "ok" : "bad", o.Message, steps);
    }

    public static OpView FromRollback(string name, UpdateOutcome o)
    {
        var steps = new List<OpStep>();
        AddOutcomeSteps(steps, o);
        return new OpView($"Back {name}", o.Kind == UpdateKind.WentBack ? "ok" : "bad", o.Message, steps);
    }

    public static OpView FromInPlaceStart(string name, StartOutcome o) =>
        new($"Start {name}", o.Ok ? "ok" : "bad", o.Ok ? $"{name} runs from the source folder" : o.Message,
            new List<OpStep> { new("Started from the source folder", o.Ok ? "done" : "fail", o.Message) });

    private static void AddOutcomeSteps(List<OpStep> steps, UpdateOutcome o)
    {
        switch (o.Kind)
        {
            case UpdateKind.Updated:
                steps.Add(new OpStep("Copied and checked", "done", ""));
                steps.Add(new OpStep("Stopped the old version", "done", $"{o.StoppedPids.Count} process(es)"));
                steps.Add(new OpStep("Started the new version", "done", ""));
                break;
            case UpdateKind.Restarted:
                steps.Add(new OpStep("Stopped what runs from the source folder", "done", $"{o.StoppedPids.Count} process(es)"));
                steps.Add(new OpStep("Started the new build from the source folder", "done", ""));
                break;
            case UpdateKind.RolledBack:
                steps.Add(new OpStep("Copied and checked", "done", ""));
                steps.Add(new OpStep("Stopped the old version", "done", $"{o.StoppedPids.Count} process(es)"));
                steps.Add(new OpStep("The new version did not start", "fail", o.Message));
                steps.Add(new OpStep("Started the previous version again", "done", ""));
                break;
            case UpdateKind.WentBack:
                steps.Add(new OpStep("Stopped the current version", "done", $"{o.StoppedPids.Count} process(es)"));
                steps.Add(new OpStep("Started the earlier version", "done", o.Message));
                break;
            case UpdateKind.Switched:
                steps.Add(new OpStep("Copied and checked", "done", ""));
                steps.Add(new OpStep("Switched; the program was not running", "done", ""));
                break;
            case UpdateKind.NothingToDo:
                steps.Add(new OpStep("Already the newest version", "done", ""));
                break;
            default:
                steps.Add(new OpStep("Failed", "fail", o.Message));
                break;
        }
    }
}

internal sealed record OpStep(string Label, string State, string Detail);
