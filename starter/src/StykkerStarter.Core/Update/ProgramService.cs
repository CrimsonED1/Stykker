using StykkerStarter.Core.Config;
using StykkerStarter.Core.Copy;
using StykkerStarter.Core.Processes;
using StykkerStarter.Core.Source;
using StykkerStarter.Core.Start;
using StykkerStarter.Core.Versions;

namespace StykkerStarter.Core.Update;

// Fristen. Vorgabe aus dem Auftrag: 10 s bis zum endgültigen Beenden, 5 s Beobachtung nach dem Start.
public sealed record ProgramTiming(TimeSpan CloseGrace, TimeSpan StartWatch)
{
    public static ProgramTiming Default { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
}

public enum UpdateKind
{
    NothingToDo,   // schon der Stand der Quelle
    Switched,      // neue Version eingetragen; das Programm lief nicht, also wurde nichts gestartet
    Updated,       // neue Version läuft, alte ist beendet
    Restarted,     // ohne Kopie: der neue Stand läuft aus dem Quellordner, der alte ist beendet
    RolledBack,    // neue Version startete nicht; die alte läuft wieder
    WentBack,      // auf Wunsch zurück zur vorigen Kopie (Rollback); die neuere Kopie bleibt liegen
    Failed,        // Kopieren fehlgeschlagen, oder auch die alte startet nicht mehr; Stand bleibt der alte
}

public sealed record UpdateOutcome(UpdateKind Kind, string Message, IReadOnlyList<int> StoppedPids, string? NewFolder);

public sealed record CleanupReport(IReadOnlyList<string> Removed, IReadOnlyList<string> KeptRunning);

// Der Ablauf eines Programms: Kopieren, Beenden, Starten, Zurückgehen, Aufräumen. Ändert die ProgramEntry; speichern
// tut der Aufrufer (ConfigStore).
public sealed class ProgramService
{
    private readonly DataLayout _layout;
    private readonly IProcessSystem _processes;
    private readonly IProgramLauncher _launcher;
    private readonly StarterLog? _log;
    private readonly ProgramTiming _timing;
    private readonly int _ownPid;

    // Meldung, wenn ein Programm ohne Kopie nicht starten kann, weil der Quellordner nicht erreichbar ist
    public const string SourceFolderUnreachable = "source folder not reachable – no local copy";

    public ProgramService(DataLayout layout, IProcessSystem processes, IProgramLauncher launcher, StarterLog? log = null,
        ProgramTiming? timing = null, int? ownPid = null)
    {
        _layout = layout;
        _processes = processes;
        _launcher = launcher;
        _log = log;
        _timing = timing ?? ProgramTiming.Default;
        _ownPid = ownPid ?? Environment.ProcessId;
    }

