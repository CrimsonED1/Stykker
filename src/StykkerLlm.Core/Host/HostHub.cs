using System.Net.WebSockets;
using System.Text.Json;

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

// Die Seite des Servers (docs/plan-hosts-gateway.md, P2): nimmt die WebSockets der Hosts an. Je Host höchstens eine
// Verbindung – meldet er sich neu (Netz kurz weg), ersetzt die neue die alte. Stand: das letzte "state" des Hosts.
public sealed class HostHub
{
    private readonly HostRegistry _registry;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private readonly Dictionary<string, HostLive> _live = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WebSocket> _sockets = new(StringComparer.Ordinal);

    public HostHub(HostRegistry registry, Func<DateTime>? now = null)
    {
        _registry = registry;
        _now = now ?? (() => DateTime.Now);
    }

    public HostRegistry Registry => _registry;
    public event Action<string>? Log;

    // Alle gekoppelten Hosts, verbunden oder nicht
    public IReadOnlyList<HostLive> List()
    {
        lock (_gate)
            return _registry.List().Select(e => _live.TryGetValue(e.Id, out var l) ? l : new HostLive { Entry = e, Name = e.Name }).ToList();
    }

    // Eine Verbindung bedienen, bis sie endet. host ist schon geprüft (Token aus dem Kopf).
    public async Task HandleAsync(WebSocket ws, HostEntry host, string address, CancellationToken ct)
    {
        WebSocket? previous;
        HostLive live;
        lock (_gate)
        {
            _sockets.TryGetValue(host.Id, out previous);
            _sockets[host.Id] = ws;
            live = new HostLive { Entry = host, Name = host.Name, Connected = true, Since = _now(), LastSeen = _now(), Address = address };
            _live[host.Id] = live;
        }
        if (previous != null) Drop(previous);
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
            lock (_gate)
            {
                if (_sockets.TryGetValue(host.Id, out var current) && ReferenceEquals(current, ws))
                {
                    _sockets.Remove(host.Id);
                    live.Connected = false;
                }
            }
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
            switch (HostProtocol.TypeOf(root))
            {
                case "hello":
                    live.Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? live.Entry.Name : live.Entry.Name;
                    live.Version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                    break;
                case "state":
                    if (root.TryGetProperty("state", out var s)) live.State = StateSnapshot.Read(s);
                    break;
            }
        }
        catch (JsonException) { /* kaputte Nachricht: übergehen */ }
    }

    // Host entfernt: Verbindung trennen, Eintrag weg (das Token gilt dann nicht mehr)
    public Task<bool> RemoveAsync(string id)
    {
        WebSocket? ws;
        lock (_gate)
        {
            _sockets.Remove(id, out ws);
            _live.Remove(id);
        }
        if (ws != null) Drop(ws);
        return Task.FromResult(_registry.Remove(id));
    }

    // Alte oder entfernte Verbindung hart beenden: ein höfliches CloseAsync würde neben dem laufenden Lesen der
    // Verbindung selbst lesen wollen (zwei Leser auf einem WebSocket gehen nicht) und auf den Host warten
    private static void Drop(WebSocket ws)
    {
        try { ws.Abort(); } catch (ObjectDisposedException) { }
    }
}
