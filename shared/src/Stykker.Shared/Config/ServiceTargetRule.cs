using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Stykker.Pairing;

namespace Stykker.Shared.Config;

// Wohin die Zentrale weiterleiten darf (docs/plan-stykkernode.md, Abschnitt 4.5). Das Token der Zentrale geht nur an
// diesen PC: an 127.0.0.1 oder localhost, an eine IP-Adresse dieses PCs (Local Control läuft im Heimnetz oder Tailnet)
// oder an einen Namen aus "hostNames" in stykker.json (etwa der Tailscale-Name, mit HTTPS). Alles andere wird abgelehnt.
//
// Die Regel selbst macht keinen Netzwerkzugriff: die Adressen dieses PCs und die Namen kommen als Parameter. Die
// Adressen liest ReadThisPcAddresses, und zwar nur dort, wo die Zentrale sie braucht.
public static class ServiceTargetRule
{
    private static readonly Regex TargetPattern = new(@"^(https?)://([^/?#@\s]+):(\d{1,5})/?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // "http://127.0.0.1:41999/" wird "http://127.0.0.1:41999": Schema und Name klein, der Port ist Pflicht, kein Pfad,
    // keine Suche, kein Benutzername. null, wenn das Ziel nicht erlaubt ist.
    public static string? Allowed(string? target, IReadOnlyCollection<string> ownAddresses, IReadOnlyCollection<string> hostNames)
    {
        if (target == null) return null;
        var match = TargetPattern.Match(target.Trim());
        if (!match.Success || !int.TryParse(match.Groups[3].Value, out var port) || port is < 1 or > 65535) return null;
        var host = match.Groups[2].Value;
        if (!HostAllowed(host, ownAddresses, hostNames)) return null;
        return $"{match.Groups[1].Value.ToLowerInvariant()}://{host.ToLowerInvariant()}:{port}";
    }

    // Ein Ziel, das nur dieser PC sein kann: die Schleife, eine seiner IPv4-Adressen oder einer seiner Namen aus der Datei.
    // Eine IP-Adresse muss in kanonischer Form stehen (wie IPAddress.ToString sie schreibt), damit es nur eine Schreibweise gibt.
    private static bool HostAllowed(string host, IReadOnlyCollection<string> ownAddresses, IReadOnlyCollection<string> hostNames)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1") return true;
        if (IPAddress.TryParse(host, out var address))
            return address.AddressFamily == AddressFamily.InterNetwork && address.ToString() == host && ownAddresses.Contains(host);
        // Ein Zusatzname gilt nur, wenn er selbst gültig ist (kein Platzhalter)
        return hostNames.Any(name => HostNames.IsValidName(name) && name.Equals(host, StringComparison.OrdinalIgnoreCase));
    }

    // Alle IPv4-Adressen dieses PCs auf den Schnittstellen, die gerade offen sind (ohne die Schleife). Zur Laufzeit gelesen,
    // damit eine neue Adresse (ein neues Netz, Tailscale) ohne Neustart gilt.
    public static IReadOnlyList<string> ReadThisPcAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .Distinct()
            .ToList();
}
