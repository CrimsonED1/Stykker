using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StykkerLlm.Core;

// Ein Host, wie der Server ihn gerade sieht: verbunden oder nicht, seit wann, sein letzter Stand
public sealed class HostLive
{
    public required HostEntry Entry { get; init; }
    public bool Connected { get; internal set; }
    public DateTime? Since { get; internal set; }
    public DateTime? LastSeen { get; internal set; }
    public string Name { get; internal set; } = "";
    public string Version { get; internal set; } = "";
    public string Address { get; internal set; } = "";
    public StateSnapshot? State { get; internal set; }
}

// Die Seite des Servers (docs/plan-hosts-gateway.md): nimmt die WebSockets der Hosts an, je Host höchstens eine
// Verbindung (eine neue ersetzt die alte). Darüber laufen der Stand des Hosts, Befehle (CommandAsync) und Anfragen an
// seine Modellserver (TunnelAsync, gestreamt).
public sealed class HostHub
{
    private sealed class Conn(WebSocket ws)
    {
        public WebSocket Ws { get; } = ws;
        public SemaphoreSlim Send { get; } = new(1, 1);
    }

    private sealed class Tunnel
    {
        public required string HostId { get; init; }
        public TaskCompletionSource<HttpResponseMessage> Head { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ChunkStream Body { get; } = new();
    }

    private readonly HostRegistry _registry;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private readonly Dictionary<string, HostLive> _live = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Conn> _conns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string HostId, TaskCompletionSource<HostReply> Reply)> _replies = new();
    private readonly ConcurrentDictionary<string, Tunnel> _tunnels = new();

    public HostHub(HostRegistry registry, Func<DateTime>? now = null)
    {
        _registry = registry;
        _now = now ?? (() => DateTime.Now);
        Pairing = new HostPairing(registry, _now);
    }

    public HostRegistry Registry => _registry;
    // Der Code, mit dem sich ein neuer Host koppelt (Seite Hosts)
    public HostPairing Pairing { get; }
    public event Action<string>? Log;

    // Alle gekoppelten Hosts, verbunden oder nicht
    public IReadOnlyList<HostLive> List()
    {
        lock (_gate)
            return _registry.List().Select(e => _live.TryGetValue(e.Id, out var l) ? l : new HostLive { Entry = e, Name = e.Name }).ToList();
    }

    public HostLive? Find(string id) => List().FirstOrDefault(h => h.Entry.Id == id);

    // Ein HttpClient, der Anfragen durch den Tunnel an die Modellserver des Hosts schickt (null, wenn er nicht verbunden ist)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HttpClient> _clients = new();
    public HttpClient? ClientFor(string hostId) =>
        Find(hostId)?.Connected == true
            ? _clients.GetOrAdd(hostId, id => new HttpClient(new HostTunnelHandler(this, id)) { Timeout = Timeout.InfiniteTimeSpan })
            : null;

