using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using StykkerMeshShare.Core;

namespace StykkerMeshShare.Server;

// Die Gegenstellen: von Hand eingetragene (settings.json, --peer) und im Heimnetz gefundene (UDP-Rundruf).
// Für jede läuft eine Schleife: Abgleich, dann lange Abfrage „hat sich bei dir etwas geändert?“, dann wieder Abgleich.
// Abgeglichen wird immer nur mit einer Gegenstelle zur Zeit, damit zwei Abrufe nicht um dieselbe Datei streiten.
public sealed class PeerManager : IHostedService, IDisposable
{
    private const string DiscoveryTag = "STYKKER-MESHSHARE/1";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(25);

    private readonly MeshNode _node;
    private readonly ServerSettings _settings;
    private readonly int _port;
    private readonly bool _discovery;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _pullGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Peer> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource _wake = new();

    public PeerManager(MeshNode node, ServerSettings settings, int port, bool discovery, IEnumerable<string> extraPeers)
    {
        _node = node;
        _settings = settings;
        _port = port;
        _discovery = discovery;
        foreach (var address in settings.Peers.Concat(extraPeers)) Track(address, manual: true);
        node.FolderChanged += _ => Wake();
    }

    public sealed class Peer
    {
        public required string Address { get; init; }
        public bool Manual { get; set; }
        public string? NodeId { get; set; }
        public string? Name { get; set; }
        public bool Online { get; set; }
        public bool IsSelf { get; set; }
        public bool Duplicate { get; set; }       // dieselbe Gegenstelle ist schon unter einer anderen Adresse verbunden
        public bool Syncing { get; set; }
        public DateTime? LastSync { get; set; }
        public string? LastError { get; set; }
        internal CancellationTokenSource Cancel { get; } = new();
    }

    public IReadOnlyList<Peer> Peers => _peers.Values.Where(p => !p.IsSelf).OrderBy(p => p.Name ?? p.Address, StringComparer.OrdinalIgnoreCase).ToList();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var peer in _peers.Values) _ = RunAsync(peer);
        if (_discovery)
        {
            _ = Task.Run(() => ListenAsync(_stop.Token));
            _ = Task.Run(() => AnnounceAsync(_stop.Token));
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        foreach (var peer in _peers.Values) peer.Cancel.Cancel();
        return Task.CompletedTask;
    }

    // Weckt alle Gegenstellen aus der langen Abfrage, damit sie sofort abgleichen.
    public void Wake()
    {
        var previous = Interlocked.Exchange(ref _wake, new CancellationTokenSource());
        previous.Cancel();   // nicht freigeben: eine Schleife kann das alte Token gerade noch lesen
    }

    public bool AddManual(string address)
    {
        _settings.AddPeer(address);
        if (_peers.TryGetValue(address, out var existing)) { existing.Manual = true; return true; }
        var peer = Track(address, manual: true);
        if (peer != null && !_stop.IsCancellationRequested) _ = RunAsync(peer);
        return peer != null;
    }

    public bool Remove(string address)
    {
        _settings.RemovePeer(address);
        if (!_peers.TryRemove(address, out var peer)) return false;
        peer.Cancel.Cancel();
        return true;
    }

    private Peer? Track(string address, bool manual)
    {
        var normalized = ServerSettings.NormalizeAddress(address, _port);
        if (normalized == null) return null;
        var peer = new Peer { Address = normalized, Manual = manual };
        return _peers.TryAdd(normalized, peer) ? peer : null;
    }

    private async Task RunAsync(Peer peer)
    {
        var source = new HttpPeerSource(_http, peer.Address);
        var ct = CancellationTokenSource.CreateLinkedTokenSource(peer.Cancel.Token, _stop.Token).Token;
        var backoff = TimeSpan.FromSeconds(3);
        long remoteSeq = -1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    helloTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                    var hello = await source.HelloAsync(helloTimeout.Token).ConfigureAwait(false);
                    if (hello.Id == _node.NodeId) { peer.IsSelf = true; return; }   // die eigene Adresse
                    peer.NodeId = hello.Id;
                    peer.Name = hello.Name;
                    remoteSeq = hello.Seq;
                }
                // Dieselbe Gegenstelle unter zwei Adressen (Heimnetz und Tailscale): nur eine gleicht ab.
                peer.Duplicate = _peers.Values.Any(other => other != peer && other.NodeId == peer.NodeId && other.Online && !other.Duplicate);
                if (peer.Duplicate)
                {
                    peer.Online = false;
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    continue;
                }

                bool complete = await PullAsync(peer, source, ct).ConfigureAwait(false);
                peer.Online = true;
                peer.LastError = null;
                backoff = TimeSpan.FromSeconds(3);

                // Warten, bis sich dort etwas ändert; dann wieder abgleichen.
                while (!ct.IsCancellationRequested)
                {
                    // Wake() (zum Beispiel nach einem Ordnerwechsel) bricht das Warten ab: dann sofort abgleichen.
                    long seq;
                    using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, _wake.Token))
                    {
                        try { seq = await source.WaitAsync(remoteSeq, WaitTimeout, wait.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested && wait.IsCancellationRequested)
                        {
                            complete = await PullAsync(peer, source, ct).ConfigureAwait(false);
                            continue;
                        }
                    }
                    if (seq != remoteSeq)
                    {
                        remoteSeq = seq;
                        complete = await PullAsync(peer, source, ct).ConfigureAwait(false);
                    }
                    else if (!complete || _node.PendingRescan)
                    {
                        // Beim letzten Mal blieb etwas liegen (Datei in Arbeit): noch einmal versuchen.
                        complete = await PullAsync(peer, source, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException)
            {
                peer.Online = false;
                peer.Syncing = false;
                peer.LastError = ex is OperationCanceledException ? "No answer" : ex.Message;
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
            }
        }
    }

    private async Task<bool> PullAsync(Peer peer, HttpPeerSource source, CancellationToken ct)
    {
        await _pullGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            peer.Syncing = true;
            var result = await _node.PullAsync(source, ct).ConfigureAwait(false);
            peer.LastSync = DateTime.UtcNow;
            peer.LastError = result.Complete ? null : "Some files are still in use; trying again.";
            return result.Complete;
        }
        finally
        {
            peer.Syncing = false;
            _pullGate.Release();
        }
    }

    // ── Suche im Heimnetz: ein kurzer Rundruf alle 20 Sekunden ──

    private string Announcement => $"{DiscoveryTag}|{_node.NodeId}|{_port}";

    private async Task AnnounceAsync(CancellationToken ct)
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        var bytes = Encoding.UTF8.GetBytes(Announcement);
        while (!ct.IsCancellationRequested)
        {
            try { await udp.SendAsync(bytes, new IPEndPoint(IPAddress.Broadcast, _port), ct).ConfigureAwait(false); }
            catch (SocketException) { }
            catch (OperationCanceledException) { return; }
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"[discovery] UDP port {_port} unavailable: {ex.Message}");
            return;
        }
        using (udp)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try { received = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { continue; }
                var parts = Encoding.UTF8.GetString(received.Buffer).Split('|');
                if (parts.Length != 3 || parts[0] != DiscoveryTag || parts[1] == _node.NodeId) continue;
                if (!int.TryParse(parts[2], out var port) || !NetworkAccess.Allowed(received.RemoteEndPoint.Address)) continue;
                var ip = received.RemoteEndPoint.Address;
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                if (_peers.Values.Any(p => p.NodeId == parts[1] && p.Online)) continue;
                var peer = Track(ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}", manual: false);
                if (peer != null) _ = RunAsync(peer);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
        _stop.Dispose();
    }
}
