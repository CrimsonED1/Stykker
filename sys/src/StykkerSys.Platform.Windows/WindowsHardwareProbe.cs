using Stykker.Shared.Gpu;
using Stykker.Shared.Windows;
using StykkerSys.Core;

namespace StykkerSys.Platform.Windows;

// Die Windows-Sonde der Übersicht: Prozessor und Speicher (SystemCounters), GPU je Engine und je Prozess und der belegte
// Grafikspeicher aller Adapter (Leistungsindikatoren). Jeder Teil ist verzichtbar: fehlt er, liefert er null – die Seite
// zeigt „–". Die Übersicht hat eigene Abfragen, nicht die der Prozessliste: eine Rate braucht ihre eigene Abtastung.
public sealed class WindowsHardwareProbe : IHardwareProbe
{
    private readonly SystemCounters _system = new();
    private readonly GpuUtilQuery? _gpuUtil;
    private readonly GpuProcessMemoryQuery? _gpuMem;
    private readonly GpuAdapterMemoryQuery? _adapterMem;

    public WindowsHardwareProbe()
    {
        _gpuUtil = TryNew(() => new GpuUtilQuery());
        _gpuMem = TryNew(() => new GpuProcessMemoryQuery());
        _adapterMem = TryNew(() => new GpuAdapterMemoryQuery());
    }

    private static T? TryNew<T>(Func<T> create) where T : class
    {
        try { return create(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    public SystemLoad? ReadSystem() => _system.Read();
    public GpuUtilSample? ReadGpuUtil() => _gpuUtil?.Read();
    public IReadOnlyList<GpuProcRow>? ReadGpuMemory() => _gpuMem?.Read();
    public double? ReadGpuMemoryUsedMb() => _adapterMem?.ReadMb();

    public void Dispose()
    {
        _gpuUtil?.Dispose();
        _gpuMem?.Dispose();
        _adapterMem?.Dispose();
    }
}

// Der Grafikspeicher, der auf allen Adaptern dediziert belegt ist, in MB. Die Gesamtgröße nennen die Zähler nicht.
internal sealed class GpuAdapterMemoryQuery : IDisposable
{
    private readonly IntPtr _query, _counter;
    private readonly bool _ok;

    public GpuAdapterMemoryQuery() => _ok = Pdh.Open(@"\GPU Adapter Memory(*)\Dedicated Usage", out _query, out _counter);

    // null, wenn der Zähler fehlt (sonst stünde dort ein falsches „0 MB").
    public double? ReadMb()
    {
        if (!_ok || !Pdh.Collect(_query)) return null;
        return Pdh.Values(_counter, Pdh.FmtLarge).Sum(v => v.Value) / 1048576.0;
    }

    public void Dispose() => Pdh.Close(_query);
}
