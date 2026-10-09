using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace StykkerLlm.Core;

// Wohin der Host sich verbindet: Server-Adresse und Token (host.json im Datenordner des Hosts, an den Benutzer gebunden).
// Geschrieben wird sie von der Kopplung (P3).
public sealed class HostConfig
{
    public const string FileName = "host.json";
    public string Server { get; set; } = "";
    public string Token { get; set; } = "";
    public string ServerName { get; set; } = "";

    // Eine halb geschriebene oder von Hand geänderte Datei (null statt Text) darf den Host nicht umwerfen
    public bool Paired => !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(Token);

    public static HostConfig Load(AppPaths paths, IPlatform platform) => ProtectedJson.Load<HostConfig>(Path.Combine(paths.Root, FileName), platform);
    public void Save(AppPaths paths, IPlatform platform) => ProtectedJson.Save(Path.Combine(paths.Root, FileName), this, platform);

    // http://server:17400 → ws://server:17400/hosts/connect (https → wss)
    public static Uri ConnectUri(string server)
    {
        var b = new UriBuilder(server.TrimEnd('/'));
        b.Scheme = b.Scheme == "https" ? "wss" : "ws";
        b.Path = HostProtocol.Path;
        return b.Uri;
    }
}

public enum HostLinkState { NotPaired, Connecting, Connected, Refused, Waiting }

// Die Verbindung des Hosts zum Server: verbinden, "hello", dann jede Sekunde "state". Bricht sie ab, wird nach
// 1, 2, 5, 10, 30 s neu verbunden (danach alle 30 s). Lehnt der Server das Token ab, wird nicht weiter gehämmert:
// dann alle 60 s ein Versuch (vielleicht wurde der Host am Server entfernt und neu gekoppelt).
public sealed class HostLinkClient
{
    private static readonly int[] Backoff = { 1, 2, 5, 10, 30 };
    private readonly HostConfig _config;
    private readonly Func<string> _state;
    private readonly string _name, _version;
    private readonly Func<CancellationToken, Task<WebSocket>> _connect;
    private readonly TimeSpan _interval;
    private readonly Func<string, JsonElement, CancellationToken, Task<HostReply>>? _onCommand;
    private readonly HttpClient _local;

    public HostLinkState State { get; private set; } = HostLinkState.Connecting;
    public string LastError { get; private set; } = "";
    public DateTime? ConnectedSince { get; private set; }
    public event Action<string>? Log;

