using Stykker.Shared.Disk;
using Stykker.Shared.Gpu;
using Stykker.Shared.Net;
using Stykker.Shared.Sampling;

namespace Stykker.Hardware;

// Eine Messung der ganzen Hardware-Seite: Prozessor und Speicher mit den größten Programmen (Overview), die Grafikkarte
// über NVML (nur NVIDIA; sonst null) und über die Windows-Zähler (in Overview), die Laufwerke, die Netzwerkadapter und
// der Verlauf seit dem Öffnen der Seite (höchstens fünf Minuten). Fehlt eine Quelle, ist ihr Teil null – die Seite
// zeigt dann „–", nie eine erfundene Zahl.
public sealed record HardwareSnapshot(DateTime At, OverviewSnapshot Overview, NvidiaGpu? Nvidia, DiskSnapshot Disks,
    IReadOnlyList<NetAdapterRow>? Network, IReadOnlyList<HistoryPoint> History);

// Ein Punkt des Verlaufs. Laufwerke je Buchstabe. null heißt „nicht gemessen" (die Linie hat dort eine Lücke).
public sealed record HistoryPoint(DateTime At, double? CpuPercent, double? RamUsedGb, double? GpuPercent, double? VramUsedGb,
    double? PowerW, double? TempC, double? NetRx, double? NetTx, IReadOnlyDictionary<string, VolumeRate> Volumes);

public readonly record struct VolumeRate(double? Read, double? Write, double? Busy);

// Setzt die Messung aus den Teilen zusammen und führt den Verlauf. Die Grafikkarte aus NVML, wenn es eine NVIDIA-Karte
// gibt (genauer, mit Gesamtgröße, Leistung, Temperatur); sonst die Windows-Zähler.
public sealed class HardwareSampler : ISampler<HardwareSnapshot>
{
    public const int HistoryLength = 300;   // fünf Minuten im Sekundentakt

    private readonly IHardwareProbe _probe;
    private readonly OverviewSampler _overview;
    private readonly DiskSampler _disks;
    private readonly NetAdapters _net = new();
    private readonly Queue<HistoryPoint> _history = new();
    private readonly object _gate = new();

    public HardwareSampler(IHardwareProbe probe, IDiskProbe disks)
    {
        _probe = probe;
        _overview = new OverviewSampler(probe);
        _disks = new DiskSampler(disks);
    }

    // Nach einer Pause: neue Grundwerte. Der Verlauf beginnt neu, sonst zöge eine Linie über die Pause hinweg.
    public void Resume()
    {
        lock (_gate)
        {
            _overview.Resume();
            _disks.Resume();
            _net.Reset();
            _history.Clear();
        }
    }

    public HardwareSnapshot Sample()
    {
        lock (_gate)
        {
            var overview = _overview.Sample();
            var disks = _disks.Sample();
            var nvidia = _probe.ReadNvidia();
            var net = _net.Read();
            var point = Point(overview, nvidia, disks, net);
            _history.Enqueue(point);
            while (_history.Count > HistoryLength) _history.Dequeue();
            return new HardwareSnapshot(point.At, overview, nvidia, disks, net, _history.ToArray());
        }
    }

    // Ein Punkt des Verlaufs aus einer Messung (öffentlich für die Tests; rechnet nur)
    public static HistoryPoint Point(OverviewSnapshot overview, NvidiaGpu? nvidia, DiskSnapshot disks, IReadOnlyList<NetAdapterRow>? net)
    {
        var volumes = new Dictionary<string, VolumeRate>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in disks.Disks?.Volumes ?? Array.Empty<VolumeRow>())
            volumes[v.Letter] = new VolumeRate(v.ReadBytesPerSec, v.WriteBytesPerSec, v.BusyPercent);
        double? rx = null, tx = null;
        if (net != null)
        {
            var counted = net.Where(a => a.CountsInTotal && a.RxBytesPerSec != null).ToList();
            if (counted.Count > 0)
            {
                rx = counted.Sum(a => a.RxBytesPerSec ?? 0);
                tx = counted.Sum(a => a.TxBytesPerSec ?? 0);
            }
        }
        return new HistoryPoint(overview.At, overview.System?.CpuPercent, overview.System?.RamUsedGb,
            nvidia?.Util ?? overview.Gpu?.UtilPercent,
            nvidia?.MemUsedGb ?? (overview.Gpu?.VramUsedMb is double mb ? mb / 1024 : null),
            nvidia?.PowerW, nvidia?.TempC, rx, tx, volumes);
    }
}

// Die Messschleife der Hardware-Seite (siehe ViewerLoop): sie misst nur, solange jemand die Seite ansieht.
public sealed class HardwareMonitor : ViewerLoop<HardwareSnapshot>
{
    public HardwareMonitor(HardwareSampler sampler) : base(sampler) { }

    // Die Sonden für dieses System: unter Windows die Leistungsindikatoren und NVML, sonst (oder mit basic) keine Werte.
    public static HardwareMonitor Create(bool basic = false)
    {
        IHardwareProbe probe = new BasicHardwareProbe();
        IDiskProbe disks = new BasicDiskProbe();
        if (!basic && OperatingSystem.IsWindows())
        {
            probe = new Windows.WindowsHardwareProbe();
            disks = new Windows.WindowsDiskProbe();
        }
        return new HardwareMonitor(new HardwareSampler(probe, disks));
    }
}
