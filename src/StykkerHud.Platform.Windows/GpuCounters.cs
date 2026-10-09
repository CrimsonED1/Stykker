using System.Runtime.InteropServices;
using StykkerHud.Core;

namespace StykkerHud.Platform.Windows;

// Leistungsindikatoren von Windows über pdh.dll: GPU-Auslastung und Grafikspeicher je Prozess – dieselben Zahlen,
// die der Task-Manager zeigt. übernommen aus StykkerLLM (ProcMem.cs).
internal static class Pdh
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);

    public const uint FmtDouble = 0x00000200, FmtLarge = 0x00000400;
    private const uint MoreData = 0x800007D2;
    // PDH_FMT_COUNTERVALUE_ITEM_W: Name-Zeiger (8), CStatus (4) + Ausrichtung (4), Wert (8) = 24 Byte
    private const int ItemSize = 24;

    public static bool Open(string path, out IntPtr query, out IntPtr counter)
    {
        var ok = Open(new[] { path }, out query, out var counters);
        counter = counters.Length > 0 ? counters[0] : IntPtr.Zero;
        return ok;
    }

    // Mehrere Zähler auf einer Abfrage: Raten-Zähler (Byte je Sekunde) müssen zusammen abgetastet werden.
    public static bool Open(string[] paths, out IntPtr query, out IntPtr[] counters)
    {
        query = IntPtr.Zero;
        counters = new IntPtr[paths.Length];
        if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) return false;
        for (int i = 0; i < paths.Length; i++)
            if (PdhAddEnglishCounterW(query, paths[i], IntPtr.Zero, out counters[i]) != 0) return false;
        return true;
    }

    public static bool Collect(IntPtr query) => PdhCollectQueryData(query) == 0;

    public static void Close(IntPtr query)
    {
        if (query != IntPtr.Zero) PdhCloseQuery(query);
    }

    // Alle Instanzen eines Zählers lesen: Instanzname und Wert (bei FmtLarge in Byte, bei FmtDouble als Prozentzahl).
    public static List<(string Name, double Value)> Values(IntPtr counter, uint format)
    {
        var list = new List<(string, double)>();
        uint size = 0;
        if (PdhGetFormattedCounterArrayW(counter, format, ref size, out _, IntPtr.Zero) != MoreData) return list;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, format, ref size, out uint count, buf) != 0) return list;
            for (int i = 0; i < count; i++)
            {
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf + i * ItemSize)) ?? "";
                long raw = Marshal.ReadInt64(buf + i * ItemSize + 16);
                list.Add((name, format == FmtDouble ? BitConverter.Int64BitsToDouble(raw) : raw));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    // Die Instanznamen der Grafikzähler beginnen mit "pid_1234_…": die PID steht zwischen den ersten Unterstrichen.
    public static int PidOf(string instance)
    {
        if (!instance.StartsWith("pid_", StringComparison.Ordinal)) return 0;
        int end = instance.IndexOf('_', 4);
        return end > 4 && int.TryParse(instance.AsSpan(4, end - 4), out int pid) ? pid : 0;
    }

    // Die Art der Engine steht am Ende des Instanznamens: "…_eng_0_engtype_3d" → "3D". Leer, wenn der Name sie
    // nicht nennt (dann zählt der Wert nur zum Prozess).
    public static string EngineOf(string instance)
    {
        const string marker = "engtype_";
        int at = instance.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return "";
        var type = instance[(at + marker.Length)..].ToLowerInvariant();
        return type switch
        {
            "3d" => "3D",
            "copy" => "Copy",
            "compute" => "Compute",
            "videodecode" => "Video decode",
            "videoencode" => "Video encode",
            "" => "",
            _ => char.ToUpperInvariant(type[0]) + type[1..],
        };
    }
}

// GPU-Auslastung in Prozent („GPU Engine", wie im Task-Manager) – je Prozess (ein Prozess hat mehrere Einträge:
// mehrere Engines, mehrere Grafikprozessoren) und je Engine-Art über alle Prozesse zusammen. Beides aus einem
// Durchgang, weil der Zähler eine Rate liefert und eine zweite Abtastung im selben Takt null ergäbe.
internal sealed class GpuUtilQuery : IDisposable
{
    private readonly IntPtr _query, _counter;
    private readonly bool _ok;

    public GpuUtilQuery()
    {
        _ok = Pdh.Open(@"\GPU Engine(*)\Utilization Percentage", out _query, out _counter);
        if (_ok) Pdh.Collect(_query);   // Prozent-Zähler brauchen eine erste Abtastung
    }

    public GpuUtilSample Read()
    {
        if (!_ok || !Pdh.Collect(_query))
            return new GpuUtilSample(Array.Empty<GpuProcRow>(), Array.Empty<GpuEngineRow>());
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
internal sealed class GpuProcessMemoryQuery : IDisposable
{
    private readonly IntPtr _query, _counter;
    private readonly bool _ok;

    public GpuProcessMemoryQuery() => _ok = Pdh.Open(@"\GPU Process Memory(*)\Dedicated Usage", out _query, out _counter);

    public IReadOnlyList<GpuProcRow> Read()
    {
        if (!_ok || !Pdh.Collect(_query)) return Array.Empty<GpuProcRow>();
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