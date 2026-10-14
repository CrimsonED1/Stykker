using StykkerStarter.Core.Config;
using StykkerStarter.Core.Copy;
using StykkerStarter.Core.Source;
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
}
