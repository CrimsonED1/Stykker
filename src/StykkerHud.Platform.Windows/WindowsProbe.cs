using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using StykkerHud.Core;

namespace StykkerHud.Platform.Windows;

// Die Windows-Sonde: SysInfo (CPU/RAM), Nvml (GPU), die PDH-Zähler (GPU je Prozess) und der Durchsatz von
// Datenträger und Netzwerk. Jeder Teil ist verzichtbar – fehlt er, liefert er null und die Anzeige zeigt „–".
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

    // Die Programmdatei über den Prozessgriff statt über Process.MainModule: das ist deutlich billiger (keine
    // Modulliste) und liefert auch für 32-Bit-Prozesse den Pfad. Verweigert Windows den Griff, gibt es null.
    public string? PathOf(int pid)
    {
        IntPtr handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(640);
            int size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    private const uint QueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

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

    public void Dispose()
    {
        _gpuUtil?.Dispose();
        _gpuMem?.Dispose();
        _disk?.Dispose();
        _nvml.Dispose();
    }
}