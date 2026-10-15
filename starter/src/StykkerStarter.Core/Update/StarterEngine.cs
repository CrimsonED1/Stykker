using StykkerStarter.Core.Config;
using StykkerStarter.Core.Copy;
using StykkerStarter.Core.Source;
using StykkerStarter.Core.Start;
using StykkerStarter.Core.Versions;

namespace StykkerStarter.Core.Update;

public enum PolicyDecision
{
    Nothing,    // nur anzeigen, dass es eine neue Version gibt
    Ask,        // dasselbe, mit dem Hinweis „Update“ – der Benutzer entscheidet
    UpdateNow,  // ohne zu fragen kopieren und umschalten
}

// Wann ein neuer Stand ohne Rückfrage übernommen wird. Laufende Programme werden nur ohne Rückfrage aktualisiert,
// wenn die Einstellung „Ask before updating a running program“ aus ist oder das Programm es selbst so will.
public static class UpdatePolicy
{
    public static PolicyDecision Decide(StarterSettings settings, ProgramEntry program, bool running)
    {
        if (program.UpdateWithoutAsking) return PolicyDecision.UpdateNow;
        if (running) return settings.AskBeforeUpdate ? PolicyDecision.Ask : PolicyDecision.UpdateNow;
        return settings.UpdateStoppedWithoutAsking ? PolicyDecision.UpdateNow : PolicyDecision.Ask;
    }
}

public enum CheckKind
{
    UpToDate,          // die Quelle ist nichts Neues
    UpdateAvailable,   // neuer Stand, wartet auf den Benutzer
    ChangedSinceStart, // ohne Kopie: die Quelle ist anders als beim Start; es wird nichts beendet oder gestartet
    Updated,           // neuer Stand, ohne Rückfrage übernommen (siehe Outcome)
    Unreachable,       // die Quelle ist nicht erreichbar; das Programm läuft aus der letzten Kopie weiter
    Failed,            // Prüfung oder Kopie hat sich nicht durchführen lassen
}

public sealed record CheckResult(CheckKind Kind, string Message, string? SourceVersion, CopyPlan? Plan, UpdateOutcome? Outcome);

// Prüft die Quelle eines Programms: gibt es einen neuen Stand, und darf er ohne Rückfrage übernommen werden?
// Die Kopie und das Umschalten macht ProgramService; diese Klasse entscheidet nur.
public sealed class StarterEngine
{
    private readonly DataLayout _layout;
    private readonly IFileVersionReader _versions;
    private readonly ProgramService _service;
    private readonly StarterLog? _log;

    // Ohne Kopie: der Stand, den die letzte Prüfung gesehen hat (je Programm). Ein neuer Stand gilt erst, wenn zwei
    // Prüfungen hintereinander dasselbe sehen: ein Build schreibt seine Dateien nicht auf einmal.
    private readonly Dictionary<string, (string Version, string ListHash)> _seen = new();

    public StarterEngine(DataLayout layout, IFileVersionReader versions, ProgramService service, StarterLog? log = null)
    {
        _layout = layout;
        _versions = versions;
        _service = service;
        _log = log;
    }

    // forceUpdate: der Benutzer hat „Update“ gedrückt; dann wird übernommen, egal was die Einstellungen sagen
    public CheckResult Check(ProgramEntry program, StarterSettings settings, IProgress<CopyProgress>? progress = null,
        CancellationToken ct = default, bool forceUpdate = false)
    {
        if (!program.CopyToLocal) return CheckInPlace(program, settings, forceUpdate);

        SourceTree source;
        try
        {
            source = SourceTree.Read(program.SourceExe);
        }
        catch (SourceUnreachableException ex)
        {
            _log?.Write($"check {program.Id}: source not reachable: {ex.Message}");
            return new CheckResult(CheckKind.Unreachable, "the source is not reachable; the local copy keeps running", null, null, null);
        }

        CopyPlan plan;
        try
        {
            plan = VersionPlanner.Plan(program, source, _versions.Read(program.SourceExe), _layout);
        }
        catch (InvalidOperationException ex)
        {
            _log?.Write($"check {program.Id}: {ex.Message}");
            return new CheckResult(CheckKind.Failed, ex.Message, null, null, null);
        }

        if (plan.Kind == PlanKind.UpToDate)
            return new CheckResult(CheckKind.UpToDate, "up to date", plan.Version, plan, null);

        bool running = _service.IsRunning(program);
        var decision = forceUpdate ? PolicyDecision.UpdateNow : UpdatePolicy.Decide(settings, program, running);
        if (decision != PolicyDecision.UpdateNow)
        {
            _log?.Write($"check {program.Id}: {plan.Version} available");
            return new CheckResult(CheckKind.UpdateAvailable, $"{plan.Version} is ready", plan.Version, plan, null);
        }

        var outcome = _service.Update(program, source, plan, progress, ct);
        _log?.Write($"update {program.Id} to {plan.Version}: {outcome.Kind}, {outcome.Message}");
        return new CheckResult(CheckKind.Updated, outcome.Message, plan.Version, plan, outcome);
    }

