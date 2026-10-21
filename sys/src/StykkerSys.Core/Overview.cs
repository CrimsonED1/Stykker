using System.ComponentModel;
using System.Diagnostics;
using Stykker.Shared.Gpu;
using Stykker.Shared.Sampling;
using Stykker.Shared.Windows;

namespace StykkerSys.Core;

// Die Übersicht (Seite /overview): Prozessor und Arbeitsspeicher des ganzen Rechners, die GPU und die größten Programme.
// Die Laufwerke kommen aus der Messschleife der Laufwerkseite (DiskService); /api/overview liest beide. Die Sonde
// (IHardwareProbe) liefert die Zähler von außen; die Windows-Sonde steht in StykkerSys.Platform.Windows.
public interface IHardwareProbe : IDisposable
{
    // Gesamtwerte des Prozessors und des Speichers; null, wenn Windows sie nicht nennt.
    SystemLoad? ReadSystem();

    // Auslastung je Prozess und je Engine. null, wenn die Zähler fehlen.
    GpuUtilSample? ReadGpuUtil();

    // Grafikspeicher je Prozess in MB (dediziert). null, wenn der Zähler fehlt.
    IReadOnlyList<GpuProcRow>? ReadGpuMemory();

    // Grafikspeicher, der auf allen Adaptern dediziert belegt ist, in MB. null, wenn der Zähler fehlt.
    double? ReadGpuMemoryUsedMb();
}

// Ohne Windows oder mit --basic: keine Zahl, überall „–".
public sealed class BasicHardwareProbe : IHardwareProbe
{
    public SystemLoad? ReadSystem() => null;
    public GpuUtilSample? ReadGpuUtil() => null;
    public IReadOnlyList<GpuProcRow>? ReadGpuMemory() => null;
    public double? ReadGpuMemoryUsedMb() => null;
    public void Dispose() { }
}

// Prozessor und Speicher des ganzen Rechners. CpuPercent ist null, solange noch keine zweite Messung vorliegt.
public sealed record OverviewSystem(double? CpuPercent, int Cores, double[]? CoreLoads, double RamUsedGb, double RamTotalGb,
    double CommitUsedGb, double CommitLimitGb);

// Die GPU. UtilPercent ist die Auslastung der höchsten Engine (Engines überlappen sich, eine Summe ergäbe mehr als 100 %).
// VramUsedMb: belegter Grafikspeicher; eine Gesamtgröße nennen die Zähler nicht, deshalb gibt es keine.
public sealed record OverviewGpu(double? UtilPercent, IReadOnlyList<GpuEngineRow> Engines, double? VramUsedMb,
    IReadOnlyList<GpuProgramRow> Programs);

// Ein Programm: alle Prozesse mit gleichem Namen. CpuPercent ist der Anteil am ganzen Rechner, null bei noch keiner Messung.
public sealed record ProgramLoad(string Name, int Count, double RamMb, double? CpuPercent);

// Ein Programm auf der GPU: Auslastung in Prozent und Grafikspeicher in MB, je null, wenn der Zähler nichts nennt.
public sealed record GpuProgramRow(string Name, int Count, double? GpuPercent, double? VramMb);

// Ein Prozess, wie die Übersicht ihn liest (ohne Pfad und Priorität).
public sealed record ProcessMeasure(int Pid, string Name, double RamMb, double? CpuPercent);

// Eine Messung der Übersicht. Notes nennt, was fehlt, statt Werte zu erfinden.
public sealed record OverviewSnapshot(DateTime At, OverviewSystem? System, OverviewGpu? Gpu,
    IReadOnlyList<ProgramLoad> TopByRam, IReadOnlyList<ProgramLoad> TopByCpu, string[] Notes);

// Die Antwort von /api/overview: die Übersicht und die Laufwerke, aus einer Abfrage.
public sealed record OverviewResponse(OverviewSnapshot Hardware, DiskSnapshot Disks);

