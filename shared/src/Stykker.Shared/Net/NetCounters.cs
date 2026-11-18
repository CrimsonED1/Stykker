using System.Net.NetworkInformation;

namespace Stykker.Shared.Net;

// Liest die Byte-Zähler der Netzwerkadapter und macht daraus mit NetRateMeter den Durchsatz. Gezählt werden alle Adapter,
// die verbunden sind, ohne Loopback und Tunnel (sonst zählte ein VPN den Verkehr doppelt), IPv4 und IPv6 zusammen.
// Nur .NET-Bordmittel, also auch außerhalb von Windows lesbar. Darf von mehreren Threads gerufen werden.
public sealed class NetCounters
{
    private readonly NetRateMeter _meter = new();
    private readonly object _lock = new();

    // Durchsatz seit dem letzten Aufruf; null beim ersten Aufruf und wenn die Zähler nicht lesbar sind
    public NetSample? Read()
    {
        var totals = ReadTotals();
        if (totals == null) return null;
        lock (_lock) return _meter.Next(totals.Value, DateTime.UtcNow);
    }

    // Die nächste Abfrage setzt nur die Basis (nach einer Pause, damit die Rate nicht über die Pause gemittelt wird)
    public void Reset() { lock (_lock) _meter.Reset(); }

    public static NetTotals? ReadTotals()
    {
        long rx = 0, tx = 0;
        double link = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                try
                {
                    var stats = ni.GetIPStatistics();
                    rx += stats.BytesReceived;
                    tx += stats.BytesSent;
                    if (ni.Speed > 0) link += ni.Speed / 8.0;
                }
                catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { return null; }
        return new NetTotals(rx, tx, link);
    }
}
