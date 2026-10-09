using System.ComponentModel;
using System.Diagnostics;
using Stykker.Shared.Gpu;
using Stykker.Shared.Sampling;

namespace StykkerSys.Core;

// Nimmt die Prozessliste ab: CPU-Zeit je Prozess als Differenz zur letzten Messung, Speicher, Threads, Priorität und –
// wo Windows es hergibt – GPU-Anteil und Grafikspeicher je PID. Nichts hier ist Windows-spezifisch: das erledigt die
// Sonde. Alle Zugriffe laufen unter einem Schloss, weil die Messschleife und die Abfrage der Oberfläche dieselbe
// Instanz benutzen.
public sealed class ProcessSampler : ISampler<SysSnapshot>
{
    private const int MemoryEveryTicks = 3;   // die Abfrage über alle Grafikprozesse auf einmal ist teuer

    private readonly IProcessProbe _probe;
    private readonly int _cores = Environment.ProcessorCount;
    private readonly DateTime _started = DateTime.Now;
    private readonly Dictionary<int, (long CpuMs, DateTime At)> _prev = new();

    private readonly object _gate = new();
    private IReadOnlyList<GpuProcRow>? _gpuMem;
    private int _tick;

    public ProcessSampler(IProcessProbe probe) => _probe = probe;

    // Anfang oder Wiederanfang nach einer Pause: die CPU-Differenzen beginnen neu (die Zeit davor gehört zu einem
    // anderen Zeitfenster), und der Grafikspeicher wird beim nächsten Takt frisch gelesen.
    public void Resume()
    {
        lock (_gate)
        {
            _prev.Clear();
            _gpuMem = null;
            _tick = 0;
        }
    }

    public SysSnapshot Sample()
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            var processes = ReadProcesses(now);
            var util = _probe.ReadGpuUtil();
            // Der Speicher nur alle paar Takte: die Abfrage zählt jeden Grafikprozess der Maschine auf einmal durch.
            if (_tick++ % MemoryEveryTicks == 0) _gpuMem = _probe.ReadGpuMemory();
            AttachGpu(processes, util, _gpuMem);

            var notes = new List<string>(1);
            if (util == null && _gpuMem == null)
                notes.Add("No GPU values per process – the Windows GPU counters did not answer.");
            return new SysSnapshot(now, (long)(now - _started).TotalSeconds, processes, notes.ToArray());
        }
    }

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
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
                list.Add(new ProcessSample(pid, p.ProcessName, cpu, p.WorkingSet64 / 1048576.0, null, null,
                    p.Threads.Count, cpuMs / 1000.0, ProcessState.Of(cpu), priority, _probe.PathOf(pid)));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception
                                          or NotSupportedException or PlatformNotSupportedException)
            {
                // Beendet oder geschützt (viele Systemprozesse): die Zeile fehlt in diesem Durchlauf.
            }
            finally { p.Dispose(); }
        }
        foreach (var pid in _prev.Keys.Where(k => !alive.Contains(k)).ToArray()) _prev.Remove(pid);
        return list;
    }

    // Auslastung und Grafikspeicher über die PID anhängen. Ein Prozess hat mehrere Einträge (mehrere Engines, mehrere
    // Grafikprozessoren): je PID summiert. Was die Zähler nicht nennen, bleibt null – nicht 0.
    private static void AttachGpu(List<ProcessSample> processes, GpuUtilSample? util, IReadOnlyList<GpuProcRow>? mem)
    {
        var byPid = Sum(util?.Processes);
        var memByPid = Sum(mem);
        if (byPid == null && memByPid == null) return;
        for (int i = 0; i < processes.Count; i++)
        {
            var p = processes[i];
            double? value = byPid != null && byPid.TryGetValue(p.Pid, out var u) ? Math.Min(100.0, u) : null;
            double? vram = memByPid != null && memByPid.TryGetValue(p.Pid, out var m) ? m : null;
            if (value != null || vram != null) processes[i] = p with { GpuPercent = value, VramMb = vram };
        }
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