// Die Umrechnung der Übersicht ohne Windows-Aufrufe (testbar mit Testdaten).
public static class OverviewMath
{
    // Gleichnamige Prozesse zu einem Programm. PID 4 und darunter (System und Dienste des Kerns) bleiben weg. Der
    // Prozessor-Anteil zählt nur die gemessenen Prozesse; sind es keine, bleibt er null.
    public static IReadOnlyList<ProgramLoad> ByName(IEnumerable<ProcessMeasure> processes)
    {
        var groups = new Dictionary<string, (int Count, double Ram, double Cpu, int Measured)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in processes)
        {
            if (p.Pid <= 4 || string.IsNullOrEmpty(p.Name)) continue;
            var g = groups.GetValueOrDefault(p.Name);
            groups[p.Name] = (g.Count + 1, g.Ram + p.RamMb, g.Cpu + (p.CpuPercent ?? 0), g.Measured + (p.CpuPercent is null ? 0 : 1));
        }
        return groups.Select(kv => new ProgramLoad(kv.Key, kv.Value.Count, kv.Value.Ram,
            kv.Value.Measured > 0 ? kv.Value.Cpu : null)).ToList();
    }

    // Die größten Programme nach Arbeitsspeicher, größte zuerst.
    public static IReadOnlyList<ProgramLoad> TopByRam(IEnumerable<ProgramLoad> programs, int count) =>
        programs.OrderByDescending(p => p.RamMb).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(count).ToList();

    // Die größten Programme nach Prozessorzeit. Programme ohne Messung fehlen.
    public static IReadOnlyList<ProgramLoad> TopByCpu(IEnumerable<ProgramLoad> programs, int count) =>
        programs.Where(p => p.CpuPercent is not null)
                .OrderByDescending(p => p.CpuPercent).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(count).ToList();

    // Die Auslastung der GPU gesamt: die höchste Engine. Ohne Zähler null; mit Zähler, aber ohne Engine-Zeile, 0 (keine Last).
    public static double? UtilTotal(GpuUtilSample? util) =>
        util == null ? null : util.Engines.Count == 0 ? 0 : Math.Min(100, util.Engines.Max(e => e.Percent));

    // Die GPU-Werte je Programm. Ein Prozess ohne Namen (schon beendet) fällt weg. Sortiert nach Grafikspeicher, dann
    // nach Auslastung; Programme ohne Speicherwert kommen nach hinten.
    public static IReadOnlyList<GpuProgramRow> GpuByName(GpuUtilSample? util, IReadOnlyList<GpuProcRow>? memory,
        IReadOnlyDictionary<int, string> names, int count)
    {
        var byUtil = Sum(util?.Processes);
        var byMem = Sum(memory);
        var pids = new HashSet<int>();
        if (byUtil != null) pids.UnionWith(byUtil.Keys);
        if (byMem != null) pids.UnionWith(byMem.Keys);

        var groups = new Dictionary<string, (int Count, double Gpu, bool HasGpu, double Vram, bool HasVram)>(StringComparer.OrdinalIgnoreCase);
        foreach (int pid in pids)
        {
            if (!names.TryGetValue(pid, out var name) || string.IsNullOrEmpty(name)) continue;
            var g = groups.GetValueOrDefault(name);
            double u = 0, m = 0;
            bool hasGpu = byUtil != null && byUtil.TryGetValue(pid, out u);
            bool hasVram = byMem != null && byMem.TryGetValue(pid, out m);
            groups[name] = (g.Count + 1,
                g.Gpu + (hasGpu ? u : 0), g.HasGpu || hasGpu,
                g.Vram + (hasVram ? m : 0), g.HasVram || hasVram);
        }
        return groups.Select(kv => new GpuProgramRow(kv.Key, kv.Value.Count,
                kv.Value.HasGpu ? Math.Min(100, kv.Value.Gpu) : null,
                kv.Value.HasVram ? kv.Value.Vram : null))
            .OrderByDescending(r => r.VramMb ?? -1).ThenByDescending(r => r.GpuPercent ?? -1)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Take(count).ToList();
    }

    // Eine Zeile je PID summiert (ein Prozess hat mehrere Engines, mehrere Grafikprozessoren).
    private static Dictionary<int, double>? Sum(IReadOnlyList<GpuProcRow>? rows)
    {
        if (rows == null) return null;
        var byPid = new Dictionary<int, double>(rows.Count);
        foreach (var row in rows) byPid[row.Pid] = byPid.GetValueOrDefault(row.Pid) + row.Value;
        return byPid;
    }
}

// Nimmt die Übersicht ab. Prozessor-Anteile der Programme brauchen zwei Messungen: der erste Takt nach einem Start
// liefert dafür nichts (null), wie die Prozessliste. Alle Zugriffe laufen unter einem Schloss, weil die Messschleife und
// die Abfrage der Seite dieselbe Instanz nutzen.
public sealed class OverviewSampler : ISampler<OverviewSnapshot>
{
    private const int ProgramCount = 8;
    private const int MemoryEveryTicks = 3;   // die Abfragen über alle Grafikprozesse sind teuer

