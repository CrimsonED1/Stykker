using Stykker.Shared.Disk;
using Stykker.Shared.Sampling;

namespace StykkerSys.Core;

// Die Betriebssystem-Seite der Laufwerke: die Zahlen liefert die Windows-Sonde (Stykker.Shared: DiskCounters). Die Sonde
// ist austauschbar, damit die Messung ohne Windows prüfbar bleibt; BasicDiskProbe antwortet nichts.
public interface IDiskProbe : IDisposable
{
    // Die Laufwerke mit ihren Takt-Werten; null, wenn die Zähler fehlen.
    DiskSample? ReadDisks();

    // Die Prozesse mit dem meisten Platten-E/A (größte zuerst); null, wenn die Zähler fehlen.
    IReadOnlyList<ProcessIoRow>? ReadProcesses(int count);

    // Neuer Anfang nach einer Pause: die nächste Abtastung liefert nur Grundwerte.
    void Reset();
}

public sealed class BasicDiskProbe : IDiskProbe
{
    public DiskSample? ReadDisks() => null;
    public IReadOnlyList<ProcessIoRow>? ReadProcesses(int count) => null;
    public void Reset() { }
    public void Dispose() { }
}

// Eine Messung der Laufwerke für die Seite /disks. Notes nennt, was fehlt, statt Werte zu erfinden.
public sealed record DiskSnapshot(DateTime At, DiskSample? Disks, IReadOnlyList<ProcessIoRow> Processes, string[] Notes);

// Nimmt die Laufwerke ab: die Liste der Volumes und Datenträger bei jedem Takt, die Prozesse mit dem meisten E/A alle fünf
// Takte (die Abfrage zählt jeden Prozess einzeln). Alle Zugriffe laufen unter einem Schloss, weil die Messschleife und die
// Abfrage der Seite dieselbe Instanz nutzen.
public sealed class DiskSampler : ISampler<DiskSnapshot>
{
    private const int ProcessCount = 12;
    private const int ProcessEveryTicks = 5;

    private readonly IDiskProbe _probe;
    private readonly object _gate = new();
    private IReadOnlyList<ProcessIoRow>? _processes;
    private int _tick;

    public DiskSampler(IDiskProbe probe) => _probe = probe;

    public void Resume()
    {
        lock (_gate)
        {
            _probe.Reset();
            _processes = null;
            _tick = 0;
        }
    }

    public DiskSnapshot Sample()
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            var disks = _probe.ReadDisks();
            if (_tick++ % ProcessEveryTicks == 0) _processes = _probe.ReadProcesses(ProcessCount);

            var notes = new List<string>(2);
            if (disks == null) notes.Add("No disk values – the Windows disk counters did not answer.");
            else if (disks.Volumes.Count == 0) notes.Add("No drive letters found.");
            if (_processes == null) notes.Add("No values per program – the Windows process counters did not answer.");
            return new DiskSnapshot(now, disks, _processes ?? Array.Empty<ProcessIoRow>(), notes.ToArray());
        }
    }
}

// Die Messschleife der Laufwerkseite (siehe ViewerLoop): sie liest nur, solange jemand die Seite ansieht.
public sealed class DiskService : ViewerLoop<DiskSnapshot>
{
    public DiskService(DiskSampler sampler) : base(sampler) { }
}
