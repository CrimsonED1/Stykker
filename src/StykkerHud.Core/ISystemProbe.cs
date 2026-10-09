namespace StykkerHud.Core;

// Eine Zeile eines Leistungsindikators für genau einen Prozess: Auslastung in Prozent oder Grafikspeicher in MB.
public sealed record GpuProcRow(int Pid, double Value);

// Ein Takt des GPU-Engine-Zählers: die Auslastung je Prozess und je Engine. Beides stammt aus derselben Abfrage
// und wird deshalb zusammen gelesen – eine zweite Abtastung desselben Raten-Zählers im selben Takt gäbe null.
public sealed record GpuUtilSample(IReadOnlyList<GpuProcRow> Processes, IReadOnlyList<GpuEngineRow> Engines);

// Die Betriebssystem-Seite der Messung. WindowsProbe liest echte Zähler; BasicProbe antwortet nichts, damit die
// Anzeige auch ohne diese Zähler startet (und überall außerhalb von Windows).
public interface ISystemProbe : IDisposable
{
    bool GpuAvailable { get; }
    SystemSample? ReadSystem();
    GpuSample? ReadGpu();
    GpuUtilSample? ReadGpuUtil();
    IReadOnlyList<GpuProcRow>? ReadGpuMemory();

    // Datenträger- und Netzwerk-Durchsatz. null, solange die Quelle keine Rate liefern kann.
    IoRates? ReadIo();

    // Name der Grafikkarte, wenn nvml.dll sie nicht nennt (Anzeige ohne NVIDIA). null, wenn unbekannt.
    string? AdapterName { get; }

    // Die Programmdatei eines Prozesses. Wird für jede Zeile jedes Takts gefragt und muss deshalb billig sein;
    // null, wenn Windows sie verweigert (geschützte Prozesse).
    string? PathOf(int pid);

    // Nach einer Pause: die Differenz-Zähler (CPU-Zeiten, Durchsatz) neu aufsetzen, sonst wäre der erste Wert
    // über die ganze Pause gemittelt.
    void ResetBaselines();
}

public sealed class BasicProbe : ISystemProbe
{
    public bool GpuAvailable => false;
    public SystemSample? ReadSystem() => null;
    public GpuSample? ReadGpu() => null;
    public GpuUtilSample? ReadGpuUtil() => null;
    public IReadOnlyList<GpuProcRow>? ReadGpuMemory() => null;
    public IoRates? ReadIo() => null;
    public string? AdapterName => null;
    public string? PathOf(int pid) => null;
    public void ResetBaselines() { }
    public void Dispose() { }
}