using System.Runtime.InteropServices;
using System.Text;
using Stykker.Shared.Gpu;
using Stykker.Shared.Windows;
using StykkerSys.Core;

namespace StykkerSys.Platform.Windows;

// Die Windows-Sonde der Prozessliste: GPU je Prozess über die Leistungsindikatoren (Stykker.Shared) und die
// Programmdatei über den Prozessgriff. Jeder Teil ist verzichtbar – fehlt er, liefert er null und die Liste zeigt „–".
public sealed class WindowsProcessProbe : IProcessProbe
{
    private readonly GpuUtilQuery? _gpuUtil;
    private readonly GpuProcessMemoryQuery? _gpuMem;

    public WindowsProcessProbe()
    {
        _gpuUtil = TryNew(() => new GpuUtilQuery());
        _gpuMem = TryNew(() => new GpuProcessMemoryQuery());
    }

    private static T? TryNew<T>(Func<T> create) where T : class
    {
        try { return create(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    public GpuUtilSample? ReadGpuUtil() => _gpuUtil?.Read();
    public IReadOnlyList<GpuProcRow>? ReadGpuMemory() => _gpuMem?.Read();

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

    public void Dispose()
    {
        _gpuUtil?.Dispose();
        _gpuMem?.Dispose();
    }
}
