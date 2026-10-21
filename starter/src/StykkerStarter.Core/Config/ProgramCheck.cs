using StykkerStarter.Core.Ready;

namespace StykkerStarter.Core.Config;

// Ein Eintrag, den der Starter nicht ausführen kann (von Hand geändert oder unvollständig). Er wird trotzdem gezeigt,
// mit dem Grund, und lässt sich entfernen; die übrigen Einträge bleiben davon unberührt.
public static class ProgramCheck
{
    // null, wenn der Eintrag in Ordnung ist; sonst der Grund in englischer Sprache (die Oberfläche ist englisch)
    public static string? BrokenReason(ProgramEntry program)
    {
        if (!DataLayout.IsPlainName(program.Id)) return "the id in starter.json is not a plain name";
        if (string.IsNullOrWhiteSpace(program.Name)) return "no name in starter.json";
        if (string.IsNullOrWhiteSpace(program.SourceExe)) return "no source file in starter.json";
        return null;
    }

    // Derselbe Eintrag: gleiche Programmdatei (voller Pfad, Groß/Kleinschreibung egal) und gleiche Argumente (getrimmt).
    // Ein Eintrag, dessen Pfad nicht gültig ist, gleicht nichts. exceptId: der Eintrag, der selbst geändert wird.
    public static ProgramEntry? FindDuplicate(StarterConfig config, string sourceExe, string? arguments, string? exceptId = null)
    {
        var full = FullPath(sourceExe);
        if (full == null) return null;
        var args = (arguments ?? "").Trim();
        return config.Programs.FirstOrDefault(p => p.Id != exceptId
            && FullPath(p.SourceExe) is { } other && string.Equals(other, full, StringComparison.OrdinalIgnoreCase)
            && string.Equals((p.Arguments ?? "").Trim(), args, StringComparison.Ordinal));
    }

    private static string? FullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
    }
}

// Die Werte, die „Bearbeiten“ setzen kann (alles außer Id und Programmdatei)
public sealed record ProgramChanges(
    string Name, string Arguments, bool StartWithStarter, bool UpdateWithoutAsking, bool KeepPrevious, bool CopyToLocal,
    bool RestartOnCrash, int StartDelaySeconds, string ReadyCheck);

public static class ProgramEdit
{
    public const int MaxStartDelaySeconds = 120;

    // null, wenn die Änderung gilt; sonst der Grund in englischer Sprache (für die Meldung)
    public static string? Problem(StarterConfig config, ProgramEntry program, ProgramChanges changes)
    {
        if (string.IsNullOrWhiteSpace(changes.Name)) return "the name is empty";
        if (changes.StartDelaySeconds is < 0 or > MaxStartDelaySeconds) return "the start delay must be 0 to 120 seconds";
        if (ReadyRule.Problem(changes.ReadyCheck) is { } ready) return "the ready check: " + ready;
        if (ProgramCheck.FindDuplicate(config, program.SourceExe, changes.Arguments, program.Id) is { } other)
            return $"{other.Name} is already in the list";
        return null;
    }

    // Die Änderung auf den Eintrag anwenden (nach Problem)
    public static void Apply(ProgramEntry program, ProgramChanges changes)
    {
        program.Name = changes.Name.Trim();
        program.Arguments = (changes.Arguments ?? "").Trim();
        program.StartWithStarter = changes.StartWithStarter;
        program.UpdateWithoutAsking = changes.UpdateWithoutAsking;
        program.KeepPrevious = changes.KeepPrevious;
        program.CopyToLocal = changes.CopyToLocal;
        program.RestartOnCrash = changes.RestartOnCrash;
        program.StartDelaySeconds = changes.StartDelaySeconds;
        program.ReadyCheck = (changes.ReadyCheck ?? "").Trim();
    }
}

// Die Reihenfolge der Programme (starter.json; die Reihenfolge der Liste ist die Startreihenfolge)
public static class ProgramList
{
    // Verschiebt den Eintrag auf die Position index (wird auf die Liste begrenzt). false, wenn es ihn nicht gibt.
    public static bool Move(StarterConfig config, string id, int index)
    {
        int from = config.Programs.FindIndex(p => p.Id == id);
        if (from < 0) return false;
        var item = config.Programs[from];
        config.Programs.RemoveAt(from);
        config.Programs.Insert(Math.Clamp(index, 0, config.Programs.Count), item);
        return true;
    }

    // Die Einträge, die starten können, in der Reihenfolge der Liste (kaputte Einträge fallen weg)
    public static IReadOnlyList<ProgramEntry> Startable(StarterConfig config) =>
        config.Programs.Where(p => ProgramCheck.BrokenReason(p) == null).ToList();
}
