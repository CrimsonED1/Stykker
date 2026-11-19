using System.Net;
using System.Net.Sockets;

namespace StykkerMeshShare.Core;

// Über welchen Weg eine Gegenstelle erreicht wird. Kleiner ist besser: dieselbe Gegenstelle im Heimnetz und über
// Tailscale gleicht über das Heimnetz ab (schneller, kein Umweg über das Tailscale-Relay).
public enum PeerRoute { ThisPc = 0, Lan = 1, Tailscale = 2, Other = 3 }

public static class NetworkRoute
{
    public static PeerRoute Classify(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return PeerRoute.ThisPc;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return PeerRoute.Tailscale;   // 100.64.0.0/10
            if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254))
                return PeerRoute.Lan;
            return PeerRoute.Other;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (b[0] == 0xFD && b[1] == 0x7A && b[2] == 0x11 && b[3] == 0x5C && b[4] == 0xA1 && b[5] == 0xE0) return PeerRoute.Tailscale;  // fd7a:115c:a1e0::/48
            if ((b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)) return PeerRoute.Lan;
        }
        return PeerRoute.Other;
    }

    // Für eine Adresse wie "192.168.1.5:17404", "[fe80::1]:17404" oder "pc.example.ts.net:17404".
    // Ein Name wird aufgelöst; zählt die erste Adresse (mit der verbindet sich auch der HTTP-Client zuerst).
    public static async Task<PeerRoute> ResolveAsync(string address, CancellationToken ct)
    {
        var host = HostOf(address);
        if (IPAddress.TryParse(host, out var ip)) return Classify(ip);
        if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)) return PeerRoute.Tailscale;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses.Length == 0 ? PeerRoute.Other : Classify(addresses[0]);
        }
        catch (SocketException) { return PeerRoute.Other; }
    }

    public static string HostOf(string address)
    {
        if (address.StartsWith('['))
        {
            int end = address.IndexOf(']');
            return end > 1 ? address[1..end] : address;
        }
        int colon = address.LastIndexOf(':');
        return colon > 0 && address.IndexOf(':') == colon ? address[..colon] : address;
    }

    // Dieselbe Gegenstelle unter mehreren Adressen: eine neue Adresse wartet, solange eine andere über einen mindestens
    // so guten Weg abgleicht. Ist sie besser, löst sie alle aktiven ab (Rückgabe: welche).
    public static bool MustWait(PeerRoute mine, IEnumerable<PeerRoute> activeOthers) => activeOthers.Any(route => route <= mine);

    public static string Label(PeerRoute route) => route switch
    {
        PeerRoute.ThisPc => "this PC",
        PeerRoute.Lan => "LAN",
        PeerRoute.Tailscale => "Tailscale",
        _ => "network",
    };
}
