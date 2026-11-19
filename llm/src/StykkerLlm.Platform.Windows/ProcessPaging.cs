using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StykkerLlm.Core;

namespace StykkerLlm.Platform.Windows;

// Seitenfehler und eingeblendete Dateien eines Prozesses, ohne Administratorrechte:
// - harte Seitenfehler aus der Prozessliste des Systems (NtQuerySystemInformation, SystemProcessInformation),
// - wie viel einer eingeblendeten Datei (GGUF über mmap) im Arbeitsspeicher des Prozesses liegt (VirtualQueryEx,
//   K32GetMappedFileNameW, K32QueryWorkingSetEx).
// Nur 64-Bit-Prozesse (die Versätze unten gelten für x64 und ARM64). Alle Methoden sind threadsicher und werfen nicht.
public static class ProcessPaging
{
    // ── Prozessliste des Systems ──
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int cls, IntPtr info, int len, out int ret);

    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int MaxListBytes = 64 << 20;

    // SYSTEM_PROCESS_INFORMATION (x64, phnt/ntddk; geprüft im Test N117 gegen den eigenen Prozess:
    // Name, Elternprozess, Startzeit, Threads und Working Set stimmen)
    internal const int OffNext = 0, OffThreads = 4, OffHardFaults = 16, OffCreateTime = 32, OffImageName = 56,
        OffPid = 80, OffParentPid = 88, OffPageFaults = 128, OffWorkingSet = 144;

    // Ein Eintrag der Prozessliste (für die Prüfung der Versätze und für Aufrufer, die alle Prozesse auf einmal brauchen)
    public sealed record ProcessEntry(int Pid, int ParentPid, string? ImageName, int Threads, long HardFaults, long PageFaults,
        long CreateTime, long WorkingSet);

    // Gepinnter Puffer (Pinned Object Heap), damit die Zeiger der Namen (ImageName.Buffer zeigt in den Puffer) gültig
    // bleiben; zwischen den Aufrufen behalten, die Abfrage läuft alle paar Sekunden.
    private static readonly object ListLock = new();
    private static byte[]? _list;

    // Harte Seitenfehler des Prozesses seit seinem Start; null = Prozess nicht in der Liste oder Abfrage gescheitert
    public static long? HardFaults(int pid)
    {
        long? result = null;
        if (!Walk(e => { if (ReadPid(e.Buffer, e.Offset) == pid) { result = (uint)BitConverter.ToInt32(e.Buffer, e.Offset + OffHardFaults); return false; } return true; }))
            return null;
        return result;
    }

    // Harte Seitenfehler aller Prozesse aus einer einzigen Abfrage (für mehrere Server im selben Takt)
    public static Dictionary<int, long>? AllHardFaults()
    {
        var map = new Dictionary<int, long>();
        return Walk(e => { map[ReadPid(e.Buffer, e.Offset)] = (uint)BitConverter.ToInt32(e.Buffer, e.Offset + OffHardFaults); return true; }) ? map : null;
    }

    // Alle Einträge mit den wichtigsten Feldern (vor allem für die Prüfung der Versätze)
    public static List<ProcessEntry>? Processes()
    {
        var list = new List<ProcessEntry>();
        return Walk(e =>
        {
            var b = e.Buffer; int o = e.Offset;
            list.Add(new ProcessEntry(ReadPid(b, o), (int)BitConverter.ToInt64(b, o + OffParentPid), ImageName(b, o, e.Base),
                BitConverter.ToInt32(b, o + OffThreads), (uint)BitConverter.ToInt32(b, o + OffHardFaults),
                (uint)BitConverter.ToInt32(b, o + OffPageFaults), BitConverter.ToInt64(b, o + OffCreateTime),
                BitConverter.ToInt64(b, o + OffWorkingSet)));
            return true;
        }) ? list : null;
    }

    private static int ReadPid(byte[] b, int o) => (int)BitConverter.ToInt64(b, o + OffPid);

    // UNICODE_STRING: Length (2), MaximumLength (2), Buffer (8 ab Versatz 8); der Zeiger zeigt in unseren Puffer
    private static string? ImageName(byte[] b, int o, long baseAddr)
    {
        int len = BitConverter.ToUInt16(b, o + OffImageName);
        long ptr = BitConverter.ToInt64(b, o + OffImageName + 8);
        long rel = ptr - baseAddr;
        if (len == 0 || ptr == 0 || rel < 0 || rel + len > b.Length) return null;
        return System.Text.Encoding.Unicode.GetString(b, (int)rel, len);
    }

    private readonly record struct Entry(byte[] Buffer, int Offset, long Base);

    // Liste lesen (Puffer wächst bei STATUS_INFO_LENGTH_MISMATCH) und Eintrag für Eintrag an onEntry geben, solange es true liefert.
    // false = Abfrage gescheitert.
    private static bool Walk(Func<Entry, bool> onEntry)
    {
        if (IntPtr.Size != 8) return false;
        try
        {
            lock (ListLock)
            {
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    _list ??= GC.AllocateUninitializedArray<byte>(512 << 10, pinned: true);
                    var basePtr = Marshal.UnsafeAddrOfPinnedArrayElement(_list, 0);
                    int rc = NtQuerySystemInformation(SystemProcessInformation, basePtr, _list.Length, out int needed);
                    if (rc == StatusInfoLengthMismatch)
                    {
                        // zwischen den Aufrufen können Prozesse dazukommen: Reserve obendrauf
                        long next = Math.Max((long)needed, _list.Length) + (256 << 10);
                        if (next > MaxListBytes) return false;
                        _list = GC.AllocateUninitializedArray<byte>((int)next, pinned: true);
                        continue;
                    }
                    if (rc != 0) return false;
                    int end = Math.Min(needed > 0 ? needed : _list.Length, _list.Length);
                    long baseAddr = (long)basePtr;
                    int off = 0;
                    while (off >= 0 && off + OffWorkingSet + 8 <= end)
                    {
                        if (!onEntry(new Entry(_list, off, baseAddr))) break;
                        int step = BitConverter.ToInt32(_list, off + OffNext);
                        if (step <= 0) break;
                        off += step;
                    }
                    return true;
                }
                return false;
            }
        }
        catch { return false; }
    }

    // ── Eingeblendete Dateien ──
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MemoryBasicInformation info, IntPtr len);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint K32GetMappedFileNameW(IntPtr h, IntPtr addr, [Out] char[] name, uint size);
    [DllImport("kernel32.dll")] private static extern bool K32QueryWorkingSetEx(IntPtr h, [In, Out] long[] info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint QueryDosDeviceW(string device, [Out] char[] target, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, [Out] char[] path, uint size, uint flags);

    // Zugriffsrechte (gemessen unter Windows 11 26200): VirtualQueryEx und K32GetMappedFileNameW gehen schon mit
    // PROCESS_QUERY_LIMITED_INFORMATION, K32QueryWorkingSetEx braucht PROCESS_QUERY_INFORMATION (sonst ERROR_ACCESS_DENIED).
    // PROCESS_VM_READ ist nicht nötig, es wird kein fremder Speicher gelesen. Ohne PROCESS_QUERY_INFORMATION (fremder
    // Benutzer, geschützter Prozess) gibt es daher keinen Rückfall: dann null.
    private const uint QueryInfo = 0x0400;
    private const uint MemCommit = 0x1000, MemMapped = 0x40000;
    private const int MaxSamples = 65536, ChunkPages = 4096;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation   // MEMORY_BASIC_INFORMATION (x64, 48 Byte)
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State, Protect, Type;
    }

    // Wie viel der Datei filePath im Prozess pid eingeblendet ist und wie viel davon im Working Set liegt.
    // Bei mehr als 65 536 Seiten wird gleichmäßig verteilt nur jede n-te Seite geprüft und hochgerechnet.
    // Seiten in der Standby-Liste (im RAM, aber nicht im Working Set) zählen als nicht vorhanden.
    public static MappedFileResidency? MappedResidency(int pid, string filePath, int maxSamples = MaxSamples)
    {
        if (IntPtr.Size != 8 || string.IsNullOrWhiteSpace(filePath) || maxSamples <= 0) return null;
        try
        {
            var names = DeviceNames(filePath);
            if (names.Count == 0) return null;
            var h = OpenProcess(QueryInfo, false, pid);
            if (h == IntPtr.Zero) return null;
            try { return Measure(h, names, maxSamples); }
            finally { CloseHandle(h); }
        }
        catch { return null; }
    }

    private static MappedFileResidency? Measure(IntPtr h, List<string> names, int maxSamples)
    {
        int pageSize = Environment.SystemPageSize;
        var regions = new List<(long Base, long Size)>();
        var nameBuf = new char[1024];
        long lastAlloc = -1; bool lastMatch = false;
        long addr = 0;
        int mbiSize = Marshal.SizeOf<MemoryBasicInformation>();
        while (VirtualQueryEx(h, (IntPtr)addr, out var m, mbiSize) != IntPtr.Zero)
        {
            long regionBase = (long)m.BaseAddress, size = (long)m.RegionSize;
            if (m.Type == MemMapped && m.State == MemCommit)
            {
                long alloc = (long)m.AllocationBase;
                if (alloc != lastAlloc)   // eine Ansicht kann aus mehreren Bereichen bestehen: Name nur einmal je Ansicht
                {
                    lastAlloc = alloc;
                    var mapped = MappedName(h, m.BaseAddress, ref nameBuf);
                    lastMatch = mapped != null && names.Exists(n => string.Equals(n, mapped, StringComparison.OrdinalIgnoreCase));
                }
                if (lastMatch) regions.Add((regionBase, size));
            }
            long next = regionBase + size;
            if (size <= 0 || next <= addr) break;
            addr = next;
        }
        if (regions.Count == 0) return null;

        long mappedBytes = 0, totalPages = 0;
        foreach (var r in regions) { mappedBytes += r.Size; totalPages += (r.Size + pageSize - 1) / pageSize; }
        long stride = Math.Max(1, (totalPages + maxSamples - 1) / maxSamples);

        // Seiten gleichmäßig über alle Bereiche: globaler Seitenindex 0, stride, 2·stride …
        var chunk = new long[2 * (int)Math.Min(ChunkPages, (totalPages + stride - 1) / stride)];
        int filled = 0, sampled = 0, resident = 0;
        long pageIndex = 0;   // Index der ersten Seite des aktuellen Bereichs
        long nextSample = 0;
        foreach (var r in regions)
        {
            long pages = (r.Size + pageSize - 1) / pageSize;
            while (nextSample < pageIndex + pages)
            {
                chunk[2 * filled] = r.Base + (nextSample - pageIndex) * pageSize;
                chunk[2 * filled + 1] = 0;
                if (++filled * 2 == chunk.Length)
                {
                    if (!Flush(h, chunk, filled, ref sampled, ref resident)) return null;
                    filled = 0;
                }
                nextSample += stride;
            }
            pageIndex += pages;
        }
        if (filled > 0 && !Flush(h, chunk, filled, ref sampled, ref resident)) return null;
        if (sampled == 0) return null;
        long residentBytes = (long)Math.Round(mappedBytes * (double)resident / sampled);
        return new MappedFileResidency(mappedBytes, residentBytes, sampled, resident);
    }

    // PSAPI_WORKING_SET_EX_INFORMATION: VirtualAddress (8), VirtualAttributes (8, Bit 0 = Valid = im Working Set)
    private static bool Flush(IntPtr h, long[] chunk, int count, ref int sampled, ref int resident)
    {
        if (!K32QueryWorkingSetEx(h, chunk, (uint)(count * 16))) return false;
        for (int i = 0; i < count; i++)
            if ((chunk[2 * i + 1] & 1) != 0) resident++;
        sampled += count;
        return true;
    }

    private static string? MappedName(IntPtr h, IntPtr addr, ref char[] buf)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint n = K32GetMappedFileNameW(h, addr, buf, (uint)buf.Length);
            if (n == 0) return null;
            if (n < buf.Length - 1) return new string(buf, 0, (int)n);
            buf = new char[buf.Length * 4];   // abgeschnitten: größerer Puffer
        }
        return null;
    }

    // NT-Gerätepfade der Datei (\Device\HarddiskVolumeN\…), wie K32GetMappedFileNameW sie liefert: der endgültige Pfad über
    // ein Handle (löst Laufwerksbuchstaben, SUBST, Verknüpfungen und Netzlaufwerke auf) und als Rückfall der Laufwerksbuchstabe
    // über QueryDosDeviceW. Anführungszeichen und Leerraum fallen weg, Schrägstriche werden über den vollen Pfad normalisiert.
    internal static List<string> DeviceNames(string filePath)
    {
        var list = new List<string>();
        string full;
        try { full = Path.GetFullPath(filePath.Trim().Trim('"', '\'').Trim()); }
        catch { return list; }

        try
        {
            using var fh = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new char[1024];
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint n = GetFinalPathNameByHandleW(fh, buf, (uint)buf.Length, 0x2 /* VOLUME_NAME_NT */);
                if (n == 0) break;
                if (n < buf.Length) { list.Add(new string(buf, 0, (int)n)); break; }
                buf = new char[n + 1];
            }
        }
        catch { }

        if (full.Length >= 3 && full[1] == ':' && full[2] == '\\')
        {
            var target = new char[1024];
            uint n = QueryDosDeviceW(full[..2], target, (uint)target.Length);
            if (n > 0)
            {
                var device = new string(target, 0, Array.IndexOf(target, '\0') is int z and >= 0 ? z : (int)n);
                if (device.Length > 0) Add(list, device.TrimEnd('\\') + full[2..]);
            }
        }
        else if (full.StartsWith(@"\\", StringComparison.Ordinal) && !full.StartsWith(@"\\?\", StringComparison.Ordinal)
                 && !full.StartsWith(@"\\.\", StringComparison.Ordinal))
            Add(list, @"\Device\Mup" + full[1..]);   // \\server\share\… → \Device\Mup\server\share\…
        return list;
    }

    private static void Add(List<string> list, string name)
    {
        if (!list.Exists(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) list.Add(name);
    }
}
