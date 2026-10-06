using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace StykkerLlm.Core;

// Adressen dieses Rechners, damit andere Geräte (Handy im Heimnetz) den Server erreichen können.
// Nur Anzeige: der Server bindet immer auf allen Adressen, gesperrt werden fremde Zugriffe im Middleware (siehe AccessControl).
public static class NetInfo
{
    // Nur IPv4: die meisten Heimrouter vergeben IPv4, und ein QR-Code mit http://…:8078 ist für ein Handy verständlicher.
    // Sortiert: privates Netz zuerst (192.168, 10, 172.16-31), dann der Rest.
    public static IReadOnlyList<string> Ipv4Addresses()
    {
        var list = new List<(int Rank, string Address)>();
        foreach (var nic in SafeNics())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (IsVirtualAdapter(nic.Name)) continue;
            foreach (var unicast in SafeAddresses(nic))
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = unicast.Address.ToString();
                if (ip.StartsWith("127.", StringComparison.Ordinal)) continue;
                if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;   // APIPA: keine Adresse vom Router
                list.Add((Rank(ip), ip));
            }
        }
        return list.OrderBy(x => x.Rank).ThenBy(x => x.Address, StringComparer.Ordinal)
            .Select(x => x.Address).Distinct(StringComparer.Ordinal).ToList();
    }

    // Die Adresse, die im QR-Code steht: privates Netz bevorzugt, damit das Handy im gleichen WLAN bleibt.
    public static string? LanAddress() => Ipv4Addresses().FirstOrDefault();

    public static string LanAddressOrLoopback() => LanAddress() ?? "127.0.0.1";

    // http://192.168.1.23:8078/pair?code=ABCD2345
    public static string PairUrl(int port, string code, string? address = null)
    {
        string host = address is { Length: > 0 } ? address : LanAddressOrLoopback();
        return $"{NetAddr.Url(host, port)}/pair?code={Uri.EscapeDataString(code)}";
    }

    // 192.168 = 0, 10. = 1, 172.16-31 = 2, alles andere (Carrier Grade NAT, VPN) = 3
    private static int Rank(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4 || !int.TryParse(parts[0], out var a)) return 4;
        if (a == 192 && parts[1] == "168") return 0;
        if (a == 10) return 1;
        if (a == 172 && int.TryParse(parts[1], out var b) && b >= 16 && b <= 31) return 2;
        return 3;
    }

    private static bool IsVirtualAdapter(string name)
    {
        string[] virtualNames = { "vethernet", "virtualbox", "vmware", "hyper-v", "loopback", "isatap", "bluetooth", "teredo", "tap-windows" };
        var n = name.ToLowerInvariant();
        return virtualNames.Any(v => n.Contains(v, StringComparison.Ordinal));
    }

    private static IEnumerable<NetworkInterface> SafeNics()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces(); } catch { return Array.Empty<NetworkInterface>(); }
    }

    private static IEnumerable<UnicastIPAddressInformation> SafeAddresses(NetworkInterface nic)
    {
        try { return nic.GetIPProperties().UnicastAddresses; } catch { return Array.Empty<UnicastIPAddressInformation>(); }
    }
}
