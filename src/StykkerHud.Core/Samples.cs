namespace StykkerHud.Core;

// Eine Messung der ganzen Maschine. Fehlt ein Wert, steht dort null – die Oberfläche zeigt dann „–", nie eine
// erfundene Zahl. Verlaufspunkte nutzen -1 als „nicht gemessen", weil JSON kein NaN kennt.
public sealed record SystemSample(
    double CpuPercent,
    int Cores,
    double RamUsedGb,
    double RamTotalGb,
    double CommitUsedGb,
    double CommitTotalGb,
    double[]? CoreLoads);

// Die Auslastung einer Engine der Grafikkarte über alle Prozesse („GPU Engine" der Leistungsindikatoren,
// dieselbe Quelle wie die GPU-Anteile der Prozessliste). Engine ist die Art – „3D", „Compute", „Copy",
// „Video decode", „Video encode". Mehrere Engines können gleichzeitig laufen, die Summe darf also über 100 %
// liegen.
public sealed record GpuEngineRow(string Engine, double Percent);

// Ohne nvml.dll (Intel, AMD) kommen Auslastung, Engines und Speicher aus den Windows-Zählern; was dort fehlt, steht
// auf -1 („nicht gemessen"), genau wie bei den Verlaufspunkten.
public sealed record GpuSample(
    string Name,
    double UtilPercent,
    double VramUsedGb,
    double VramTotalGb,
    double VramUtilPercent,
    double PowerW,
    double PowerLimitW,
    double TempC,
    double GfxClockMhz,
    double GfxClockMaxMhz,
    double MemClockMhz,
    double MemClockMaxMhz,
    ulong ThrottleReasons,
    // Auslastung je Engine: nvml.dll kennt sie nicht, sie kommt aus den Leistungsindikatoren dazu und fehlt
    // deshalb, wo es diese Zähler nicht gibt.
    IReadOnlyList<GpuEngineRow>? Engines = null);

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

public sealed record HistoryPoint(DateTime At, double Cpu, double Gpu, double Ram);

// Durchsatz in Byte je Sekunde: Datenträger und Netzwerk der ganzen Maschine.
public sealed record IoRates(double DiskReadBps, double DiskWriteBps, double NetRxBps, double NetTxBps);

// Der Sockel der Balken: der größte Wert der laufenden Sitzung, mit einer Untergrenze, damit eine ruhige Maschine
// nicht schon bei wenigen MB/s voll ausschlägt. Der Balken zeigt also „aktuell von bisher am meisten".
public sealed record IoPeaks(double DiskReadBps, double DiskWriteBps, double NetRxBps, double NetTxBps);

public sealed record HudSnapshot(
    DateTime At,
    string Host,
    int Cores,
    long UptimeSeconds,
    SystemSample? System,
    GpuSample? Gpu,
    IReadOnlyList<ProcessSample> Processes,
    IReadOnlyList<HistoryPoint> History,
    IoRates? Io,
    IoPeaks Peaks,
    string[] Notes);