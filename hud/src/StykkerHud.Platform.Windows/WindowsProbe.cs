using System.Net.NetworkInformation;
using Microsoft.Win32;
using Stykker.Shared.Gpu;
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
    private long _netRx, _netTx;
    private DateTime _netAt = DateTime.MinValue;

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

    // Datenträger aus den Leistungsindikatoren, Netzwerk aus den Zählern der Schnittstellen (Summe über alle
    // aktiven Schnittstellen, ohne Schleife und Tunnel). Beide brauchen eine Basiszahl, deshalb antwortet die
    // erste Abfrage mit null.
    public IoRates? ReadIo()
    {
        var disk = _disk?.Read();
        var (rx, tx) = NetworkTotals();
        var now = DateTime.Now;
        double? rxRate = null, txRate = null;
        if (_netAt != DateTime.MinValue)
        {
            double seconds = (now - _netAt).TotalSeconds;
            if (seconds > 0)
            {
                rxRate = Math.Max(0, (rx - _netRx) / seconds);
                txRate = Math.Max(0, (tx - _netTx) / seconds);
            }
        }
        _netRx = rx;
        _netTx = tx;
        _netAt = now;
        if (disk == null && rxRate == null) return null;
        return new IoRates(disk?.Read ?? 0, disk?.Write ?? 0, rxRate ?? 0, txRate ?? 0);
    }

    public void ResetBaselines()
    {
        _sys.Reset();
        _disk?.Reset();
        _netAt = DateTime.MinValue;   // die nächste Abfrage setzt nur die Basiszahl
    }

    private static (long Rx, long Tx) NetworkTotals()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                try
                {
                    var stats = ni.GetIPv4Statistics();
                    rx += stats.BytesReceived;
                    tx += stats.BytesSent;
                }
                catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { }
        return (rx, tx);
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
