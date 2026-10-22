namespace StykkerStarter.Core.Web;

// Ein Programm, wie die Seite und der Tray es sehen (Momentaufnahme, nur gelesen). Broken: der Grund, warum der
// Eintrag nicht laufen kann (null, wenn er in Ordnung ist). Die Seite bekommt die Felder in camelCase (PageJson).
// Die letzten Felder haben Vorgaben, damit bestehende Aufrufe gleich bleiben.
//   UpdateIsNewBuild: das Update ist derselbe Stand mit anderem Inhalt („new build“)
//   SourceBuiltAt:    jüngste Schreibzeit der Programmdateien der Quelle („yyyy-MM-dd HH:mm“), null wenn unbekannt
//   Previous:         Version, zu der man zurückgehen kann (null, wenn keine behaltene ältere Kopie)
//   Busy:             läuft gerade ein Vorgang für das Programm: "queued", "starting", "stopping", "restarting",
//                     "updating", "copying", "rollback" oder null
//   BusySince:        Beginn des Vorgangs (Unix-Millisekunden)
//   BusyGraceMs:      die Frist des höflichen Schließens (10 s), wenn gerade darauf gewartet wird, sonst null
//   CrashState:       null, "crashed", "restarting" oder "gaveUp"
//   Restarts:         Neustarte nach einem Absturz in den letzten zehn Minuten
//   Ready:            null (keine Prüfung), "waiting", "ready" oder "timeout"
public sealed record ProgramView(
    string Id, string Name, string Source, string Arguments, string? Local, string? SourceVersion, bool Reachable,
    string? UpdateVersion, bool Running, int[] Pids, string? Folder, bool HasCopy, bool StartWithStarter,
    bool UpdateWithoutAsking, bool KeepPrevious, bool CopyToLocal, bool ChangedSinceStart, string? Broken,
    bool UpdateIsNewBuild = false,
    string? SourceBuiltAt = null,
    string? Previous = null,
    string? Busy = null,
    long? BusySince = null,
    int? BusyGraceMs = null,
    string? CrashState = null,
    int Restarts = 0,
    string? Ready = null,
    bool RestartOnCrash = false,
    int StartDelaySeconds = 0,
    string ReadyCheck = "",
    bool HeldAfterRollback = false);
