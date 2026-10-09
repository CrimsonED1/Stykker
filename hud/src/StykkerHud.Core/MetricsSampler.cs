using Stykker.Shared.Gpu;
using Stykker.Shared.Sampling;

namespace StykkerHud.Core;

// Nimmt eine Messung der ganzen Maschine ab und hält einen kurzen Verlauf für die Kurven. Nichts hier ist
// Windows-spezifisch: das erledigt die Sonde. Alle Zugriffe laufen unter einem Schloss, weil die Messschleife und
// die Abfrage der Oberfläche dieselbe Instanz benutzen.
public sealed class MetricsSampler : ISampler<HudSnapshot>
{
    private const int MemoryEveryTicks = 3;   // die Abfrage über alle Grafikprozesse auf einmal ist teuer

    private readonly ISystemProbe _probe;
    private readonly int _history;
    private readonly int _cores = Environment.ProcessorCount;
    private readonly DateTime _started = DateTime.Now;
    private readonly List<HistoryPoint> _points = new();
    // Untergrenzen der Balken-Sockel: eine ruhige Maschine soll nicht schon bei wenigen MB/s voll ausschlagen.
    private static readonly IoPeaks Floors = new(25e6, 15e6, 5e6, 2e6);

    private readonly object _gate = new();
    private IReadOnlyList<GpuProcRow>? _gpuMem;
    private IoPeaks _peaks = Floors;
    private int _tick;

    public MetricsSampler(ISystemProbe probe, int history = 300)
    {
        _probe = probe;
        _history = Math.Clamp(history, 30, 3600);
    }

    // Anfang oder Wiederanfang nach einer Pause: der Verlauf beginnt neu (die Punkte davor gehören zu einem
    // anderen Zeitfenster), und die Differenz-Zähler der Sonde werden neu gesetzt.
    public void Resume()
    {
        lock (_gate)
        {
            _points.Clear();
            _gpuMem = null;
            _tick = 0;
            _peaks = Floors;
            _probe.ResetBaselines();
        }
    }

    public HudSnapshot Sample()
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            var system = _probe.ReadSystem();
            var gpu = _probe.ReadGpu();
            var util = _probe.ReadGpuUtil();
            // Der Speicher nur alle paar Takte: die Abfrage zählt jeden Grafikprozess der Maschine auf einmal durch.
            if (_tick++ % MemoryEveryTicks == 0) _gpuMem = _probe.ReadGpuMemory();
            // Ohne nvml.dll (Intel, AMD) kommt die Karte aus den Windows-Zählern. Was sie nicht liefern, bleibt -1.
            if (gpu == null && (util != null || _gpuMem != null)) gpu = FromCounters(util, _gpuMem);
            // Engines gehören zur Karte, nicht zu einem Prozess.
            if (gpu != null && util is { Engines.Count: > 0 }) gpu = gpu with { Engines = util.Engines };

            // Durchsatz lesen und den Sockel der Balken nachziehen (größter Wert der Sitzung, nie unter der Untergrenze).
            var io = _probe.ReadIo();
            _peaks = new IoPeaks(
                Peak(_peaks.DiskReadBps, io?.DiskReadBps, Floors.DiskReadBps),
                Peak(_peaks.DiskWriteBps, io?.DiskWriteBps, Floors.DiskWriteBps),
                Peak(_peaks.NetRxBps, io?.NetRxBps, Floors.NetRxBps),
                Peak(_peaks.NetTxBps, io?.NetTxBps, Floors.NetTxBps));

            _points.Add(new HistoryPoint(system?.CpuPercent ?? -1, gpu?.UtilPercent ?? -1,
                system is { RamTotalGb: > 0 } s ? 100.0 * s.RamUsedGb / s.RamTotalGb : -1));
            while (_points.Count > _history) _points.RemoveAt(0);

            var notes = new List<string>(2);
            if (system == null) notes.Add("No CPU or memory values – this build runs without system access.");
            if (gpu == null) notes.Add("No GPU values – neither nvml.dll nor the Windows GPU counters answered.");
            else if (gpu.TempC < 0) notes.Add("GPU from the Windows counters only (no nvml.dll): temperature, power, clocks and total memory are not shown.");

            return new HudSnapshot(now, Environment.MachineName, _cores, (long)(now - _started).TotalSeconds,
                system, gpu, _points.ToArray(), io, _peaks, notes.ToArray());
        }
    }

    private static double Peak(double peak, double? seen, double floor) => Math.Max(Math.Max(peak, seen ?? 0), floor);

    private GpuSample FromCounters(GpuUtilSample? util, IReadOnlyList<GpuProcRow>? mem)
    {
        // Die stärkste Engine entspricht dem, was der Task-Manager als Auslastung zeigt: Engines addieren sich nicht.
        double busiest = util is { Engines.Count: > 0 } ? util.Engines.Max(e => e.Percent) : 0;
        double usedMb = mem == null ? -1 : mem.Sum(r => r.Value);
        return new GpuSample(
            Name: _probe.AdapterName ?? "GPU",
            UtilPercent: util == null ? -1 : Math.Min(100, busiest),
            VramUsedGb: usedMb < 0 ? -1 : usedMb / 1024.0,
            VramTotalGb: -1, VramUtilPercent: -1, PowerW: -1, PowerLimitW: -1, TempC: -1,
            GfxClockMhz: -1, GfxClockMaxMhz: -1, MemClockMhz: -1, MemClockMaxMhz: -1, ThrottleReasons: 0);
    }
}
