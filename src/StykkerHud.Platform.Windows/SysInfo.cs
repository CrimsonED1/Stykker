using System.Runtime.InteropServices;
using StykkerHud.Core;

namespace StykkerHud.Platform.Windows;

// CPU-Auslastung gesamt (GetSystemTimes-Differenz), RAM und Commit (GlobalMemoryStatusEx) und die Last je Kern
// (NtQuerySystemInformation). übernommen aus StykkerLLM (gleiche Quelle, SysInfo.cs).
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

    // Zeiten je Kern (U14): SystemProcessorPerformanceInformation = 8, je Kern Idle, Kernel, User, Dpc, Interrupt
    // (je 8 Byte) + Zähler
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, byte[] info, int length, out int returned);

    public static readonly int Cores = Environment.ProcessorCount;
    private long[]? _coreIdle, _coreTotal;
    private double[]? _coreLoads;
    private long _idle, _kernel, _user;
    private bool _have;
    private double _cpu;

    // Nach einer Pause (niemand hat zugesehen): Differenzen wären über die ganze Pause gemittelt – neu anfangen.
    public void Reset()
    {
        _have = false;
        _coreIdle = null;
        _coreTotal = null;
        _coreLoads = null;
    }

    private void ReadCores()
    {
        const int entry = 48;
        var buf = new byte[entry * Cores];
        if (NtQuerySystemInformation(8, buf, buf.Length, out int got) != 0 || got < entry) return;
        int n = got / entry;
        var idle = new long[n]; var total = new long[n];
        for (int k = 0; k < n; k++)
        {
            idle[k] = BitConverter.ToInt64(buf, k * entry);
            total[k] = BitConverter.ToInt64(buf, k * entry + 8) + BitConverter.ToInt64(buf, k * entry + 16);   // Kernel enthält Idle
        }
        if (_coreIdle != null && _coreTotal != null && _coreIdle.Length == n)
        {
            var loads = new double[n];
            for (int k = 0; k < n; k++)
            {
                long dt = total[k] - _coreTotal[k], di = idle[k] - _coreIdle[k];
                loads[k] = dt > 0 ? Math.Clamp(100.0 * (dt - di) / dt, 0, 100) : 0;
            }
            _coreLoads = loads;
        }
        _coreIdle = idle; _coreTotal = total;
    }

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
        try { ReadCores(); } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref m)) return null;
        const double gb = 1024.0 * 1024 * 1024;
        return new SystemSample(_cpu, Cores, (m.TotalPhys - m.AvailPhys) / gb, m.TotalPhys / gb,
            (m.TotalPageFile - m.AvailPageFile) / gb, m.TotalPageFile / gb, _coreLoads);
    }
}