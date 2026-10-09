using Stykker.Shared.Gpu;

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

// Ein Punkt des Verlaufs. Ohne Zeitstempel: die Kurve ist ein Sekundentakt, die Oberfläche braucht keine Uhrzeit.
public sealed record HistoryPoint(double Cpu, double Gpu, double Ram);

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
    IReadOnlyList<HistoryPoint> History,
    IoRates? Io,
    IoPeaks Peaks,
    string[] Notes);
