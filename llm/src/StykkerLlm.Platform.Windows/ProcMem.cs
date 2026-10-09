using System.Net;
using System.Runtime.InteropServices;
using StykkerLlm.Core;

namespace StykkerLlm.Platform.Windows;

// Windows-Zugriffe: TCP-Tabellen (IPv4 und IPv6) und GPU-Speicher über die Leistungsindikatoren "GPU Process Memory"
// (wie im Task-Manager).
internal static class ProcMem
{
    // ── TCP-Tabellen ──
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    private const int AfInet = 2, AfInet6 = 23;
    private const int ListenerClass = 3 /* TCP_TABLE_OWNER_PID_LISTENER */, ConnectionsClass = 4 /* TCP_TABLE_OWNER_PID_CONNECTIONS */;

    // Tabelle mit Puffer-Reserve lesen, bei ERROR_INSUFFICIENT_BUFFER (122) mit größerem Puffer wiederholen.
    // onRow bekommt den Zeiger auf je eine Zeile (MIB_TCPROW_OWNER_PID: 24 Byte, MIB_TCP6ROW_OWNER_PID: 56 Byte).
    private static bool ReadTcpTable(int af, int tableClass, int rowSize, Action<IntPtr> onRow)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, tableClass, 0);
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            size += 4096 * attempt;   // die Tabelle kann zwischen den Aufrufen wachsen
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                uint rc = GetExtendedTcpTable(buf, ref size, false, af, tableClass, 0);
                if (rc == 122) continue;
                if (rc != 0) return false;
                int n = Math.Min(Marshal.ReadInt32(buf), (size - 4) / rowSize);
                for (int i = 0; i < n; i++) onRow(buf + 4 + i * rowSize);
                return true;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return false;
    }

    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private static string Addr4(IntPtr p) => new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(p))).ToString();

    private static string Addr6(IntPtr p)
    {
        var b = new byte[16];
        Marshal.Copy(p, b, 0, 16);
        return new IPAddress(b).ToString();
    }

    public static List<ListenerInfo> Listeners()
    {
        var list = new List<ListenerInfo>();
        // IPv4: state(0) localAddr(4) localPort(8) remoteAddr(12) remotePort(16) pid(20)
        ReadTcpTable(AfInet, ListenerClass, 24, row => list.Add(new ListenerInfo(Addr4(row + 4), Port(Marshal.ReadInt32(row + 8)), Marshal.ReadInt32(row + 20))));
        // IPv6: localAddr(0,16) scope(16) localPort(20) remoteAddr(24,16) scope(40) remotePort(44) state(48) pid(52)
        ReadTcpTable(AfInet6, ListenerClass, 56, row => list.Add(new ListenerInfo(Addr6(row), Port(Marshal.ReadInt32(row + 20)), Marshal.ReadInt32(row + 52))));
        return list;
    }

    // Nur Zustand ESTABLISHED (5)
    public static List<ConnectionInfo> Connections()
    {
        var list = new List<ConnectionInfo>();
        ReadTcpTable(AfInet, ConnectionsClass, 24, row =>
        {
            if (Marshal.ReadInt32(row) != 5) return;
            list.Add(new ConnectionInfo(Addr4(row + 4), Port(Marshal.ReadInt32(row + 8)), Addr4(row + 12), Port(Marshal.ReadInt32(row + 16)), Marshal.ReadInt32(row + 20)));
        });
        ReadTcpTable(AfInet6, ConnectionsClass, 56, row =>
        {
            if (Marshal.ReadInt32(row + 48) != 5) return;
            list.Add(new ConnectionInfo(Addr6(row), Port(Marshal.ReadInt32(row + 20)), Addr6(row + 24), Port(Marshal.ReadInt32(row + 44)), Marshal.ReadInt32(row + 52)));
        });
        return list;
    }

    // ── PDH: GPU-Speicher pro Prozess ──
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);

    private const uint PdhFmtLarge = 0x400, PdhMoreData = 0x800007D2;

    public sealed class GpuMemQuery : IGpuMemoryQuery
    {
        public int Pid { get; }
        private readonly IntPtr _query, _dedicated, _shared;
        private readonly bool _ok;

        public GpuMemQuery(int pid)
        {
            Pid = pid;
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;
            _ok = PdhAddEnglishCounterW(_query, $@"\GPU Process Memory(pid_{pid}_*)\Dedicated Usage", IntPtr.Zero, out _dedicated) == 0
                & PdhAddEnglishCounterW(_query, $@"\GPU Process Memory(pid_{pid}_*)\Shared Usage", IntPtr.Zero, out _shared) == 0;
        }

        public (double DedicatedGb, double SharedGb)? Read()
        {
            if (!_ok || PdhCollectQueryData(_query) != 0) return null;
            return (Sum(_dedicated) / 1073741824.0, Sum(_shared) / 1073741824.0);
        }

        private static double Sum(IntPtr counter)
        {
            uint size = 0;
            if (PdhGetFormattedCounterArrayW(counter, PdhFmtLarge, ref size, out _, IntPtr.Zero) != PdhMoreData) return 0;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PdhFmtLarge, ref size, out uint count, buf) != 0) return 0;
                double sum = 0;
                // PDH_FMT_COUNTERVALUE_ITEM_W: Name-Zeiger (8), CStatus (4) + Ausrichtung (4), Wert (8) = 24 Byte
                for (int i = 0; i < count; i++) sum += Marshal.ReadInt64(buf + i * 24 + 16);
                return sum;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public void Dispose() { if (_query != IntPtr.Zero) PdhCloseQuery(_query); }
    }

    // GPU-Speicher aller Prozesse auf einmal (Platzhalter-Abfrage, teuer: höchstens alle paar Sekunden aufrufen)
    public sealed class GpuProcessQuery : IDisposable
    {
        private readonly IntPtr _query, _dedicated;
        private readonly bool _ok;
        private readonly Dictionary<int, string> _names = new();
        private DateTime _namesAt = DateTime.Now;

        public GpuProcessQuery()
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;
            _ok = PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out _dedicated) == 0;
        }

        // Die größten Belegungen (dedizierter VRAM) mit Prozessname
        public List<(string Name, double Gb)>? ReadTop(int count)
        {
            if (!_ok || PdhCollectQueryData(_query) != 0) return null;
            uint size = 0;
            if (PdhGetFormattedCounterArrayW(_dedicated, PdhFmtLarge, ref size, out _, IntPtr.Zero) != PdhMoreData) return new();
            var buf = Marshal.AllocHGlobal((int)size);
            var perPid = new Dictionary<int, long>();
            try
            {
                if (PdhGetFormattedCounterArrayW(_dedicated, PdhFmtLarge, ref size, out uint n, buf) != 0) return null;
                for (int i = 0; i < n; i++)
                {
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf + i * 24)) ?? "";   // "pid_1234_luid_..._phys_0"
                    long val = Marshal.ReadInt64(buf + i * 24 + 16);
                    if (!name.StartsWith("pid_") || val <= 0) continue;
                    int end = name.IndexOf('_', 4);
                    if (end < 0 || !int.TryParse(name.AsSpan(4, end - 4), out int pid)) continue;
                    perPid[pid] = perPid.GetValueOrDefault(pid) + val;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return perPid.OrderByDescending(k => k.Value).Take(count)
                         .Select(k => (NameOf(k.Key), k.Value / 1073741824.0)).ToList();
        }

        private string NameOf(int pid)
        {
            if ((DateTime.Now - _namesAt).TotalMinutes > 5) { _names.Clear(); _namesAt = DateTime.Now; }   // PIDs werden wiederverwendet
            if (_names.TryGetValue(pid, out var n)) return n;
            try { using var pr = System.Diagnostics.Process.GetProcessById(pid); n = pr.ProcessName; } catch { n = $"PID {pid}"; }
            if (_names.Count > 200) _names.Clear();
            return _names[pid] = n;
        }

        public void Dispose() { if (_query != IntPtr.Zero) PdhCloseQuery(_query); }
    }

    // GPU-Auslastung je Prozess (Prozent) über die Leistungsindikatoren "GPU Engine" (wie im Task-Manager).
    // Ein Prozess hat mehrere Engines (3D, Copy, Video …): je PID summiert, größte zuerst.
    public sealed class GpuUtilQuery : IDisposable
    {
        private const uint PdhFmtDouble = 0x00000200;
        private readonly IntPtr _query, _util;
        private readonly bool _ok;
        private readonly Dictionary<int, string> _names = new();
        private DateTime _namesAt = DateTime.Now;

        public GpuUtilQuery()
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;
            _ok = PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _util) == 0;
            PdhCollectQueryData(_query);   // Prozent-Zähler brauchen eine erste Abtastung
        }

        public List<(string Name, double Percent)>? ReadTop(int count)
        {
            if (!_ok || PdhCollectQueryData(_query) != 0) return null;   // zweite Abtastung: Prozent seit dem letzten Aufruf
            uint size = 0;
            if (PdhGetFormattedCounterArrayW(_util, PdhFmtDouble, ref size, out _, IntPtr.Zero) != PdhMoreData) return new();
            var buf = Marshal.AllocHGlobal((int)size);
            var perPid = new Dictionary<int, double>();
            try
            {
                if (PdhGetFormattedCounterArrayW(_util, PdhFmtDouble, ref size, out uint n, buf) != 0) return null;
                for (int i = 0; i < n; i++)
                {
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf + i * 24)) ?? "";   // "pid_1234_luid_..._engtype_3D"
                    double val = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buf + i * 24 + 16));
                    if (!name.StartsWith("pid_") || val <= 0) continue;
                    int end = name.IndexOf('_', 4);
                    if (end < 0 || !int.TryParse(name.AsSpan(4, end - 4), out int pid)) continue;
                    perPid[pid] = perPid.GetValueOrDefault(pid) + val;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return perPid.OrderByDescending(k => k.Value).Take(count)
                         .Select(k => (NameOf(k.Key), Math.Min(100.0, k.Value))).ToList();
        }

        private string NameOf(int pid)
        {
            if ((DateTime.Now - _namesAt).TotalMinutes > 5) { _names.Clear(); _namesAt = DateTime.Now; }   // PIDs werden wiederverwendet
            if (_names.TryGetValue(pid, out var n)) return n;
            try { using var pr = System.Diagnostics.Process.GetProcessById(pid); n = pr.ProcessName; } catch { n = $"PID {pid}"; }
            if (_names.Count > 200) _names.Clear();
            return _names[pid] = n;
        }

        public void Dispose() { if (_query != IntPtr.Zero) PdhCloseQuery(_query); }
    }
}
