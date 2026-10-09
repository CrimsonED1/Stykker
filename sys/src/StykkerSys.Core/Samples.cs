namespace StykkerSys.Core;

// Eine Zeile der Prozessliste. GPU-Anteil und Grafikspeicher kommen aus den Leistungsindikatoren und werden über
// die PID angehängt; ohne diese Zähler bleiben beide null.
public sealed record ProcessSample(
    int Pid,
    string Name,
    double CpuPercent,
    double RamMb,
    double? GpuPercent,
    double? VramMb,
    int Threads,
    double CpuSeconds,
    string State,
    string? Priority,
    string? Path);

// State trägt einen der Namen, die das Design-System für Zustandszeichen kennt: idle, read, gen.
public static class ProcessState
{
    public const string Idle = "idle", Read = "read", Gen = "gen";

    public static string Of(double cpuPercent) => cpuPercent >= 25 ? Gen : cpuPercent >= 2 ? Read : Idle;
}

// Eine Messung der Prozessliste. Notes nennt, was fehlt, statt eine Spalte mit erfundenen Werten zu füllen.
public sealed record SysSnapshot(DateTime At, long UptimeSeconds, IReadOnlyList<ProcessSample> Processes, string[] Notes);
