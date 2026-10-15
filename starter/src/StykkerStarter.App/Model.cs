using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using StykkerStarter.Core;
using StykkerStarter.Core.Config;
using StykkerStarter.Core.Copy;
using StykkerStarter.Core.Processes;
using StykkerStarter.Core.Source;
using StykkerStarter.Core.Start;
using StykkerStarter.Core.Update;
using StykkerStarter.Core.Versions;
using StykkerStarter.Core.Web;
using StykkerStarter.Platform.Windows;

namespace StykkerStarter.App;

// Der Zustand des Starters und alle Vorgänge. Alles läuft nacheinander auf einem eigenen Thread (einer Warteschlange),
// damit Kopieren, Umschalten, Prüfen und Einstellungen einander nicht in die Quere kommen. Die Oberfläche bekommt
// nur fertige JSON-Zustände; der Tray bekommt eine Momentaufnahme der Programme.
internal sealed class Model : IDisposable
{
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
    private readonly ProgramService _service;
    private readonly StarterEngine _engine;
    private readonly Dictionary<string, CheckResult> _checks = new();
    private readonly HashSet<string> _announced = new();
    private readonly Dictionary<string, long> _sizes = new();

    private StarterConfig _config = new();
    private CancellationTokenSource? _cancel;
    private CopyView? _copy;
    private OpView? _last;
    private int _opSeq;        // zählt die abgeschlossenen Vorgänge; die Seite erkennt daran das Ende
    private string? _notice;
    private DateTime? _lastChecked;
    private DateTime _nextCheck;
    private System.Threading.Timer? _timer;
    private DateTime _copyPublished = DateTime.MinValue;

