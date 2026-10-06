using System.Runtime.InteropServices;
using StykkerLlm.Core;

namespace StykkerLlm.Platform.Windows;

// CPU-Auslastung gesamt (GetSystemTimes-Differenz), RAM und Commit (GlobalMemoryStatusEx).
public sealed class SysInfo
{
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    public static readonly int Cores = Environment.ProcessorCount;
    private long _idle, _kernel, _user;
    private bool _have;
    private double _cpu;

    public SystemSample? Read()
    {
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            if (_have)
            {
                long dIdle = idle - _idle, dTotal = (kernel - _kernel) + (user - _user);   // Kernel-Zeit enthält die Leerlaufzeit
                if (dTotal > 0) _cpu = Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
            }
            _idle = idle; _kernel = kernel; _user = user; _have = true;
        }
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref m)) return null;
        const double gb = 1024.0 * 1024 * 1024;
        return new SystemSample(_cpu, Cores, (m.TotalPhys - m.AvailPhys) / gb, m.TotalPhys / gb,
            (m.TotalPageFile - m.AvailPageFile) / gb, m.TotalPageFile / gb);
    }
}
