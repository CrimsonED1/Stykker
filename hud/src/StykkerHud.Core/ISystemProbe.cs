using Stykker.Shared.Gpu;

namespace StykkerHud.Core;

// Die Betriebssystem-Seite der Messung. WindowsProbe liest echte Zähler; BasicProbe antwortet nichts, damit die
// Anzeige auch ohne diese Zähler startet (und überall außerhalb von Windows).
public interface ISystemProbe : IDisposable
{
    bool GpuAvailable { get; }
    SystemSample? ReadSystem();
    GpuSample? ReadGpu();
    GpuUtilSample? ReadGpuUtil();

    // Grafikspeicher je Prozess in MB. Die Anzeige braucht davon nur die Summe (ohne nvml.dll).
    IReadOnlyList<GpuProcRow>? ReadGpuMemory();

    // Datenträger- und Netzwerk-Durchsatz. null, solange die Quelle keine Rate liefern kann.
    IoRates? ReadIo();

    // Name der Grafikkarte, wenn nvml.dll sie nicht nennt (Anzeige ohne NVIDIA). null, wenn unbekannt.
    string? AdapterName { get; }

    // Nach einer Pause: die Differenz-Zähler (Durchsatz) neu aufsetzen, sonst wäre der erste Wert
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
    public void ResetBaselines() { }
    public void Dispose() { }
}
