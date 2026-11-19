using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using StykkerMeshShare.Core;

namespace StykkerMeshShare.Server;

// Die Gegenstellen: von Hand eingetragene (settings.json, --peer) und im Heimnetz gefundene (UDP-Rundruf).
// Für jede läuft eine Schleife: Abgleich, dann lange Abfrage „hat sich bei dir etwas geändert?“, dann wieder Abgleich.
// Abgeglichen wird immer nur mit einer Gegenstelle zur Zeit, damit zwei Abrufe nicht um dieselbe Datei streiten.
//
// Dieselbe Gegenstelle kann unter mehreren Adressen bekannt sein (Heimnetz und Tailscale). Abgeglichen wird nur über
// eine, und zwar über den besten Weg (Route: dieser PC, dann Heimnetz, dann Tailscale). Taucht ein besserer Weg auf,
// bricht der schlechtere sofort ab, auch mitten in einer Übertragung; fällt der bessere weg, übernimmt der andere.
public sealed class PeerManager : IHostedService, IDisposable
{
    private const string DiscoveryTag = "STYKKER-MESHSHARE/1";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan AnnounceEvery = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ForgetFoundAfter = TimeSpan.FromMinutes(10);
    private const int MaxFound = 64;                                     // selbst gefundene Adressen höchstens
    private static readonly TimeSpan ReplyEvery = TimeSpan.FromSeconds(5); // je Absender höchstens eine Antwort