    // Erste Einrichtung eines Programms: lesen, kopieren, starten
    public UpdateOutcome Install(ProgramEntry program, IProgress<CopyProgress>? progress = null, CancellationToken ct = default)
    {
        var source = SourceTree.Read(program.SourceExe);
        var plan = VersionPlanner.Plan(program, source, _versions.Read(program.SourceExe), _layout);
        return _service.Install(program, source, plan, progress, ct);
    }

    // Ohne Kopie: startet das Programm aus dem Quellordner und merkt sich den Stand, mit dem es startet. Läuft es
    // schon, bleibt der gemerkte Stand, wie er ist; ein neuer Build wird dann über die Prüfung erkannt.
    public StartOutcome StartInPlace(ProgramEntry program)
    {
        if (_service.IsRunning(program)) return _service.StartCurrent(program);

        SourceTree source;
        try
        {
            source = SourceTree.Read(program.SourceExe);
        }
        catch (SourceUnreachableException)
        {
            return new StartOutcome(false, null, ProgramService.SourceFolderUnreachable);
        }

        var version = VersionPlanner.VersionOf(source, _versions.Read(program.SourceExe));
        var outcome = _service.StartCurrent(program);
        if (outcome.Ok)
        {
            program.StartedVersion = version;
            program.StartedListHash = source.ListHash;
            _seen.Remove(program.Id);
        }
        return outcome;
    }

    // Ohne Kopie: ist die Quelle anders als beim Start, und das zweimal hintereinander, wird neu gestartet (wenn das
    // Programm läuft und die Einstellung es erlaubt, sonst wird nur angeboten). Läuft es nicht, bleibt es beim Status.
    private CheckResult CheckInPlace(ProgramEntry program, StarterSettings settings, bool forceUpdate)
    {
        SourceTree source;
        try
        {
            source = SourceTree.Read(program.SourceExe);
        }
        catch (SourceUnreachableException ex)
        {
            _log?.Write($"check {program.Id}: source not reachable: {ex.Message}");
            _seen.Remove(program.Id);
            return new CheckResult(CheckKind.Unreachable, ProgramService.SourceFolderUnreachable, null, null, null);
        }

        var version = VersionPlanner.VersionOf(source, _versions.Read(program.SourceExe));
        var stand = (version, source.ListHash);
        if (program.StartedListHash == source.ListHash && program.StartedVersion == version)
        {
            _seen.Remove(program.Id);
            return new CheckResult(CheckKind.UpToDate, "up to date", version, null, null);
        }

        bool stable = _seen.TryGetValue(program.Id, out var before) && before == stand;
        _seen[program.Id] = stand;
        bool running = _service.IsRunning(program);
        if (!forceUpdate && (!stable || !running))
        {
            _log?.Write($"check {program.Id}: {version} differs from the start" +
                        (stable ? "; not running, so the next start takes it" : "; checking again at the next check"));
            return new CheckResult(CheckKind.ChangedSinceStart, "changed since last start", version, null, null);
        }

        var decision = forceUpdate ? PolicyDecision.UpdateNow : UpdatePolicy.Decide(settings, program, running: true);
        if (decision != PolicyDecision.UpdateNow)
        {
            _log?.Write($"check {program.Id}: {version} available, restart waits for the user");
            return new CheckResult(CheckKind.UpdateAvailable, $"{version} is ready", version, null, null);
        }

        var outcome = _service.RestartInPlace(program, version, source.ListHash);
        _seen.Remove(program.Id);
        _log?.Write($"restart {program.Id} with {version}: {outcome.Kind}, {outcome.Message}");
        return new CheckResult(CheckKind.Updated, outcome.Message, version, null, outcome);
    }
}
