using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace StykkerLlm.Core;

// Ein Stykker im Heimnetz, gefunden über die Suche (UDP-Broadcast)
public sealed record DiscoveredNode(string Name, string Url, bool Remote, bool Self);

// Suche nach anderen Stykker-Servern im Heimnetz (docs/nodes.md, N1). Der Hub ruft einmal laut in alle Netze
// ("STYKKER-DISCOVER/1" an Port 17501), jeder Server mit eingeschaltetem Home/VPN antwortet mit Name und Port.
// Nur Name, Port und Schalter – kein Code, kein Schlüssel. Über VPN kommt ein Broadcast meist nicht an: dann hilft
// die Adresse von Hand.
public static class NodeDiscovery
{
    public const int Port = 17501;
    public const string Probe = "STYKKER-DISCOVER/1";

    // Antwort des Servers (klein, ohne Geheimnisse)
    public static string Reply(int webPort, bool remote) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["app"] = "StykkerLLM-Server", ["name"] = Environment.MachineName, ["port"] = webPort, ["remote"] = remote,
    });

    // Antwort lesen; null, wenn es keine Stykker-Antwort ist
    public static DiscoveredNode? Parse(string json, IPAddress from, IReadOnlyCollection<string> ownAddresses)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || J.Str(r, "app") != "StykkerLLM-Server") return null;
            int port = J.Int(r, "port");
            if (port <= 0 || port > 65535) return null;
            var ip = from.IsIPv4MappedToIPv6 ? from.MapToIPv4() : from;
            var host = ip.ToString();
            bool self = IPAddress.IsLoopback(ip) || ownAddresses.Contains(host);
            return new DiscoveredNode(J.Str(r, "name") ?? host, NetAddr.Url(host, port), J.Bool(r, "remote"), self);
        }
        catch (JsonException) { return null; }
    }

    // Einmal rufen und bis wait sammeln. Doppelte Antworten (mehrere Netzkarten) zählen einmal.
    public static async Task<List<DiscoveredNode>> SearchAsync(TimeSpan wait, CancellationToken ct = default)
    {
        var found = new Dictionary<string, DiscoveredNode>(StringComparer.OrdinalIgnoreCase);
        var own = NetInfo.Ipv4Addresses().ToHashSet(StringComparer.Ordinal);
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var probe = Encoding.UTF8.GetBytes(Probe);
        foreach (var target in BroadcastAddresses())
        {
            try { await udp.SendAsync(probe, probe.Length, new IPEndPoint(target, Port)).ConfigureAwait(false); }
            catch (SocketException) { /* Netz ohne Broadcast: weiter mit dem nächsten */ }
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(wait);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var res = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (Parse(Encoding.UTF8.GetString(res.Buffer), res.RemoteEndPoint.Address, own) is { } n) found[n.Url] = n;
            }
        }
        catch (OperationCanceledException) { /* Zeit um */ }
        catch (SocketException) { /* Netzkarte weg */ }
        return found.Values.OrderBy(n => n.Self).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // 255.255.255.255 plus die gerichteten Broadcasts jeder aktiven IPv4-Netzkarte (manche Router lassen nur diese durch)
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                    var ip = u.Address.GetAddressBytes();
                    var mask = u.IPv4Mask.GetAddressBytes();
                    var b = new byte[4];
                    for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                    var addr = new IPAddress(b);
                    if (!list.Contains(addr)) list.Add(addr);
                }
            }
        }
        catch (NetworkInformationException) { /* nur der allgemeine Broadcast */ }
        return list;
    }
}

// Die Antwortseite im Server: lauscht auf Port 17501 und antwortet nur, solange Home/VPN an ist.
// Ist der Port belegt (zweiter Server, anderes Programm), bleibt die Suche für diesen Rechner eben stumm.
public sealed class DiscoveryResponder : IDisposable
{
    private readonly Func<bool> _enabled;
    private readonly Func<string> _reply;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;

    public string? Error { get; private set; }

    public DiscoveryResponder(Func<bool> enabled, Func<string> reply)
    {
        _enabled = enabled;
        _reply = reply;
    }

    public void Start(int port = NodeDiscovery.Port)
    {
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch (SocketException ex)
        {
            Error = ex.Message;
            _udp?.Dispose();
            _udp = null;
            return;
        }
        var udp = _udp;
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            var probe = Encoding.UTF8.GetBytes(NodeDiscovery.Probe);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var res = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                    if (!_enabled() || !res.Buffer.AsSpan().SequenceEqual(probe)) continue;
                    var answer = Encoding.UTF8.GetBytes(_reply());
                    await udp.SendAsync(answer, answer.Length, res.RemoteEndPoint).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { /* einzelne kaputte Anfrage */ }
            }
        }, ct);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        _cts.Dispose();
    }
}
