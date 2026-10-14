namespace StykkerStarter.Core.Processes;

// Ein Prozess mit seinem Programmpfad. Der Pfad ist null, wenn Windows ihn nicht herausgibt (fremde, geschützte
// Prozesse). Ein Prozess ohne Pfad gehört nie zu einem Ordner.
public sealed record ProcessInfo(int Pid, string? ImagePath);

// Die Prozesse des Rechners, hinter einer Schnittstelle: Windows liefert sie in Stykker.Starter.Platform.Windows,
// Tests liefern Fakes. Beendet wird immer über die PID eines Prozesses, den die Liste gerade gezeigt hat – nie über
// den Namen.
public interface IProcessSystem
{
    IReadOnlyList<ProcessInfo> List();

    bool IsRunning(int pid);

    // Die höfliche Bitte (Hauptfenster schließen). False, wenn es kein Hauptfenster gibt oder Windows es ablehnt.
    bool CloseMainWindow(int pid);

    void Kill(int pid);
}