    // Eine Verbindung bedienen, bis sie endet. host ist schon geprüft (Token aus dem Kopf).
    public async Task HandleAsync(WebSocket ws, HostEntry host, string address, CancellationToken ct)
    {
        var conn = new Conn(ws);
        Conn? previous;
        HostLive live;
        lock (_gate)
        {
            _conns.TryGetValue(host.Id, out previous);
            _conns[host.Id] = conn;
            live = new HostLive { Entry = host, Name = host.Name, Connected = true, Since = _now(), LastSeen = _now(), Address = address };
            _live[host.Id] = live;
        }
        if (previous != null) Drop(previous.Ws);
        Log?.Invoke($"host connected: {host.Name} ({address})");
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var text = await HostProtocol.ReceiveAsync(ws, ct).ConfigureAwait(false);
                if (text == null) break;
                Handle(live, text);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
        finally
        {
            // Der Host hat ordentlich geschlossen: ordentlich antworten (sonst wartet er auf uns)
            if (ws.State == WebSocketState.CloseReceived)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
            }
            bool current;
            lock (_gate)
            {
                current = _conns.TryGetValue(host.Id, out var c) && ReferenceEquals(c, conn);
                if (current)
                {
                    _conns.Remove(host.Id);
                    live.Connected = false;
                }
            }
            if (current) FailPending(host.Id, Strings.HostGone(live.Name));
            Log?.Invoke($"host disconnected: {host.Name}");
        }
    }

    private void Handle(HostLive live, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            live.LastSeen = _now();
            var id = HostProtocol.Str(root, "id");
            switch (HostProtocol.TypeOf(root))
            {
                case "hello":
                    live.Name = HostProtocol.Str(root, "name") is { Length: > 0 } n ? n : live.Entry.Name;
                    live.Version = HostProtocol.Str(root, "version");
                    break;
                case "state":
                    if (root.TryGetProperty("state", out var s)) live.State = StateSnapshot.Read(s);
                    break;
                case "reply":
                    if (_replies.TryRemove(id, out var pending))
                    {
                        bool ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                        JsonElement? data = root.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null ? d.Clone() : null;
                        pending.Reply.TrySetResult(new HostReply(ok, HostProtocol.Str(root, "message"), data));
                    }
                    break;
                case "http-head":
                    if (_tunnels.TryGetValue(id, out var th))
                    {
                        int status = root.TryGetProperty("status", out var st) && st.TryGetInt32(out var sv) ? sv : 502;
                        var resp = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(th.Body) };
                        foreach (var (k, v) in HostProtocol.Headers(root))
                            if (!resp.Headers.TryAddWithoutValidation(k, v)) resp.Content.Headers.TryAddWithoutValidation(k, v);
                        th.Head.TrySetResult(resp);
                    }
                    break;
                case "http-body":
                    if (_tunnels.TryGetValue(id, out var tb)) tb.Body.Write(Convert.FromBase64String(HostProtocol.Str(root, "data")));
                    break;
                case "http-end":
                    if (_tunnels.TryRemove(id, out var te))
                    {
                        var error = HostProtocol.Str(root, "error");
                        if (error.Length > 0 && !te.Head.Task.IsCompleted) te.Head.TrySetException(new HttpRequestException(error));
                        te.Body.Complete(error.Length > 0 ? new IOException(error) : null);
                    }
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { /* kaputte Nachricht: übergehen */ }
    }

    // Befehl an einen Host; wartet auf seine Antwort (oder Zeitgrenze, oder Host weg)
    public async Task<HostReply> CommandAsync(string hostId, string name, JsonObject? args = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var conn = ConnOf(hostId);
        if (conn == null) return new HostReply(false, Strings.HostNotConnected);
        var id = Guid.NewGuid().ToString("N")[..12];
        var tcs = new TaskCompletionSource<HostReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _replies[id] = (hostId, tcs);
        try
        {
            await HostProtocol.SendAsync(conn.Ws, HostProtocol.Cmd(id, name, args), ct, conn.Send).ConfigureAwait(false);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            await using var _ = limit.Token.Register(() => tcs.TrySetResult(new HostReply(false, Strings.HostNoAnswer)));
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            return new HostReply(false, Strings.HostGone(hostId));
        }
        finally { _replies.TryRemove(id, out _); }
    }

    // Anfrage an einen Modellserver des Hosts (url aus Sicht des Hosts, z. B. http://127.0.0.1:8081/v1/chat/completions).
    // Die Antwort kommt, sobald der Kopf da ist; der Inhalt streamt weiter.
    public async Task<HttpResponseMessage> TunnelAsync(string hostId, HttpRequestMessage request, CancellationToken ct)
    {
        var conn = ConnOf(hostId) ?? throw new HttpRequestException(Strings.HostNotConnected);
        var id = Guid.NewGuid().ToString("N")[..12];
        var tunnel = new Tunnel { HostId = hostId };
        tunnel.Body.OnAbandoned = () => _ = CancelTunnel(id);
        _tunnels[id] = tunnel;
        var headers = request.Headers.Select(h => new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value))).ToList();
        byte[]? body = null;
        if (request.Content != null)
        {
            body = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            headers.AddRange(request.Content.Headers.Select(h => new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value))));
        }
        try
        {
            await HostProtocol.SendAsync(conn.Ws, HostProtocol.Http(id, request.Method.Method, request.RequestUri?.ToString() ?? "", headers, body), ct, conn.Send).ConfigureAwait(false);
            await using var reg = ct.Register(() =>
            {
                tunnel.Head.TrySetCanceled(ct);
                _ = CancelTunnel(id);
            });
            return await tunnel.Head.Task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            _tunnels.TryRemove(id, out _);
            throw new HttpRequestException(Strings.HostGone(hostId), ex);
        }
    }

    private async Task CancelTunnel(string id)
    {
        if (!_tunnels.TryRemove(id, out var t)) return;
        t.Body.Complete();
        if (ConnOf(t.HostId) is { } conn)
        {
            try { await HostProtocol.SendAsync(conn.Ws, HostProtocol.HttpCancel(id), CancellationToken.None, conn.Send).ConfigureAwait(false); }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { }
        }
    }

    // Host weg: wartende Befehle und Tunnel dieses Hosts sofort beenden, statt sie in die Zeitgrenze laufen zu lassen
    private void FailPending(string hostId, string why)
    {
        foreach (var (id, p) in _replies.Where(r => r.Value.HostId == hostId).ToList())
            if (_replies.TryRemove(id, out _)) p.Reply.TrySetResult(new HostReply(false, why));
        foreach (var (id, t) in _tunnels.Where(r => r.Value.HostId == hostId).ToList())
            if (_tunnels.TryRemove(id, out _))
            {
                t.Head.TrySetException(new HttpRequestException(why));
                t.Body.Complete(new IOException(why));
            }
    }

    private Conn? ConnOf(string hostId) { lock (_gate) return _conns.TryGetValue(hostId, out var c) ? c : null; }

    // Host entfernt: Verbindung trennen, Eintrag weg (das Token gilt dann nicht mehr)
    public Task<bool> RemoveAsync(string id)
    {
        Conn? conn;
        lock (_gate)
        {
            _conns.Remove(id, out conn);
            _live.Remove(id);
        }
        if (conn != null) Drop(conn.Ws);
        return Task.FromResult(_registry.Remove(id));
    }

    // Alte oder entfernte Verbindung hart beenden: ein höfliches CloseAsync würde neben dem laufenden Lesen der
    // Verbindung selbst lesen wollen (zwei Leser auf einem WebSocket gehen nicht) und auf den Host warten
    private static void Drop(WebSocket ws)
    {
        try { ws.Abort(); } catch (ObjectDisposedException) { }
    }
}

// Ein HttpMessageHandler, der Anfragen durch den Tunnel zu einem Host schickt – so kann jeder HttpClient einen
// Modellserver auf einem Host ansprechen, als wäre er lokal (Proxy, Tests). Die Adresse ist die aus Sicht des Hosts.
public sealed class HostTunnelHandler(HostHub hub, string hostId) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        hub.TunnelAsync(hostId, request, ct);
}
