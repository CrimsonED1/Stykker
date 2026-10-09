using System.Diagnostics;

namespace StykkerHud.Core;

// Nimmt eine Messung der ganzen Maschine ab und hält einen kurzen Verlauf für die Kurven. Nichts hier ist
// Windows-spezifisch: das erledigt die Sonde. Alle Zugriffe laufen unter einem Schloss, weil die Messschleife und
// die Abfrage der Oberfläche dieselbe Instanz benutzen.
public sealed class MetricsSampler
{
    private const int MemoryEveryTicks = 3;   // die Abfrage über alle Grafikprozesse auf einmal ist teuer

    private readonly ISystemProbe _probe;
    private readonly int _history;
    private readonly int _cores = Environment.ProcessorCount;
    private readonly DateTime _started = DateTime.Now;
    private readonly List<HistoryPoint> _points = new();
    private readonly Dictionary<int, (long CpuMs, DateTime At)> _prev = new();
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
            _prev.Clear();
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
            var processes = ReadProcesses(now);
            // Die Engine-Aufteilung kommt aus derselben Abfrage wie die GPU-Anteile der Prozesse.
            gpu = MergeGpu(processes, gpu);

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
                system, gpu, processes, _points.ToArray(), io, _peaks, notes.ToArray());
        }
    }

    private static double Peak(double peak, double? seen, double floor) => Math.Max(Math.Max(peak, seen ?? 0), floor);

    // Prozessorzeit je Prozess als Differenz zur letzten Messung; der erste Durchlauf liefert überall 0 %.
    private List<ProcessSample> ReadProcesses(DateTime now)
    {
        var list = new List<ProcessSample>(256);
        var alive = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                int pid = p.Id;
                alive.Add(pid);
                long cpuMs = (long)p.TotalProcessorTime.TotalMilliseconds;
                double cpu = 0;
                if (_prev.TryGetValue(pid, out var prev))
                {
                    double elapsed = (now - prev.At).TotalMilliseconds;
                    if (elapsed > 0) cpu = Math.Clamp(100.0 * (cpuMs - prev.CpuMs) / (elapsed * _cores), 0, 100);
                }
                _prev[pid] = (cpuMs, now);
                // Die Priorität einzeln abfangen: verweigert Windows sie, fällt die ganze Zeile nicht weg.
                string? priority = null;
                try { priority = p.PriorityClass.ToString(); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                list.Add(new ProcessSample(pid, p.ProcessName, cpu, p.WorkingSet64 / 1048576.0, null, null,
                    p.Threads.Count, cpuMs / 1000.0, ProcessState.Of(cpu), priority, _probe.PathOf(pid)));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                          or NotSupportedException or PlatformNotSupportedException)
            {
                // Beendet oder geschützt (viele Systemprozesse): die Zeile fehlt in diesem Durchlauf.
            }
            finally { p.Dispose(); }
        }
        foreach (var pid in _prev.Keys.Where(k => !alive.Contains(k)).ToArray()) _prev.Remove(pid);
        return list;
    }

    // Auslastung und Grafikspeicher aus den Leistungsindikatoren über die PID anhängen. Den Speicher nur alle paar
    // Takte lesen: diese Abfrage zählt jeden Grafikprozess der Maschine auf einmal durch. Das Ergebnis trägt
    // außerdem die Engine-Aufteilung der Grafikkarte.
    private GpuSample? MergeGpu(List<ProcessSample> processes, GpuSample? gpu)
    {
        var util = _probe.ReadGpuUtil();
        if (_tick++ % MemoryEveryTicks == 0) _gpuMem = _probe.ReadGpuMemory();
        var mem = _gpuMem;

        var byPid = Sum(util?.Processes);
        var memByPid = Sum(mem);
        if (byPid != null || memByPid != null)
            for (int i = 0; i < processes.Count; i++)
            {
                var p = processes[i];
                double? value = byPid != null && byPid.TryGetValue(p.Pid, out var u) ? Math.Min(100.0, u) : null;
                double? vram = memByPid != null && memByPid.TryGetValue(p.Pid, out var m) ? m : null;
                if (value != null || vram != null) processes[i] = p with { GpuPercent = value, VramMb = vram };
            }

        // Ohne nvml.dll (Intel, AMD) kommt die Karte aus den Windows-Zählern. Was sie nicht liefern, bleibt -1.
        if (gpu == null && (util != null || mem != null)) gpu = FromCounters(util, mem);

        // Engines gehören zur Karte, nicht zu einem Prozess.
        if (gpu != null && util is { Engines.Count: > 0 }) gpu = gpu with { Engines = util.Engines };
        return gpu;
    }

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

    // Ein Prozess hat mehrere Einträge (mehrere Engines, mehrere Grafikprozessoren): je PID summiert.
    private static Dictionary<int, double>? Sum(IReadOnlyList<GpuProcRow>? rows)
    {
        if (rows == null) return null;
        var byPid = new Dictionary<int, double>(rows.Count);
        foreach (var row in rows) byPid[row.Pid] = byPid.GetValueOrDefault(row.Pid) + row.Value;
        return byPid;
    }
}