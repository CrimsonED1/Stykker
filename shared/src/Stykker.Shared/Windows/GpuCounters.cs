using Stykker.Shared.Gpu;

namespace Stykker.Shared.Windows;

// GPU-Auslastung in Prozent („GPU Engine", wie im Task-Manager) – je Prozess (ein Prozess hat mehrere Einträge:
// mehrere Engines, mehrere Grafikprozessoren) und je Engine-Art über alle Prozesse zusammen. Beides aus einem
// Durchgang, weil der Zähler eine Rate liefert und eine zweite Abtastung im selben Takt null ergäbe.
public sealed class GpuUtilQuery : IDisposable
{
    private readonly IntPtr _query, _counter;
    private readonly bool _ok;

    public GpuUtilQuery()
    {
        _ok = Pdh.Open(@"\GPU Engine(*)\Utilization Percentage", out _query, out _counter);
        if (_ok) Pdh.Collect(_query);   // Prozent-Zähler brauchen eine erste Abtastung
    }

    // null, wenn der Zähler fehlt: dann wäre „0 %" eine Behauptung, die niemand gemessen hat.
    public GpuUtilSample? Read()
    {
        if (!_ok || !Pdh.Collect(_query)) return null;
        var byPid = new Dictionary<int, double>();
        var byEngine = new Dictionary<string, double>();
        foreach (var (name, value) in Pdh.Values(_counter, Pdh.FmtDouble))
        {
            if (value <= 0) continue;
            int pid = Pdh.PidOf(name);
            if (pid != 0) byPid[pid] = byPid.GetValueOrDefault(pid) + value;
            var engine = Pdh.EngineOf(name);
            if (engine.Length > 0) byEngine[engine] = byEngine.GetValueOrDefault(engine) + value;
        }
        return new GpuUtilSample(
            byPid.Select(kv => new GpuProcRow(kv.Key, Math.Min(100.0, kv.Value))).ToArray(),
            byEngine.Select(kv => new GpuEngineRow(kv.Key, Math.Round(kv.Value, 1))).ToArray());
    }

    public void Dispose() => Pdh.Close(_query);
}

// Grafikspeicher je Prozess in MB (dediziert). Teuer: diese Abfrage zählt jeden Grafikprozess der Maschine auf
// einmal durch – der Taktgeber ruft sie deshalb nur alle paar Sekunden auf.
public sealed class GpuProcessMemoryQuery : IDisposable
{
    private readonly IntPtr _query, _counter;
    private readonly bool _ok;

    public GpuProcessMemoryQuery() => _ok = Pdh.Open(@"\GPU Process Memory(*)\Dedicated Usage", out _query, out _counter);

    // null, wenn der Zähler fehlt (sonst stünde dort ein falsches „0 MB").
    public IReadOnlyList<GpuProcRow>? Read()
    {
        if (!_ok || !Pdh.Collect(_query)) return null;
        var byPid = new Dictionary<int, double>();
        foreach (var (name, value) in Pdh.Values(_counter, Pdh.FmtLarge))
        {
            int pid = Pdh.PidOf(name);
            if (pid == 0 || value <= 0) continue;
            byPid[pid] = byPid.GetValueOrDefault(pid) + value;
        }
        return byPid.Select(kv => new GpuProcRow(kv.Key, kv.Value / 1048576.0)).ToArray();
    }

    public void Dispose() => Pdh.Close(_query);
}
