using System.Net.NetworkInformation;

namespace StykkerLlm.Core;

// Die Host-Prüfung von StykkerLLM gegen DNS-Rebinding (Auftrag Host-Regel). Sie gilt vor allem anderen, für jeden Pfad:
// eine fremde Seite legt ihren Namen auf 127.0.0.1, und ohne diese Prüfung käme sie als Loopback herein und bekäme die
// Anmeldeseite mit dem Zugangscode. Netz aus: nur die Loopback-Namen, wie bisher. Netz an: der Rechnername, seine
// DNS-Endung, eine IP-Adresse und die Zusatznamen aus stykker.json (Stykker.Pairing, HostNames).
//
// Zwei Wege sind ausgenommen, die Model-Hosts: /hosts/connect (WebSocket) und POST /hosts/pair. Beide verleihen über ein
// Browser-Cookie keine Rechte. /hosts/connect verlangt das Host-Token im Kopf X-Stykker-Host, /hosts/pair den Host-Code,
// der nur auf der angemeldeten Seite Hosts steht und nach fünf Fehlversuchen wechselt. Eine Rebinding-Seite gewinnt dort
// nichts, und ein mit einem Namen gekoppelter StykkerHost bleibt verbunden. Für beide gilt weiter, was ohne diese Regel
// gilt: das Token bzw. der Code wird am Endpunkt geprüft, und bei ausgeschaltetem Netz gilt Schritt 2b des AccessGate.
public sealed record HostNameRule(string MachineName, string? DomainName, IReadOnlyCollection<string> ExtraNames)
{
    // Dieser PC: der Rechnername und die DNS-Endung wie beim Start, dazu die Zusatznamen (leer = keine)
    public static HostNameRule ForThisPc(IReadOnlyCollection<string> extraNames) =>
        new(Environment.MachineName, IPGlobalProperties.GetIPGlobalProperties().DomainName, extraNames);

    // Die Ausnahme der Model-Hosts (siehe oben)
    public static bool IsModelHostPath(string path, string method) =>
        path.Equals(HostProtocol.Path, StringComparison.OrdinalIgnoreCase)
        || (path.Equals("/hosts/pair", StringComparison.OrdinalIgnoreCase) && method.Equals("POST", StringComparison.OrdinalIgnoreCase));

    // Darf die Anfrage durch? remoteEnabled = der Netzschalter (Home/VPN). hostHeader = der Host-Kopf der Anfrage.
    public bool Allowed(string path, string method, string? hostHeader, bool remoteEnabled) =>
        IsModelHostPath(path, method)
        || (remoteEnabled
            ? Stykker.Pairing.HostNames.Allowed(hostHeader, MachineName, DomainName, ExtraNames)
            : LoopbackOnly(hostHeader));

    // Netz aus: nur die Loopback-Namen, unverändert zu der Prüfung, die das AccessGate schon hatte. Ein leerer Host
    // (HTTP/1.0 ohne Host-Kopf) gilt als eigener.
    public static bool LoopbackOnly(string? hostHeader)
    {
        var value = hostHeader ?? "";
        if (value.Length == 0) return true;
        // "[::1]:8078" -> "[::1]", "127.0.0.1:8078" -> "127.0.0.1"
        var name = value.StartsWith('[') ? (value.IndexOf(']') is var end and > 0 ? value[..(end + 1)] : value)
            : value.Contains(':') ? value[..value.IndexOf(':')] : value;
        return name is "127.0.0.1" or "localhost" or "::1" or "[::1]" or "0.0.0.0";
    }
}
