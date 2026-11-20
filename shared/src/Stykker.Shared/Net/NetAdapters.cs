using System.Net.NetworkInformation;

namespace Stykker.Shared.Net;

// Ein Netzwerkadapter mit seinem Durchsatz: Empfangen und Senden in Byte je Sekunde (null beim ersten Takt), die
// Verbindungsgeschwindigkeit in Byte je Sekunde (0 = unbekannt) und die Art. Tunnel sind VPNs wie Tailscale: ihr Verkehr
// läuft zusätzlich über einen echten Adapter und zählt deshalb nicht in die Summe (wie bei NetCounters).
public sealed record NetAdapterRow(string Id, string Name, string Description, NetAdapterKind Kind, double LinkBytesPerSec,
    double? RxBytesPerSec, double? TxBytesPerSec)
{
    public bool CountsInTotal => Kind != NetAdapterKind.Tunnel;
}

public enum NetAdapterKind { Ethernet, Wifi, Tunnel, Other }

// Liest die verbundenen Adapter einzeln (ohne Loopback) und rechnet je Adapter den Durchsatz seit dem letzten Aufruf.
// Nur .NET-Bordmittel. Darf von mehreren Threads gerufen werden.
public sealed class NetAdapters
{
    private readonly Dictionary<string, (long Rx, long Tx, DateTime At)> _prev = new();
    private readonly object _lock = new();

    public IReadOnlyList<NetAdapterRow>? Read()
    {
        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { return null; }
        var now = DateTime.UtcNow;
        var rows = new List<NetAdapterRow>();
        lock (_lock)
        {
            var seen = new HashSet<string>();
            foreach (var ni in all)
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.OperationalStatus != OperationalStatus.Up) continue;
                if (IsFilter(ni.Name)) continue;
                long rx, tx;
                try
                {
                    var stats = ni.GetIPStatistics();
                    rx = stats.BytesReceived;
                    tx = stats.BytesSent;
                }
                catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { continue; }
                seen.Add(ni.Id);
                double? rxRate = null, txRate = null;
                if (_prev.TryGetValue(ni.Id, out var prev))
                {
                    double sec = (now - prev.At).TotalSeconds;
                    if (sec >= NetRateMeter.MinSeconds && rx >= prev.Rx && tx >= prev.Tx)
                    {
                        rxRate = (rx - prev.Rx) / sec;
                        txRate = (tx - prev.Tx) / sec;
                    }
                }
                if (!_prev.TryGetValue(ni.Id, out var old) || (now - old.At).TotalSeconds >= NetRateMeter.MinSeconds)
                    _prev[ni.Id] = (rx, tx, now);
                rows.Add(new NetAdapterRow(ni.Id, ni.Name, ni.Description, KindOf(ni), ni.Speed > 0 ? ni.Speed / 8.0 : 0, rxRate, txRate));
            }
            foreach (var id in _prev.Keys.Where(k => !seen.Contains(k)).ToList()) _prev.Remove(id);
        }
        return rows.OrderBy(r => r.Kind == NetAdapterKind.Tunnel).ThenByDescending(r => (r.RxBytesPerSec ?? 0) + (r.TxBytesPerSec ?? 0))
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Nach einer Pause: die nächste Messung setzt nur die Basis.
    public void Reset() { lock (_lock) _prev.Clear(); }

    // Windows meldet zu jedem Adapter seine Filter-Treiber als eigene Schnittstellen mit denselben Zählern, etwa
    // „Ethernet-QoS Packet Scheduler-0000“ oder „Ethernet-WFP Native MAC Layer LightWeight Filter-0000“. Sie würden den
    // Verkehr mehrfach zählen und fallen weg: Name endet auf „-0000“ (vier Ziffern) und nennt einen Filter oder Scheduler.
    public static bool IsFilter(string name)
    {
        int dash = name.LastIndexOf('-');
        if (dash < 0 || name.Length - dash != 5 || !name[(dash + 1)..].All(char.IsAsciiDigit)) return false;
        return name.Contains("Filter", StringComparison.OrdinalIgnoreCase) || name.Contains("Scheduler", StringComparison.OrdinalIgnoreCase);
    }

    // Tailscale und andere VPNs melden sich teils als „Unknown“; der Name verrät sie.
    public static NetAdapterKind KindOf(NetworkInterface ni) => KindOf(ni.NetworkInterfaceType, ni.Name, ni.Description);

    public static NetAdapterKind KindOf(NetworkInterfaceType type, string name, string description)
    {
        string text = name + " " + description;
        if (type == NetworkInterfaceType.Tunnel || text.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Wintun", StringComparison.OrdinalIgnoreCase) || text.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VPN", StringComparison.OrdinalIgnoreCase))
            return NetAdapterKind.Tunnel;
        if (type == NetworkInterfaceType.Wireless80211) return NetAdapterKind.Wifi;
        if (type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit) return NetAdapterKind.Ethernet;
        return NetAdapterKind.Other;
    }
}
