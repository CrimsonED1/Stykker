using System.Net;
using System.Runtime.InteropServices;
using StykkerLlm.Core;

namespace StykkerLlm.Platform.Windows;

// Windows-Zugriffe: TCP-Tabellen (IPv4 und IPv6). Die Leistungsindikatoren (GPU, Speicher) kommen aus Stykker.Shared.

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
}