    private readonly IHardwareProbe _probe;
    private readonly int _cores = Environment.ProcessorCount;
    private readonly Dictionary<int, (long CpuMs, DateTime At)> _prev = new();
    private readonly object _gate = new();
    private IReadOnlyList<GpuProcRow>? _gpuMem;
    private double? _vramUsedMb;
    private int _tick;

    public OverviewSampler(IHardwareProbe probe) => _probe = probe;

    // Anfang oder Wiederanfang nach einer Pause: die Zeitdifferenzen beginnen neu.
    public void Resume()
    {
        lock (_gate)
        {
            _prev.Clear();
            _gpuMem = null;
            _vramUsedMb = null;
            _tick = 0;
        }
    }

    public OverviewSnapshot Sample()
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            bool first = _tick == 0;
            var load = _probe.ReadSystem();
            var util = _probe.ReadGpuUtil();
            if (_tick % MemoryEveryTicks == 0)
            {
                _gpuMem = _probe.ReadGpuMemory();
                _vramUsedMb = _probe.ReadGpuMemoryUsedMb();
            }
            var processes = ReadProcesses(now, out int unreadable);
            _tick++;

            OverviewSystem? system = load == null ? null
                : new OverviewSystem(first ? null : load.CpuPercent, load.Cores, load.CoreLoads,
                    load.RamUsedGb, load.RamTotalGb, load.CommitUsedGb, load.CommitLimitGb);

            var names = new Dictionary<int, string>(processes.Count);
            foreach (var p in processes) names[p.Pid] = p.Name;
            var programs = OverviewMath.ByName(processes);

            bool gpuAnswered = util != null || _gpuMem != null || _vramUsedMb != null;
            OverviewGpu? gpu = gpuAnswered
                ? new OverviewGpu(OverviewMath.UtilTotal(util), util?.Engines ?? Array.Empty<GpuEngineRow>(), _vramUsedMb,
                    OverviewMath.GpuByName(util, _gpuMem, names, ProgramCount))
                : null;

            var notes = new List<string>(3);
            if (system == null) notes.Add("No processor and memory values – the Windows counters did not answer.");
            if (!gpuAnswered) notes.Add("No GPU values – the Windows GPU counters did not answer.");
            if (unreadable > 0)
                notes.Add($"Not listed: {unreadable} of {processes.Count + unreadable} processes. Windows does not show their CPU time to StykkerSYS (system processes and protected services).");

            return new OverviewSnapshot(now, system, gpu,
                OverviewMath.TopByRam(programs, ProgramCount), OverviewMath.TopByCpu(programs, ProgramCount), notes.ToArray());
        }
    }

    // Alle Prozesse mit Speicher und Prozessorzeit. Die Zeit je Prozess als Differenz zur letzten Messung. Zählt zugleich
    // die Prozesse, deren Zeit Windows nicht herausgibt.
    private List<ProcessMeasure> ReadProcesses(DateTime now, out int unreadable)
    {
        var list = new List<ProcessMeasure>(256);
        var alive = new HashSet<int>();
        unreadable = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                int pid = p.Id;
                alive.Add(pid);
                long cpuMs = (long)p.TotalProcessorTime.TotalMilliseconds;
                double? cpu = null;
                if (_prev.TryGetValue(pid, out var prev))
                {
                    double elapsed = (now - prev.At).TotalMilliseconds;
                    if (elapsed > 0) cpu = Math.Clamp(100.0 * (cpuMs - prev.CpuMs) / (elapsed * _cores), 0, 100);
                }
                _prev[pid] = (cpuMs, now);
                list.Add(new ProcessMeasure(pid, p.ProcessName, p.WorkingSet64 / 1048576.0, cpu));
            }
            catch (Win32Exception)
            {
                // Geschützt: Windows verweigert die Zeit. Die Zeile fehlt in diesem Takt – und wird gezählt.
                unreadable++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
            {
                // Beendet, während die Liste entstand.
            }
            finally { p.Dispose(); }
        }
        foreach (var pid in _prev.Keys.Where(k => !alive.Contains(k)).ToArray()) _prev.Remove(pid);
        return list;
    }
}

// Die Messschleife der Übersicht (siehe ViewerLoop): sie liest nur, solange jemand die Seite ansieht.
public sealed class OverviewService : ViewerLoop<OverviewSnapshot>
{
    public OverviewService(OverviewSampler sampler) : base(sampler) { }
}
