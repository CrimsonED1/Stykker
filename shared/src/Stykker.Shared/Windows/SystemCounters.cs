using System.Diagnostics;
using System.Runtime.InteropServices;
using Stykker.Shared.Hardware;

namespace Stykker.Shared.Windows;

// Prozessor und Arbeitsspeicher des ganzen Rechners: Gesamtauslastung (GetSystemTimes-Differenz), je Kern
// (NtQuerySystemInformation, nur für den Tooltip), RAM und Commit (GlobalMemoryStatusEx) und die größten
// Programme nach Arbeitsspeicher. Lesend, ohne Administratorrechte. Typ und Takt der Riegel (SMBIOS) liest nur
// StykkerLLM; sie gehören nicht hierher.
public sealed class SystemCounters
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, byte[] info, int length, out int returned);

    // SystemProcessorPerformanceInformation (8): je Kern Leerlauf, Kernel, User, DPC, Interrupt (je 8 Byte), 48 Byte je Kern
    private const int ProcessorPerformanceClass = 8, ProcessorEntry = 48;

    public static readonly int Cores = Environment.ProcessorCount;

    private long _idle, _kernel, _user;
    private bool _have;
    private double _cpu;
    private long[]? _coreIdle, _coreTotal;
    private double[]? _coreLoads;

    // Ein Takt: null, wenn die Zeiten nicht zu lesen sind (dann gibt es keine Zahl, die man zeigen könnte).
    public SystemLoad? Read()
    {
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            if (_have && SystemMath.CpuPercent(idle, kernel, user, _idle, _kernel, _user) is double cpu) _cpu = cpu;
            _idle = idle; _kernel = kernel; _user = user; _have = true;
        }
        try { ReadCores(); } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref m)) return null;
        const double gb = SystemMath.Gib;
        return new SystemLoad(_cpu, Cores, _coreLoads, (m.TotalPhys - m.AvailPhys) / gb, m.TotalPhys / gb,
            (m.TotalPageFile - m.AvailPageFile) / gb, m.TotalPageFile / gb);
    }

    private void ReadCores()
    {
        var buf = new byte[ProcessorEntry * Cores];
        if (NtQuerySystemInformation(ProcessorPerformanceClass, buf, buf.Length, out int got) != 0 || got < ProcessorEntry) return;
        int n = got / ProcessorEntry;
        var idle = new long[n];
        var total = new long[n];
        for (int k = 0; k < n; k++)
        {
            idle[k] = BitConverter.ToInt64(buf, k * ProcessorEntry);
            total[k] = BitConverter.ToInt64(buf, k * ProcessorEntry + 8) + BitConverter.ToInt64(buf, k * ProcessorEntry + 16);   // Kernel enthält Idle
        }
        if (_coreIdle != null && _coreTotal != null && _coreIdle.Length == n)
        {
            var loads = new double[n];
            for (int k = 0; k < n; k++) loads[k] = SystemMath.CoreLoad(total[k] - _coreTotal[k], idle[k] - _coreIdle[k]);
            _coreLoads = loads;
        }
        _coreIdle = idle;
        _coreTotal = total;
    }

    // Die größten Programme nach Arbeitsspeicher (Working Set), gleichnamige zusammengefasst. Ein Aufruf, öffnet keine
    // Prozess-Handles. null, wenn die Prozessliste nicht zu lesen ist.
    public static IReadOnlyList<(string Name, double Gb)>? TopPrograms(int count)
    {
        try
        {
            var rows = new List<(int, string, long)>();
            foreach (var p in Process.GetProcesses())
            {
                try { rows.Add((p.Id, p.ProcessName, p.WorkingSet64)); }
                catch { }
                finally { p.Dispose(); }
            }
            return SystemMath.TopPrograms(rows, count);
        }
        catch { return null; }
    }
}

// Ein Takt der Gesamtwerte. CoreLoads: Auslastung je logischem Kern in Prozent, null = noch nicht gemessen (erster Takt).
// RamUsedGb = belegt (gesamt minus verfügbar, wie „In Verwendung" im Task-Manager). Commit = zugesagter Speicher
// (RAM und Auslagerungsdatei).
public sealed record SystemLoad(double CpuPercent, int Cores, double[]? CoreLoads, double RamUsedGb, double RamTotalGb,
    double CommitUsedGb, double CommitLimitGb);
