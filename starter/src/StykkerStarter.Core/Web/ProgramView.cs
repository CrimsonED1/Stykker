namespace StykkerStarter.Core.Web;

// Ein Programm, wie die Seite und der Tray es sehen (Momentaufnahme, nur gelesen). Broken: der Grund, warum der
// Eintrag nicht laufen kann (null, wenn er in Ordnung ist). Die Seite bekommt die Felder in camelCase (PageJson).
public sealed record ProgramView(
    string Id, string Name, string Source, string Arguments, string? Local, string? SourceVersion, bool Reachable,
    string? UpdateVersion, bool Running, int[] Pids, string? Folder, bool HasCopy, bool StartWithStarter,
    bool UpdateWithoutAsking, bool KeepPrevious, bool CopyToLocal, bool ChangedSinceStart, string? Broken);
