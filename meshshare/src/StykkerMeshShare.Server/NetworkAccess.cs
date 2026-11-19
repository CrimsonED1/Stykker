using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Stykker.Pairing;

namespace StykkerMeshShare.Server;

// Vorläufiger Schutz, bis die Kopplung (Stykker.Pairing) dazukommt:
//  1. Anfragen nur aus dem eigenen Netz: diese Maschine, die privaten Bereiche des Heimnetzes, Link-Local und der
//     Bereich, den Tailscale vergibt. Alles andere bekommt 403.
//  2. Der Host-Kopf muss ein Name dieses PCs sein (IP-Adresse, localhost, Rechnername, Rechnername mit DNS-Endung,
//     <Rechnername>.<tailnet>.ts.net oder ein Zusatzname aus stykker.json). Sonst könnte eine fremde Webseite per
//     DNS-Rebinding ihren Namen auf diesen PC zeigen lassen; ihre Anfragen kämen dann scheinbar von hier.
// Wer im Heimnetz ist, kommt damit an die Dateien – das steht so im README.
public static class NetworkAccess
{
    private static readonly string MachineName = Environment.MachineName;
    private static readonly string? DomainName = ReadDomain();
    private static readonly IReadOnlyList<string> ExtraNames = HostNames.LoadExtra(HostNames.DefaultFile());

    public static bool Allowed(IPAddress? address)
    {
        if (address == null) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);   // 100.64.0.0/10: Tailscale
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC                       // fc00::/7, darin auch Tailscale (fd7a:115c:a1e0::/48)
                || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);    // fe80::/10
        }
        return false;
    }

    public static bool HostAllowed(string? hostHeader)
    {
        if (HostNames.Allowed(hostHeader, MachineName, DomainName, ExtraNames)) return true;
        // Tailscale (MagicDNS): <rechnername>.<tailnet>.ts.net
        var name = (hostHeader ?? "").Trim();
        if (name.StartsWith('[')) return false;
        int colon = name.LastIndexOf(':');
        if (colon > 0) name = name[..colon];
        var parts = name.Split('.');
        return parts.Length == 4
            && parts[0].Equals(MachineName, StringComparison.OrdinalIgnoreCase)
            && parts[2].Equals("ts", StringComparison.OrdinalIgnoreCase)
            && parts[3].Equals("net", StringComparison.OrdinalIgnoreCase);
    }

    public static void Use(WebApplication app) => app.Use(async (context, next) =>
    {
        if (!Allowed(context.Connection.RemoteIpAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("StykkerMeshShare answers only inside the home network and Tailscale.");
            return;
        }
        if (!HostAllowed(context.Request.Host.Value))
        {
            context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            await context.Response.WriteAsync("Unknown host name. Use this PC's name or address, or add the name to hostNames in stykker.json.");
            return;
        }
        await next();
    });

    public static bool IsLocal(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

    private static string? ReadDomain()
    {
        try { return IPGlobalProperties.GetIPGlobalProperties().DomainName; }
        catch (NetworkInformationException) { return null; }
    }
}
