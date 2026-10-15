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
}
