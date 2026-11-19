using System.Net;
using System.Net.Sockets;

namespace StykkerMeshShare.Server;

// Vorläufiger Schutz, bis die Kopplung (Stykker.Pairing) dazukommt: Anfragen nur aus dem eigenen Netz. Das sind diese
// Maschine, die privaten Bereiche des Heimnetzes, Link-Local und der Bereich, den Tailscale vergibt. Alles andere
// bekommt 403. Wer im Heimnetz ist, kommt damit an die Dateien – das steht so im README.
public static class NetworkAccess
{
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

    public static void Use(WebApplication app) => app.Use(async (context, next) =>
    {
        if (!Allowed(context.Connection.RemoteIpAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("StykkerMeshShare answers only inside the home network and Tailscale.");
            return;
        }
        await next();
    });

    public static bool IsLocal(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
}
