using Stykker.Shared.Gpu;

namespace StykkerSys.Core;

// Die Betriebssystem-Seite der Prozessliste. WindowsProcessProbe liest die Zähler; BasicProcessProbe antwortet nichts,
// damit die Liste auch ohne sie läuft (und überall außerhalb von Windows).
public interface IProcessProbe : IDisposable
{
    // Auslastung je Prozess und je Engine. null, wenn die Zähler fehlen – dann bleibt die GPU-Spalte leer.
    GpuUtilSample? ReadGpuUtil();

    // Grafikspeicher je Prozess in MB. null, wenn die Zähler fehlen.
    IReadOnlyList<GpuProcRow>? ReadGpuMemory();

    // Die Programmdatei eines Prozesses. Wird für jede Zeile jedes Takts gefragt und muss deshalb billig sein;
    // null, wenn Windows sie verweigert (geschützte Prozesse).
    string? PathOf(int pid);
}

public sealed class BasicProcessProbe : IProcessProbe
{
    public GpuUtilSample? ReadGpuUtil() => null;
    public IReadOnlyList<GpuProcRow>? ReadGpuMemory() => null;
    public string? PathOf(int pid) => null;
    public void Dispose() { }
}