    // onCommand: führt Befehle des Servers aus (start, stop, unload, models); local: HttpClient für den Tunnel zu den
    // Modellservern dieses PCs (nur localhost)
    public HostLinkClient(HostConfig config, Func<string> state, string name, string version, TimeSpan? interval = null,
        Func<CancellationToken, Task<WebSocket>>? connect = null,
        Func<string, JsonElement, CancellationToken, Task<HostReply>>? onCommand = null, HttpMessageHandler? local = null)
    {
        _onCommand = onCommand;
        _local = local == null ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan } : new HttpClient(local, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _config = config;
        _state = state;
        _name = name;
        _version = version;
        _interval = interval ?? TimeSpan.FromSeconds(1);
        _connect = connect ?? ConnectAsync;
        if (!config.Paired) State = HostLinkState.NotPaired;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_config.Paired) { State = HostLinkState.NotPaired; return; }
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            State = HostLinkState.Connecting;
            bool refused = false;
            try
            {
                using var ws = await _connect(ct).ConfigureAwait(false);
                State = HostLinkState.Connected;
                ConnectedSince = DateTime.Now;
                LastError = "";
                attempt = 0;
                Log?.Invoke($"link: connected to {_config.Server}");
                await SessionAsync(ws, ct).ConfigureAwait(false);
                Log?.Invoke("link: connection closed");
            }
            catch (HostRefusedException)
            {
                refused = true;
                LastError = Strings.HostRefused;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or TimeoutException or OperationCanceledException)
            {
                LastError = ex.Message;
            }
            ConnectedSince = null;
            if (ct.IsCancellationRequested) break;
            State = refused ? HostLinkState.Refused : HostLinkState.Waiting;
            var wait = refused ? 60 : Backoff[Math.Min(attempt++, Backoff.Length - 1)];
            if (LastError.Length > 0) Log?.Invoke($"link: {LastError} – next try in {wait} s");
            try { await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    // Eine Sitzung: hello, dann Stand im Takt; gleichzeitig Befehle und Tunnel-Anfragen des Servers bedienen.
    // Gesendet wird immer unter einer Sperre (ein Sender je WebSocket).
    private async Task SessionAsync(WebSocket ws, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var send = new SemaphoreSlim(1, 1);
        var tunnels = new ConcurrentDictionary<string, CancellationTokenSource>();
        Task Send(string text) => HostProtocol.SendAsync(ws, text, session.Token, send);

        await Send(HostProtocol.Hello(_name, _version)).ConfigureAwait(false);
        var reader = Task.Run(async () =>
        {
            try
            {
                while (await HostProtocol.ReceiveAsync(ws, session.Token).ConfigureAwait(false) is { } text)
                    Dispatch(text, Send, tunnels, session.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
            session.Cancel();   // Server hat geschlossen: Sitzung beenden
        });
        try
        {
            while (!session.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Send(HostProtocol.State(_state())).ConfigureAwait(false);
                await Task.Delay(_interval, session.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        }
        finally
        {
            session.Cancel();
            foreach (var t in tunnels.Values) t.Cancel();
            await reader.ConfigureAwait(false);
        }
    }

    private void Dispatch(string text, Func<string, Task> send, ConcurrentDictionary<string, CancellationTokenSource> tunnels, CancellationToken ct)
    {
        JsonElement root;
        try { using var doc = JsonDocument.Parse(text); root = doc.RootElement.Clone(); }
        catch (JsonException) { return; }
        var id = HostProtocol.Str(root, "id");
        switch (HostProtocol.TypeOf(root))
        {
            case "cmd":
                _ = Task.Run(async () =>
                {
                    HostReply reply;
                    var name = HostProtocol.Str(root, "name");
                    var args = root.TryGetProperty("args", out var a) ? a : default;
                    try { reply = _onCommand == null ? new HostReply(false, Strings.HostCommandUnknown(name)) : await _onCommand(name, args, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { reply = new HostReply(false, ex.Message); }
                    Log?.Invoke($"command {name}: {(reply.Ok ? "ok" : reply.Message)}");
                    try { await send(HostProtocol.Reply(id, reply.Ok, reply.Message, reply.Data is { } d ? System.Text.Json.Nodes.JsonNode.Parse(d.GetRawText()) : null)).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
                }, ct);
                break;
            case "http":
                var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tunnels[id] = cts;
                _ = Task.Run(async () =>
                {
                    try { await TunnelAsync(id, root, send, cts.Token).ConfigureAwait(false); }
                    finally { tunnels.TryRemove(id, out _); cts.Dispose(); }
                }, ct);
                break;
            case "http-cancel":
                if (tunnels.TryGetValue(id, out var c)) c.Cancel();
                break;
        }
    }

    // Eine Anfrage an einen Modellserver dieses PCs ausführen und die Antwort in Stücken zurückschicken.
    // Nur an localhost: der Tunnel darf nicht zum Sprungbrett in das Netz des Hosts werden.
    private async Task TunnelAsync(string id, JsonElement root, Func<string, Task> send, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(HostProtocol.Str(root, "url"), UriKind.Absolute, out var url) || url.Scheme != "http"
                || !(NetAddr.IsLoopback(url.Host) || url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
            {
                await send(HostProtocol.HttpEnd(id, Strings.HostTunnelOnlyLocal)).ConfigureAwait(false);
                return;
            }
            using var req = new HttpRequestMessage(new HttpMethod(HostProtocol.Str(root, "method") is { Length: > 0 } m ? m : "GET"), url);
            var body = HostProtocol.Str(root, "body");
            if (body.Length > 0) req.Content = new ByteArrayContent(Convert.FromBase64String(body));
            foreach (var (k, v) in HostProtocol.Headers(root))
            {
                if (k.Equals("Host", StringComparison.OrdinalIgnoreCase) || k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                if (!req.Headers.TryAddWithoutValidation(k, v)) req.Content?.Headers.TryAddWithoutValidation(k, v);
            }
            using var resp = await _local.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var headers = resp.Headers.Concat(resp.Content.Headers)
                .Where(h => !h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(h => new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value)));
            await send(HostProtocol.HttpHead(id, (int)resp.StatusCode, headers)).ConfigureAwait(false);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[HostProtocol.BodyChunk];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                await send(HostProtocol.HttpBody(id, buffer.AsSpan(0, n))).ConfigureAwait(false);
            await send(HostProtocol.HttpEnd(id)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException or InvalidOperationException or OperationCanceledException)
        {
            try { await send(HostProtocol.HttpEnd(id, ct.IsCancellationRequested ? "cancelled" : ex.Message)).ConfigureAwait(false); }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }

    private async Task<WebSocket> ConnectAsync(CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader(HostProtocol.TokenHeader, _config.Token);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        ws.Options.CollectHttpResponseDetails = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await ws.ConnectAsync(HostConfig.ConnectUri(_config.Server), timeout.Token).ConfigureAwait(false);
            return ws;
        }
        catch (WebSocketException) when (ws.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            ws.Dispose();
            throw new HostRefusedException();
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }
}

// Der Server kennt das Token nicht (Host am Server entfernt) oder lässt von außen niemanden herein (Home/VPN aus)
public sealed class HostRefusedException : Exception
{
    public HostRefusedException() : base(Strings.HostRefused) { }
}