    // Der Ordner, aus dem das Programm läuft: die Kopie, oder ohne Kopie der Quellordner. Null, wenn es noch keine
    // Kopie gibt (oder der Quellpfad nicht gültig ist).
    public string? CurrentDir(ProgramEntry program)
    {
        if (!program.CopyToLocal)
        {
            try { return Path.GetDirectoryName(Path.GetFullPath(program.SourceExe)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
        }
        return program.CurrentFolder is { } folder ? _layout.VersionDir(program.Id, folder) : null;
    }

    // Was aus dem Ordner der laufenden Kopie läuft (für den Update-Dialog und das Tray-Menü)
    public IReadOnlyList<ProcessInfo> RunningProcesses(ProgramEntry program) =>
        CurrentDir(program) is { } dir ? FolderProcesses.RunningFrom(_processes, dir, _ownPid) : Array.Empty<ProcessInfo>();

    public bool IsRunning(ProgramEntry program) => RunningProcesses(program).Count > 0;

    // Startet die laufende Kopie, sofern sie nicht schon läuft
    public StartOutcome StartCurrent(ProgramEntry program)
    {
        var dir = CurrentDir(program);
        if (dir == null || !Directory.Exists(dir))
            return new StartOutcome(false, null, program.CopyToLocal ? "there is no local copy yet" : SourceFolderUnreachable);
        if (IsRunning(program)) return new StartOutcome(true, RunningProcesses(program)[0].Pid, "already running");
        var outcome = StartFrom(program, dir);
        _log?.Write($"start {program.Id}: {(outcome.Ok ? "ok" : "failed")}, pid {outcome.Pid}, {outcome.Message}");
        return outcome;
    }

    // Beendet alles, was aus der laufenden Kopie läuft
    public StopReport Stop(ProgramEntry program)
    {
        var dir = CurrentDir(program);
        if (dir == null) return new StopReport(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());
        return FolderStopper.StopAll(_processes, dir, _ownPid, _timing.CloseGrace, _log);
    }

    // Die Quelle ist neuer (oder anders): kopieren, bei Bedarf umschalten. Der Plan kommt von VersionPlanner.
    // Reihenfolge: erst die neue Kopie fertig und geprüft (die alte läuft derweil), dann beenden, dann starten.
    public UpdateOutcome Update(ProgramEntry program, SourceTree source, CopyPlan plan, IProgress<CopyProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (plan.Kind == PlanKind.UpToDate)
            return new UpdateOutcome(UpdateKind.NothingToDo, "already the newest version", Array.Empty<int>(), null);

        var oldDir = CurrentDir(program);
        var newDir = _layout.VersionDir(program.Id, plan.Folder);

        if (CopyNew(program, source, plan, progress, ct) is { } copyFailed) return copyFailed;

        // Die alte Version läuft nicht: nur umschalten, nichts starten
        bool wasRunning = oldDir != null && FolderProcesses.RunningFrom(_processes, oldDir, _ownPid).Count > 0;
        if (!wasRunning)
        {
            program.CurrentFolder = plan.Folder;
            _log?.Write($"switch {program.Id} to {plan.Version} (was not running)");
            return new UpdateOutcome(UpdateKind.Switched, $"now {plan.Version}; it was not running, so nothing was started", Array.Empty<int>(), plan.Folder);
        }

        var stopped = FolderStopper.StopAll(_processes, oldDir!, _ownPid, _timing.CloseGrace, _log);
        var stoppedPids = stopped.Closed.Concat(stopped.Killed).Distinct().ToList();
        if (!stopped.AllGone)
        {
            _log?.Write($"update {program.Id}: {stopped.Left.Count} process(es) still run from the old version; not switching");
            return new UpdateOutcome(UpdateKind.Failed, "the old version did not stop; the new one was not started", stoppedPids, plan.Folder);
        }

        var started = StartFrom(program, newDir);
        _log?.Write($"start {program.Id} {plan.Version}: {(started.Ok ? "ok" : "failed")}, pid {started.Pid}, {started.Message}");
        if (started.Ok)
        {
            program.CurrentFolder = plan.Folder;
            return new UpdateOutcome(UpdateKind.Updated, $"now {plan.Version}", stoppedPids, plan.Folder);
        }

        // Die neue startet nicht: die alte wieder an. Die Kopie der neuen bleibt liegen (sie wird beim Aufräumen entfernt).
        _log?.Write($"update {program.Id}: {plan.Version} did not start; starting the old version again");
        var back = oldDir != null && Directory.Exists(oldDir) ? StartFrom(program, oldDir) : new StartOutcome(false, null, "no old version");
        _log?.Write($"start {program.Id} old version: {(back.Ok ? "ok" : "failed")}, {back.Message}");
        if (back.Ok)
            return new UpdateOutcome(UpdateKind.RolledBack, $"{plan.Version} did not start ({started.Message}); the previous version runs again", stoppedPids, null);
        return new UpdateOutcome(UpdateKind.Failed, $"{plan.Version} did not start ({started.Message}) and the previous version did not start either ({back.Message})", stoppedPids, null);
    }

    // Ohne Kopie: der neue Stand liegt in der Quelle. Beendet, was aus dem Quellordner läuft (erst höflich, nach 10 s
    // endgültig), startet die Quelle neu und merkt sich den Stand. Startet sie nicht, gibt es keinen Rücksprung; der
    // Ergebnis sagt es, der alte Stand bleibt gemerkt.
    public UpdateOutcome RestartInPlace(ProgramEntry program, string version, string listHash)
    {
        var stopped = Stop(program);
        var stoppedPids = stopped.Closed.Concat(stopped.Killed).Distinct().ToList();
        if (!stopped.AllGone)
        {
            _log?.Write($"restart {program.Id}: {stopped.Left.Count} process(es) still run from the source folder; not starting");
            return new UpdateOutcome(UpdateKind.Failed, "the running program did not stop; the new build was not started", stoppedPids, null);
        }

        var started = StartCurrent(program);
        if (!started.Ok)
            return new UpdateOutcome(UpdateKind.Failed,
                $"{version} did not start ({started.Message}); there is no previous build to go back to", stoppedPids, null);

        program.StartedVersion = version;
        program.StartedListHash = listHash;
        return new UpdateOutcome(UpdateKind.Restarted, $"{version} started from the source folder", stoppedPids, null);
    }

    // Erste Einrichtung: kopieren, eintragen und starten. Schlägt das Starten fehl, bleibt die Kopie stehen und der
    // Eintrag zeigt auf sie; ein Zurückgehen gibt es hier nicht, weil es keine alte Version gibt.
    public UpdateOutcome Install(ProgramEntry program, SourceTree source, CopyPlan plan, IProgress<CopyProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (CopyNew(program, source, plan, progress, ct) is { } copyFailed) return copyFailed;
        program.CurrentFolder = plan.Folder;
        var started = StartCurrent(program);
        return started.Ok
            ? new UpdateOutcome(UpdateKind.Updated, $"{plan.Version} installed and started", Array.Empty<int>(), plan.Folder)
            : new UpdateOutcome(UpdateKind.Failed, $"{plan.Version} installed, but it did not start: {started.Message}", Array.Empty<int>(), plan.Folder);
    }

    // Kopiert die Quelle in einen neuen Ordner und trägt die Kopie ein. Liefert null bei Erfolg, sonst das Ergebnis
    // des Fehlers. Der Plan Reuse und UpToDate brauchen keine Kopie.
    private UpdateOutcome? CopyNew(ProgramEntry program, SourceTree source, CopyPlan plan, IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        if (plan.Kind != PlanKind.Copy) return null;
        var newDir = _layout.VersionDir(program.Id, plan.Folder);
        try
        {
            VersionCopier.Copy(source, newDir, progress, ct);
        }
        catch (OperationCanceledException)
        {
            _log?.Write($"copy {program.Id}: cancelled");
            return new UpdateOutcome(UpdateKind.Failed, "the copy was cancelled; the running version is unchanged", Array.Empty<int>(), null);
        }
        catch (CopyFailedException ex)
        {
            _log?.Write($"copy {program.Id}: failed: {ex.Message}");
            return new UpdateOutcome(UpdateKind.Failed, "the copy failed; the running version is unchanged: " + ex.Message, Array.Empty<int>(), null);
        }
        program.Copies.Add(new CopyRecord { Folder = plan.Folder, Version = plan.Version, ListHash = plan.ListHash, CopiedUtc = DateTime.UtcNow });
        _log?.Write($"copy {program.Id}: {plan.Version} into {plan.Folder}, {source.Files.Count} files, {source.TotalBytes} bytes, checked");
        return null;
    }

    // Entfernt Kopien, die nicht mehr gebraucht werden: alles außer der laufenden und den „keep“ neuesten. Eine Kopie,
    // aus deren Ordner noch etwas läuft, bleibt. KeepPrevious = false behält nur die laufende. Ohne Kopie läuft nichts
    // aus einer Kopie, also geht auch die zuletzt laufende.
    public CleanupReport Cleanup(ProgramEntry program, int versionsKept)
    {
        int keep = program.CopyToLocal && program.KeepPrevious ? Math.Max(1, versionsKept) : 1;
        var newest = program.Copies
            .Where(c => c.Folder != program.CurrentFolder)
            .OrderByDescending(c => c.CopiedUtc)
            .Take(keep - 1)
            .Select(c => c.Folder)
            .ToHashSet();

        var removed = new List<string>();
        var kept = new List<string>();
        foreach (var copy in program.Copies.ToList())
        {
            bool current = copy.Folder == program.CurrentFolder;
            if ((current && program.CopyToLocal) || newest.Contains(copy.Folder)) continue;
            var dir = _layout.VersionDir(program.Id, copy.Folder);
            if (FolderProcesses.RunningFrom(_processes, dir, _ownPid).Count > 0)
            {
                kept.Add(copy.Folder);
                continue;
            }
            VersionCopier.DeleteTree(dir);
            if (Directory.Exists(dir))
            {
                kept.Add(copy.Folder);   // der nächste Versuch räumt es weg
                continue;
            }
            program.Copies.Remove(copy);
            if (current) program.CurrentFolder = null;
            removed.Add(copy.Folder);
            _log?.Write($"cleanup {program.Id}: removed {copy.Folder}");
        }
        return new CleanupReport(removed, kept);
    }

    // Ohne Start: die Quelle in eine neue Kopie, und die Kopie wird die aktuelle. Das Programm darf nicht laufen (sonst
    // würde es umgeschaltet, ohne dass es neu startet). Für „Copy to this PC“ beim Bearbeiten.
    public UpdateOutcome CopyOnly(ProgramEntry program, SourceTree source, CopyPlan plan, IProgress<CopyProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (CopyNew(program, source, plan, progress, ct) is { } failed) return failed;
        program.CurrentFolder = plan.Folder;
        _log?.Write($"copy {program.Id}: {plan.Version} is the current copy; it runs from it the next time it starts");
        return new UpdateOutcome(UpdateKind.Switched,
            $"{plan.Version} copied; the program runs from the copy the next time it starts", Array.Empty<int>(), plan.Folder);
    }

    // Die vorige Version: die jüngste behaltene Kopie, die älter ist als die laufende und auf der Platte liegt. Nur mit
    // Kopie; null, wenn es keine gibt.
    public CopyRecord? PreviousCopy(ProgramEntry program)
    {
        if (!program.CopyToLocal) return null;
        var current = program.Current;
        return program.Copies
            .Where(c => c.Folder != program.CurrentFolder && (current == null || c.CopiedUtc < current.CopiedUtc))
            .Where(c => Directory.Exists(_layout.VersionDir(program.Id, c.Folder)))
            .OrderByDescending(c => c.CopiedUtc)
            .FirstOrDefault();
    }

    // Zurück zur vorigen Kopie: beendet, was läuft, startet die vorige und setzt sie als aktuell. Die neuere Kopie bleibt
    // liegen (das Aufräumen entfernt sie später). Danach übernimmt die Prüfung nichts ohne Rückfrage (HeldAfterRollback).
    // Startet die vorige nicht, läuft die aktuelle wieder, und der Stand bleibt.
    public UpdateOutcome Rollback(ProgramEntry program)
    {
        var previous = PreviousCopy(program);
        if (previous == null)
            return new UpdateOutcome(UpdateKind.Failed, "there is no earlier version to go back to", Array.Empty<int>(), null);

        var oldDir = CurrentDir(program);
        var stopped = Stop(program);
        var stoppedPids = stopped.Closed.Concat(stopped.Killed).Distinct().ToList();
        if (!stopped.AllGone)
        {
            _log?.Write($"rollback {program.Id}: {stopped.Left.Count} process(es) still run; not switching");
            return new UpdateOutcome(UpdateKind.Failed, "the running version did not stop; nothing was switched", stoppedPids, null);
        }

        var prevDir = _layout.VersionDir(program.Id, previous.Folder);
        var started = StartFrom(program, prevDir);
        _log?.Write($"rollback {program.Id} to {previous.Version}: {(started.Ok ? "ok" : "failed")}, pid {started.Pid}, {started.Message}");
        if (!started.Ok)
        {
            var back = oldDir != null && Directory.Exists(oldDir) ? StartFrom(program, oldDir) : new StartOutcome(false, null, "no current version");
            return new UpdateOutcome(UpdateKind.Failed,
                $"{previous.Version} did not start ({started.Message})" +
                (back.Ok ? "; the current version runs again" : " and the current version did not start either"),
                stoppedPids, null);
        }

        program.CurrentFolder = previous.Folder;
        program.HeldAfterRollback = true;
        return new UpdateOutcome(UpdateKind.WentBack, $"back to {previous.Version}", stoppedPids, previous.Folder);
    }

    private StartOutcome StartFrom(ProgramEntry program, string dir)
    {
        var exe = Path.Combine(dir, Path.GetFileName(program.SourceExe));
        return ProgramStart.Start(_launcher, exe, program.Arguments, dir, _timing.StartWatch);
    }
}
