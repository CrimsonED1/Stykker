using System.Runtime.InteropServices;
using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Platform.Windows;

// Windows-Umsetzung von IPlatform: TCP-Tabellen, Prozessdaten aus dem PEB, PDH, NVML, GlobalMemoryStatusEx.
public sealed class WindowsPlatform : IPlatform
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int cls, byte[] info, int len, out int ret);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr h, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    private const uint QueryLimited = 0x1000, QueryInfo = 0x0400, VmRead = 0x0010, TerminateAccess = 0x0001;
    private const uint StillActive = 259;
    private const int ErrorInvalidParameter = 87;

    // RTL_USER_PROCESS_PARAMETERS (x64)
    private const int OffCurrentDir = 0x38, OffCommandLine = 0x70, OffEnvironment = 0x80, OffEnvironmentSize = 0x3F0;
    private const int MaxEnvBytes = 1 << 20;

    private readonly Nvml _nvml = new();
    private readonly SysInfo _sys = new();
    private readonly ProcMem.GpuProcessQuery _gpuProcs = new();
    private readonly ProcMem.GpuUtilQuery _gpuUtil = new();
    private readonly object _gpuLock = new();
    private bool _disposed;

    public int ProcessorCount => Environment.ProcessorCount;

    public IReadOnlyList<ListenerInfo> ReadListeners()
    {
        try { return ProcMem.Listeners(); }
        catch { return Array.Empty<ListenerInfo>(); }
    }

    public IReadOnlyList<ConnectionInfo> ReadConnections()
    {
        try { return ProcMem.Connections(); }
        catch { return Array.Empty<ConnectionInfo>(); }
    }

    public long? ProcessStartTicks(int pid)
    {
        var h = OpenProcess(QueryLimited, false, pid);
        if (h == IntPtr.Zero) return null;
        try { return GetProcessTimes(h, out long create, out _, out _, out _) ? create : null; }
        finally { CloseHandle(h); }
    }

    // Name aus dem Pfad über ein eingeschränktes Handle (PROCESS_QUERY_LIMITED_INFORMATION); nur wenn das nicht geht, der Prozessname des Systems
    public string? ProcessName(int pid)
    {
        var h = OpenProcess(QueryLimited, false, pid);
        if (h != IntPtr.Zero)
        {
            try { if (ImagePath(h) is { Length: > 0 } path) return Path.GetFileNameWithoutExtension(path); }
            finally { CloseHandle(h); }
        }
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }

    // Startzeit prüfen und beenden über ein einziges Handle: zwischen Prüfung und Beenden kann die PID nicht neu vergeben werden
    public StopOutcome Terminate(int pid, long expectedStartTicks, out string? error)
    {
        error = null;
        var h = OpenProcess(TerminateAccess | QueryLimited, false, pid);
        if (h == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ErrorInvalidParameter) return StopOutcome.NotFound;
            error = new System.ComponentModel.Win32Exception(err).Message;
            return StopOutcome.Failed;
        }
        try
        {
            if (GetExitCodeProcess(h, out uint code) && code != StillActive) return StopOutcome.NotFound;
            if (expectedStartTicks != 0)
            {
                if (!GetProcessTimes(h, out long create, out _, out _, out _)) { error = "Process start time not readable"; return StopOutcome.Failed; }
                if (create != expectedStartTicks) return StopOutcome.ProcessChanged;
            }
            if (TerminateProcess(h, 1)) return StopOutcome.Stopped;
            error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
            return StopOutcome.Failed;
        }
        finally { CloseHandle(h); }
    }

    private static string? ImagePath(IntPtr h)
    {
        var sb = new StringBuilder(1024);
        uint len = (uint)sb.Capacity;
        return QueryFullProcessImageNameW(h, 0, sb, ref len) ? sb.ToString() : null;
    }

    private static byte[]? ReadMem(IntPtr h, IntPtr addr, int size)
    {
        if (size <= 0) return null;
        var buf = new byte[size];
        return ReadProcessMemory(h, addr, buf, size, out var got) && (long)got == size ? buf : null;
    }

    private static string? ReadUnicodeString(IntPtr h, IntPtr parms, int offset)
    {
        var us = ReadMem(h, parms + offset, 16);   // UNICODE_STRING: Length (2), MaximumLength (2), Puffer (8 ab Offset 8)
        if (us == null) return null;
        int length = BitConverter.ToUInt16(us, 0);
        var ptr = (IntPtr)BitConverter.ToInt64(us, 8);
        if (length <= 0 || length > 32768 || ptr == IntPtr.Zero) return null;
        var text = ReadMem(h, ptr, length);
        return text == null ? null : Encoding.Unicode.GetString(text);
    }

    // ProcessBasicInformation (PROCESS_BASIC_INFORMATION, x64): PebBaseAddress bei 8, InheritedFromUniqueProcessId bei 40.
    // Funktioniert schon mit PROCESS_QUERY_LIMITED_INFORMATION.
    private static int ParentOf(IntPtr h)
    {
        try
        {
            var pbi = new byte[48];
            return NtQueryInformationProcess(h, 0, pbi, pbi.Length, out _) == 0 ? (int)BitConverter.ToInt64(pbi, 40) : 0;
        }
        catch { return 0; }
    }

    // Pfad, Startzeit und Elternprozess über ein eingeschränktes Handle: es wird kein fremder Speicher gelesen
    public ProcessDetails? ReadProcessBasic(int pid)
    {
        var h = OpenProcess(QueryLimited, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            long start = GetProcessTimes(h, out long create, out _, out _, out _) ? create : 0;
            return new ProcessDetails(pid, start, ParentOf(h), ImagePath(h), null, null, new Dictionary<string, string>());
        }
        finally { CloseHandle(h); }
    }

    // Kommandozeile, Arbeitsordner und Umgebung (nur Whitelist) aus dem PEB. Nur für Prozesse, die schon als mögliche Modell-Server
    // feststehen (Namensfilter oder positive Probe). Gleiche Bitbreite und gleicher Benutzer nötig; sonst bleiben die Felder leer
    // und es gilt das eingeschränkte Ergebnis.
    public ProcessDetails? ReadProcess(int pid)
    {
        var h = OpenProcess(QueryInfo | VmRead | QueryLimited, false, pid);
        if (h == IntPtr.Zero) return ReadProcessBasic(pid);
        try
        {
            long start = GetProcessTimes(h, out long create, out _, out _, out _) ? create : 0;
            var path = ImagePath(h);
            int parent = 0;
            string? cmd = null, cwd = null;
            var env = new Dictionary<string, string>();
            try
            {
                var pbi = new byte[48];   // PROCESS_BASIC_INFORMATION (x64)
                if (NtQueryInformationProcess(h, 0, pbi, pbi.Length, out _) == 0)
                {
                    parent = (int)BitConverter.ToInt64(pbi, 40);
                    var peb = (IntPtr)BitConverter.ToInt64(pbi, 8);
                    var pp = ReadMem(h, peb + 0x20, 8);   // PEB.ProcessParameters
                    if (pp != null)
                    {
                        var parms = (IntPtr)BitConverter.ToInt64(pp, 0);
                        cmd = ReadUnicodeString(h, parms, OffCommandLine);
                        cwd = ReadUnicodeString(h, parms, OffCurrentDir);
                        if (cwd is { Length: > 3 }) cwd = cwd.TrimEnd('\\');
                        ReadEnvironment(h, parms, env);
                    }
                }
            }
            catch { }
            return new ProcessDetails(pid, start, parent, path, cmd, cwd, env);
        }
        finally { CloseHandle(h); }
    }

    private static void ReadEnvironment(IntPtr h, IntPtr parms, Dictionary<string, string> into)
    {
        var p = ReadMem(h, parms + OffEnvironment, 8);
        var s = ReadMem(h, parms + OffEnvironmentSize, 8);
        if (p == null || s == null) return;
        var ptr = (IntPtr)BitConverter.ToInt64(p, 0);
        long size = BitConverter.ToInt64(s, 0);
        if (ptr == IntPtr.Zero || size <= 0 || size > MaxEnvBytes) return;
        var block = ReadMem(h, ptr, (int)size);
        if (block == null) return;
        var text = Encoding.Unicode.GetString(block);
        foreach (var entry in text.Split('\0'))
        {
            if (entry.Length == 0) continue;
            int eq = entry.IndexOf('=', 1);   // "=C:=C:\..." beginnt mit '='
            if (eq <= 0) continue;
            var name = entry[..eq];
            if (CmdLine.IsEnvWhitelisted(name)) into[name] = entry[(eq + 1)..];   // nur die Whitelist wird überhaupt behalten
        }
    }

    public ProcessUsage? ReadUsage(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return new ProcessUsage(p.WorkingSet64 / 1073741824.0, p.PrivateMemorySize64 / 1073741824.0, p.TotalProcessorTime);
        }
        catch { return null; }
    }

    public IGpuMemoryQuery? OpenGpuMemory(int pid) => new ProcMem.GpuMemQuery(pid);

    // Working Set aller Prozesse aus der System-Prozessliste (ein Aufruf, öffnet keine Prozess-Handles), je Programmname summiert
    public IReadOnlyList<(string Name, double Gb)>? ReadRamTop(int count)
    {
        try
        {
            var sums = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try { if (p.Id > 4) sums[p.ProcessName] = sums.GetValueOrDefault(p.ProcessName) + p.WorkingSet64; }
                catch { }
                finally { p.Dispose(); }
            }
            return sums.OrderByDescending(kv => kv.Value).Take(count).Select(kv => (kv.Key, kv.Value / 1073741824.0)).ToList();
        }
        catch { return null; }
    }

    // Kindprozesse über einen Toolhelp-Schnappschuss (liest keinen fremden Speicher)
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref ProcessEntry32 e);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    public IReadOnlyList<(int Pid, string Name)> ChildProcesses(int pid)
    {
        var list = new List<(int, string)>();
        var snap = CreateToolhelp32Snapshot(0x2, 0);   // TH32CS_SNAPPROCESS
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var e = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                if (e.th32ParentProcessID == pid && e.th32ProcessID != pid) list.Add(((int)e.th32ProcessID, ProcPath.Stem(e.szExeFile)));
        }
        catch { }
        finally { CloseHandle(snap); }
        return list;
    }

    public GpuSample? ReadGpu() => _nvml.Read();

    public string? GpuDriver => _nvml.DriverVersion;

    public SystemSample? ReadSystem() => _sys.Read();

    public IReadOnlyList<(string Name, double Gb)>? ReadGpuTop(int count)
    {
        lock (_gpuLock)
            return _disposed ? null : _gpuProcs.ReadTop(count);
    }

    public IReadOnlyList<(string Name, double Percent)> ReadGpuUtilTop(int count)
    {
        lock (_gpuLock)
        {
            if (_disposed) return Array.Empty<(string, double)>();
            return _gpuUtil.ReadTop(count) ?? new List<(string Name, double Percent)>();
        }
    }

    public byte[]? ProtectForCurrentUser(byte[] data) => Dpapi.Protect(data);
    public byte[]? UnprotectForCurrentUser(byte[] data) => Dpapi.Unprotect(data);

    public void Dispose()
    {
        lock (_gpuLock) { _disposed = true; _gpuProcs.Dispose(); _gpuUtil.Dispose(); }
        _nvml.Dispose();
    }
}
