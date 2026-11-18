using Microsoft.Win32;
using Stykker.Shared.Gpu;
using Stykker.Shared.Net;
using Stykker.Shared.Windows;
using StykkerHud.Core;

namespace StykkerHud.Platform.Windows;

// Die Windows-Sonde: SysInfo (CPU/RAM), Nvml (GPU), die PDH-Zähler (GPU-Auslastung und Grafikspeicher) und der
// Durchsatz von Datenträger und Netzwerk. Jeder Teil ist verzichtbar – fehlt er, liefert er null und die Anzeige
// zeigt „–".
public sealed class WindowsProbe : ISystemProbe
{
    private readonly SysInfo _sys = new();
    private readonly Nvml _nvml = new();
    private readonly GpuUtilQuery? _gpuUtil;
    private readonly GpuProcessMemoryQuery? _gpuMem;
    private readonly DiskRateQuery? _disk;
    private readonly NetCounters _net = new();

    public WindowsProbe()
    {
        _gpuUtil = TryNew(() => new GpuUtilQuery());
        _gpuMem = TryNew(() => new GpuProcessMemoryQuery());
        _disk = TryNew(() => new DiskRateQuery());
    }

    private static T? TryNew<T>(Func<T> create) where T : class
    {
        try { return create(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    public bool GpuAvailable => _nvml.Available;
    public string? AdapterName { get; } = ReadAdapterName();
    public SystemSample? ReadSystem() => _sys.Read();
    public GpuSample? ReadGpu() => _nvml.Read();
    public GpuUtilSample? ReadGpuUtil() => _gpuUtil?.Read();
    public IReadOnlyList<GpuProcRow>? ReadGpuMemory() => _gpuMem?.Read();

    // Datenträger aus den Leistungsindikatoren, Netzwerk aus den Zählern der Schnittstellen (Stykker.Shared/Net:
    // alle aktiven Schnittstellen ohne Schleife und Tunnel, IPv4 und IPv6, dieselbe Messung wie in StykkerLLM).
    // Beide brauchen eine Basiszahl, deshalb antwortet die erste Abfrage mit null.
    public IoRates? ReadIo()
    {
        var disk = _disk?.Read();
        var net = _net.Read();
        if (disk == null && net == null) return null;
        return new IoRates(disk?.Read ?? 0, disk?.Write ?? 0, net?.RxBytesPerSec ?? 0, net?.TxBytesPerSec ?? 0);
    }

    public void ResetBaselines()
    {
        _sys.Reset();
        _disk?.Reset();
        _net.Reset();   // die nächste Abfrage setzt nur die Basiszahl
    }

    // Der Treibername des ersten Anzeige-Geräts aus der Windows-Geräteklasse „Display“. Nur für die Anzeige ohne
    // nvml.dll; mit mehreren Grafikkarten zählt die erste Eintragung.
    private static string? ReadAdapterName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000");
            return key?.GetValue("DriverDesc") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public void Dispose()
    {
        _gpuUtil?.Dispose();
        _gpuMem?.Dispose();
        _disk?.Dispose();
        _nvml.Dispose();
    }
}