    private readonly MeshNode _node;
    private readonly ServerSettings _settings;
    private readonly int _port;
    private readonly bool _discovery;
    private readonly HttpClient _http = HttpPeerSource.CreateClient();
    private readonly SemaphoreSlim _pullGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Peer> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _routeLock = new();
    private CancellationTokenSource _wake = new();
    private TaskCompletionSource _announceNow = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        public PeerRoute Route { get; set; } = PeerRoute.Other;
        public bool Online { get; set; }
        public bool IsSelf { get; set; }
        public bool Duplicate { get; set; }       // dieselbe Gegenstelle ist schon über einen besseren Weg verbunden
        public bool Syncing { get; set; }
        public DateTime? LastSync { get; set; }
        public DateTime? OfflineSince { get; set; }
        public string? LastError { get; set; }
        internal CancellationTokenSource Cancel { get; } = new();
        internal CancellationTokenSource? Round { get; set; }   // ein Durchgang: Hallo, Abgleich, Warten
        internal volatile bool InBackoff;                        // wartet nach einem Fehler auf den nächsten Versuch
    }

    public IReadOnlyList<Peer> Peers => _peers.Values.Where(p => !p.IsSelf)
        .OrderBy(p => p.Duplicate).ThenBy(p => p.Name ?? p.Address, StringComparer.OrdinalIgnoreCase).ToList();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var peer in _peers.Values) _ = RunAsync(peer);
        if (_discovery)
        {
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            _ = Task.Run(() => ListenAsync(_stop.Token));
            _ = Task.Run(() => AnnounceAsync(_stop.Token));
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
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
        var normalized = ServerSettings.NormalizeAddress(address, _port);
        if (normalized != null && _peers.TryGetValue(normalized, out var existing)) { existing.Manual = true; return true; }
        var peer = Track(address, manual: true);
        if (peer != null && !_stop.IsCancellationRequested) _ = RunAsync(peer);
        return peer != null;
    }

    public bool Remove(string address)
    {
        _settings.RemovePeer(address, _port);
        if (!_peers.TryRemove(address, out var peer)) return false;
        peer.Cancel.Cancel();
        WakeStandbys(peer);
        return true;
    }

    private Peer? Track(string address, bool manual)
    {
        var normalized = ServerSettings.NormalizeAddress(address, _port);
        if (normalized == null) return null;
        var peer = new Peer { Address = normalized, Manual = manual };
        return _peers.TryAdd(normalized, peer) ? peer : null;
    }

    // Bricht den laufenden Durchgang einer Gegenstelle ab (Warten, Abgleich oder Pause nach einem Fehler).
    private static void Interrupt(Peer peer)
    {
        try { peer.Round?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    // Die Gegenstelle ist weg: andere Adressen desselben PCs, die nur zugeschaut haben, versuchen es sofort.
    private void WakeStandbys(Peer gone)
    {
        if (gone.NodeId == null) return;
        foreach (var other in _peers.Values)
            if (other != gone && other.NodeId == gone.NodeId && other.Duplicate) Interrupt(other);
    }

    private async Task RunAsync(Peer peer)
    {
        var source = new HttpPeerSource(_http, peer.Address);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(peer.Cancel.Token, _stop.Token);
        var ct = linked.Token;
        var backoff = TimeSpan.FromSeconds(3);
        while (!ct.IsCancellationRequested)
        {
            using var round = CancellationTokenSource.CreateLinkedTokenSource(ct);
            peer.Round = round;
            var rt = round.Token;
            try
            {
                long remoteSeq;
                using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(rt))
                {
                    helloTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                    var hello = await source.HelloAsync(helloTimeout.Token).ConfigureAwait(false);
                    if (hello.Id == _node.NodeId) { peer.IsSelf = true; return; }   // die eigene Adresse
                    peer.NodeId = hello.Id;
                    peer.Name = hello.Name;
                    remoteSeq = hello.Seq;
                    peer.Route = await NetworkRoute.ResolveAsync(peer.Address, helloTimeout.Token).ConfigureAwait(false);
                }

                if (!TakeOver(peer))
                {
                    // Derselbe PC ist schon über einen mindestens so guten Weg verbunden: nur zuschauen.
                    await Task.Delay(TimeSpan.FromSeconds(30), rt).ConfigureAwait(false);
                    continue;
                }

                bool complete = await PullAsync(peer, source, rt).ConfigureAwait(false);
                peer.LastError = null;
                backoff = TimeSpan.FromSeconds(3);

                // Warten, bis sich dort etwas ändert; dann wieder abgleichen.
                while (!rt.IsCancellationRequested)
                {
                    // Wake() (zum Beispiel nach einem Ordnerwechsel) bricht das Warten ab: dann sofort abgleichen.
                    long seq;
                    using (var wait = CancellationTokenSource.CreateLinkedTokenSource(rt, _wake.Token))
                    {
                        try { seq = await source.WaitAsync(remoteSeq, WaitTimeout, wait.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!rt.IsCancellationRequested && wait.IsCancellationRequested)
                        {
                            complete = await PullAsync(peer, source, rt).ConfigureAwait(false);
                            continue;
                        }
                    }
                    if (seq != remoteSeq)
                    {
                        remoteSeq = seq;
                        complete = await PullAsync(peer, source, rt).ConfigureAwait(false);
                    }
                    else if (!complete || _node.PendingRescan)
                    {
                        // Beim letzten Mal blieb etwas liegen (Datei in Arbeit): noch einmal versuchen.
                        complete = await PullAsync(peer, source, rt).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (OperationCanceledException) when (rt.IsCancellationRequested)
            {
                // Ein besserer Weg hat übernommen, oder der bisherige ist weg: neuer Durchgang ohne Pause.
                peer.Syncing = false;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException)
            {
                bool wasActive = peer.Online;
                peer.Online = false;
                peer.Syncing = false;
                peer.Duplicate = false;
                peer.OfflineSince ??= DateTime.UtcNow;
                peer.LastError = ex is OperationCanceledException ? "No answer" : ex.Message;
                if (wasActive) WakeStandbys(peer);

                if (await BackoffAsync(peer, backoff, rt, ct).ConfigureAwait(false) is not { } next) return;
                backoff = next;
            }
            catch (Exception) when (ct.IsCancellationRequested) { return; }   // beim Beenden
            catch (Exception ex)
            {
                // Unerwartet (zum Beispiel ein Fehler auf der Platte): nicht still sterben. Wie ein Verbindungsfehler
                // behandeln, damit die Seite „offline“ zeigt und eine andere Adresse desselben PCs übernehmen kann.
                Console.Error.WriteLine($"[mesh] {peer.Address}: {ex.GetType().Name}: {ex.Message}");
                bool wasActive = peer.Online;
                peer.Online = false;
                peer.Syncing = false;
                peer.Duplicate = false;
                peer.OfflineSince ??= DateTime.UtcNow;
                peer.LastError = ex.Message;
                if (wasActive) WakeStandbys(peer);
                if (await BackoffAsync(peer, backoff, rt, ct).ConfigureAwait(false) is not { } next) return;
                backoff = next;
            }
            finally { peer.Round = null; }
        }
    }

    // Pause nach einem Fehler. null: die Schleife endet (gestoppt, oder eine gefundene Adresse wird vergessen).
    private async Task<TimeSpan?> BackoffAsync(Peer peer, TimeSpan backoff, CancellationToken rt, CancellationToken ct)
    {
        // Selbst gefundene Adressen, die lange nicht antworten (neue IP, PC weg), werden vergessen.
        if (!peer.Manual && DateTime.UtcNow - peer.OfflineSince > ForgetFoundAfter)
        {
            _peers.TryRemove(new KeyValuePair<string, Peer>(peer.Address, peer));
            return null;
        }
        peer.InBackoff = true;
        try
        {
            await Task.Delay(backoff, rt).ConfigureAwait(false);
            return TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return TimeSpan.FromSeconds(3); }   // geweckt
        catch (OperationCanceledException) { return null; }
        finally { peer.InBackoff = false; }
    }

    // Entscheidet, ob diese Adresse für ihren PC abgleicht. Ja, wenn keine andere Adresse desselben PCs über einen
    // mindestens so guten Weg verbunden ist; schlechtere Wege werden dann abgelöst (laufende Übertragung bricht ab).
    private bool TakeOver(Peer peer)
    {
        List<Peer> demoted;
        lock (_routeLock)
        {
            var same = _peers.Values.Where(o => o != peer && !o.IsSelf && o.NodeId == peer.NodeId).ToList();
            if (NetworkRoute.MustWait(peer.Route, same.Where(o => o.Online && !o.Duplicate).Select(o => o.Route)))
            {
                peer.Duplicate = true;
                peer.Online = false;
                return false;
            }
            peer.Duplicate = false;
            peer.Online = true;
            peer.OfflineSince = null;
            demoted = same.Where(o => o.Online && !o.Duplicate).ToList();
            foreach (var other in demoted)
            {
                other.Duplicate = true;
                other.Online = false;
            }
        }
        foreach (var other in demoted)
        {
            Console.WriteLine($"[mesh] {peer.Name}: using {NetworkRoute.Label(peer.Route)} ({peer.Address}) instead of {NetworkRoute.Label(other.Route)} ({other.Address})");
            Interrupt(other);
        }
        return true;
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

    // ── Suche im Heimnetz ──
    // Ein kurzer Rundruf alle 20 Sekunden, sofort nach dem Start und wenn sich das Netz ändert. Gerufen wird in jedes
    // Heimnetz einzeln (die Broadcast-Adresse jedes Netzwerkadapters), weil 255.255.255.255 bei mehreren Adaptern
    // (VPN, Hyper-V, Tailscale) nur über einen hinausgeht. Wer einen Ruf hört, antwortet direkt: so finden sich zwei
    // PCs in Sekunden statt erst beim nächsten Rundruf.

    private string Announcement(bool reply) => $"{DiscoveryTag}|{_node.NodeId}|{_port}" + (reply ? "|reply" : "");

    private void OnNetworkChanged(object? sender, EventArgs e) => _announceNow.TrySetResult();

    internal static List<IPAddress> BroadcastAddresses()
    {
        var result = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (NetworkRoute.Classify(unicast.Address) != PeerRoute.Lan) continue;   // kein Tailscale, kein öffentliches Netz
                    var ip = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    if (mask.Length != 4 || mask.All(b => b == 0)) continue;
                    var broadcast = new byte[4];
                    for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    var address = new IPAddress(broadcast);
                    if (!result.Contains(address)) result.Add(address);
                }
            }
        }
        catch (NetworkInformationException) { }
        return result;
    }

    private async Task AnnounceAsync(CancellationToken ct)
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        var bytes = Encoding.UTF8.GetBytes(Announcement(reply: false));
        while (!ct.IsCancellationRequested)
        {
            foreach (var target in BroadcastAddresses())
            {
                try { await udp.SendAsync(bytes, new IPEndPoint(target, _port), ct).ConfigureAwait(false); }
                catch (SocketException) { }
                catch (OperationCanceledException) { return; }
            }
            var kick = _announceNow.Task;
            try { await Task.WhenAny(Task.Delay(AnnounceEvery, ct), kick).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (kick.IsCompleted)
            {
                _announceNow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }   // das neue Netz braucht einen Moment
                catch (OperationCanceledException) { return; }
            }
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
            var reply = Encoding.UTF8.GetBytes(Announcement(reply: true));
            var replied = new Dictionary<IPAddress, DateTime>();
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try { received = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { continue; }
                var parts = Encoding.UTF8.GetString(received.Buffer).Split('|');
                if (parts.Length is < 3 or > 4 || parts[0] != DiscoveryTag || parts[1] == _node.NodeId) continue;
                if (!int.TryParse(parts[2], out var port) || port is < 1 or > 65535) continue;
                var ip = received.RemoteEndPoint.Address;
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                if (!NetworkAccess.Allowed(ip)) continue;
                bool isReply = parts.Length == 4 && parts[3] == "reply";

                // Antworten, damit der Rufer uns sofort kennt (auf eine Antwort wird nicht geantwortet). Je Absender
                // höchstens alle paar Sekunden: sonst ließe sich mit gefälschten Rufen ein Gerät mit Antworten fluten.
                var now = DateTime.UtcNow;
                if (replied.Count > 1024) foreach (var old in replied.Where(r => now - r.Value > ReplyEvery).Select(r => r.Key).ToList()) replied.Remove(old);
                if (!isReply && (!replied.TryGetValue(ip, out var last) || now - last > ReplyEvery))
                {
                    replied[ip] = now;
                    try { await udp.SendAsync(reply, new IPEndPoint(ip, port), ct).ConfigureAwait(false); }
                    catch (SocketException) { }
                    catch (OperationCanceledException) { return; }
                }

                var route = NetworkRoute.Classify(ip);
                // Schon über einen mindestens so guten Weg verbunden: nichts zu tun. Nur über Tailscale verbunden:
                // diese Heimnetz-Adresse trotzdem aufnehmen, sie übernimmt dann.
                if (_peers.Values.Any(p => p.NodeId == parts[1] && p.Online && p.Route <= route)) continue;
                var address = ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}";
                if (_peers.TryGetValue(address, out var known))
                {
                    if (known.InBackoff) Interrupt(known);   // meldet sich wieder: nicht erst die Pause abwarten
                    continue;
                }
                if (_peers.Values.Count(p => !p.Manual) >= MaxFound) continue;
                var peer = Track(address, manual: false);
                if (peer != null) _ = RunAsync(peer);
            }
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _stop.Cancel();
        _http.Dispose();
        _stop.Dispose();
    }
}