    private volatile ProgramView[] _programs = Array.Empty<ProgramView>();

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
            StartupPrograms();
            CheckAll();
        });
    }

    // Beim Start: ein Programm ohne Kopie wird eingerichtet (kopieren und starten); eines, das mit dem Starter starten
    // soll, startet, wenn es nicht läuft.
    private void StartupPrograms()
    {
        foreach (var program in _config.Programs.ToList())
        {
            if (ProgramCheck.BrokenReason(program) != null) continue;   // ein kaputter Eintrag läuft nie
            try
            {
                if (!program.CopyToLocal)
                {
                    if (program.StartWithStarter && !_service.IsRunning(program))
                    {
                        var started = _engine.StartInPlace(program);
                        _log.Write($"start {program.Id} at start: {(started.Ok ? "ok" : "failed")}, {started.Message}");
                    }
                    continue;
                }
                if (program.Current == null)
                {
                    var outcome = _engine.Install(program);
                    _log.Write($"install {program.Id} at start: {outcome.Kind}, {outcome.Message}");
                    if (program.Copies.Count == 0) _config.Programs.Remove(program);
                }
                else if (program.StartWithStarter && _service.RunningProcesses(program).Count == 0)
                {
                    var started = _service.StartCurrent(program);
                    _log.Write($"start {program.Id} at start: {(started.Ok ? "ok" : "failed")}, {started.Message}");
                }
            }
            catch (Exception ex) when (ex is SourceUnreachableException or InvalidOperationException or IOException)
            {
                _log.Write($"install {program.Id} at start: {ex.Message}");
            }
        }
        SaveConfig();
    }

    public void Enqueue(Action action)
    {
        try { _queue.Add(action); }
        catch (InvalidOperationException) { }   // schon beendet
    }

    // Für den Tray (der Thread des Menüs): dieselben Vorgänge wie aus der Seite
    public void RequestUpdate(string id) => Enqueue(() => UpdateProgram(id));

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

    public void Dispose()
    {
        _timer?.Dispose();
        _cancel?.Cancel();
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    // Befehle aus der Seite (JSON). Läuft auf dem Fensterthread; die Arbeit geht in die Warteschlange.
    public void Command(JsonElement message)
    {
        var cmd = Str(message, "cmd");
        switch (cmd)
        {
            case "ready": Enqueue(Publish); break;
            case "check": Enqueue(CheckAll); break;
            case "update": { var id = Str(message, "id"); Enqueue(() => UpdateProgram(id)); break; }
            case "start": { var id = Str(message, "id"); Enqueue(() => StartProgram(id)); break; }
            case "stop": { var id = Str(message, "id"); Enqueue(() => StopProgram(id)); break; }
            case "restart": { var id = Str(message, "id"); Enqueue(() => RestartProgram(id)); break; }
            case "remove": { var id = Str(message, "id"); Enqueue(() => RemoveProgram(id)); break; }
            case "cleanup": Enqueue(CleanupAll); break;
            case "cancel": _cancel?.Cancel(); break;
            case "inspect": { var path = Str(message, "path"); Enqueue(() => Inspect(path)); break; }
            case "add":
                Enqueue(() => AddProgram(new NewProgram(
                    Str(message, "path"), Str(message, "name"), Str(message, "args"),
                    Bool(message, "startWithStarter", true), Bool(message, "updateWithoutAsking", false), Bool(message, "keepPrevious", true),
                    Bool(message, "copyToLocal", true))));
                break;
            case "settings":
                Enqueue(() => SaveSettings(message));
                break;
        }
    }

    // ---------- Vorgänge (laufen auf dem Thread der Warteschlange) ----------

    private void CheckAll()
    {
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
                _announce("Update available", $"{program.Name} {v} is ready. Open the Starter to update.");
        }
        if (changed) SaveConfig();
        Reschedule();
        Publish();
    }

    private void UpdateProgram(string id)
    {
        var program = Runnable(id);
        if (program == null) return;
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
        Publish();
    }

    private void StartProgram(string id)
    {
        var program = Runnable(id);
        if (program == null) return;
        var outcome = StartNow(program);
        _notice = outcome.Ok ? $"{program.Name} is running" : $"{program.Name} did not start: {outcome.Message}";
        Publish();
    }

    // Startet die laufende Kopie; ohne Kopie die Quelle (dabei merkt sich der Starter den Stand, mit dem es startet)
    private StartOutcome StartNow(ProgramEntry program)
    {
        if (program.CopyToLocal) return _service.StartCurrent(program);
        var outcome = _engine.StartInPlace(program);
        SaveConfig();
        return outcome;
    }

    private void StopProgram(string id)
    {
        var program = Runnable(id);
        if (program == null) return;
        var report = _service.Stop(program);
        int count = report.Closed.Count + report.Killed.Count;
        _notice = report.AllGone
            ? $"{program.Name} stopped{(count > 0 ? $" ({count} process{(count == 1 ? "" : "es")})" : "")}"
            : $"{program.Name}: {report.Left.Count} process(es) still running";
        Publish();
    }

    private void RestartProgram(string id)
    {
        var program = Runnable(id);
        if (program == null) return;
        var report = _service.Stop(program);
        var outcome = report.AllGone ? StartNow(program) : new StartOutcome(false, null, "the old version did not stop");
        _notice = outcome.Ok ? $"{program.Name} restarted" : $"{program.Name} did not restart: {outcome.Message}";
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

    private void Inspect(string path)
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
        var name = string.IsNullOrWhiteSpace(input.Name) ? Path.GetFileNameWithoutExtension(input.Path) : input.Name.Trim();
        var program = new ProgramEntry
        {
            Id = UniqueId(name),
            Name = name,
            SourceExe = Path.GetFullPath(input.Path),
            Arguments = input.Args ?? "",
            StartWithStarter = input.StartWithStarter,
            UpdateWithoutAsking = input.UpdateWithoutAsking,
            KeepPrevious = input.KeepPrevious,
            CopyToLocal = input.CopyToLocal,
        };
        _config.Programs.Add(program);
        SaveConfig();

        if (!program.CopyToLocal)
        {
            // Ohne Kopie: nichts zu kopieren, das Programm startet aus dem Quellordner. Der Eintrag bleibt auch, wenn
            // der Start scheitert; die Liste zeigt dann den Stand.
            var started = StartNow(program);
            _log.Write($"add {program.Id}: runs from the source folder, {(started.Ok ? "started" : "not started")}: {started.Message}");
            _last = OpView.FromInPlaceStart(name, started);
            _opSeq++;
            _notice = started.Ok ? $"{name} runs from the source folder" : started.Message;
            SaveConfig();
            Publish();
            return;
        }

        var cts = new CancellationTokenSource();
        _cancel = cts;
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
        }

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

    // ---------- Zustand und Anzeige ----------

    private void ReportCopy(string name, CopyProgress p)
    {
        _copy = new CopyView(name, p);
        var now = DateTime.UtcNow;
        if ((now - _copyPublished).TotalMilliseconds < 150 && p.FilesDone < p.FilesTotal) return;
        _copyPublished = now;
        Publish();
    }

    // Zustand an die Seite und die Momentaufnahme für den Tray
    public void Publish()
    {
        _programs = _config.Programs.Select(ProgramSnapshot).ToArray();
        var settings = _config.Settings;
        var interval = _checkEverySeconds.HasValue ? $"{_checkEverySeconds} s" : $"{settings.CheckIntervalMinutes} min";
        var json = PageJson.Serialize(new
        {
            type = "state",
            programs = _programs,
            settings = new
            {
                checkMinutes = settings.CheckIntervalMinutes,
                startWithWindows = settings.StartWithWindows,
                askBeforeUpdate = settings.AskBeforeUpdate,
                updateStoppedWithoutAsking = settings.UpdateStoppedWithoutAsking,
                versionsKept = settings.VersionsKept,
                autostartApplies = _defaultData,
            },
            dataDir = _layout.Root,
            checkedText = _lastChecked is { } c ? c.ToString("HH:mm") : null,
            checkEvery = interval,
            nextCheckText = _nextCheck == default ? null : _nextCheck.ToString("HH:mm"),
            localBytes = LocalBytes(),
            cleanupBytes = CleanupBytes(),
            copy = _copy == null ? null : _copy.ToDto(),
            last = _last,
            opSeq = _opSeq,
            notice = _notice,
        });
        _sink(json);
        _trayChanged();
    }

    private ProgramView ProgramSnapshot(ProgramEntry p)
    {
        var running = _service.RunningProcesses(p);
        var check = _checks.GetValueOrDefault(p.Id);
        return new ProgramView(
            p.Id, p.Name, p.SourceExe, p.Arguments,
            Local: p.Current?.Version,
            SourceVersion: check?.SourceVersion,
            Reachable: check?.Kind != CheckKind.Unreachable,
            UpdateVersion: check?.Kind == CheckKind.UpdateAvailable ? check.SourceVersion : null,
            Running: running.Count > 0,
            Pids: running.Select(r => r.Pid).ToArray(),
            Folder: _service.CurrentDir(p),
            HasCopy: p.Current != null,
            StartWithStarter: p.StartWithStarter,
            UpdateWithoutAsking: p.UpdateWithoutAsking,
            KeepPrevious: p.KeepPrevious,
            CopyToLocal: p.CopyToLocal,
            ChangedSinceStart: check?.Kind == CheckKind.ChangedSinceStart,
            Broken: ProgramCheck.BrokenReason(p));
    }

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
        bool CopyToLocal);

    private sealed class CopyProgressSink(Action<CopyProgress> report) : IProgress<CopyProgress>
    {
        public void Report(CopyProgress value) => report(value);
    }
}

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
