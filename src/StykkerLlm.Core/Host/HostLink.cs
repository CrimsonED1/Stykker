using System.Net.WebSockets;

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

    // http://server:8078 → ws://server:8078/hosts/connect (https → wss)
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

    public HostLinkState State { get; private set; } = HostLinkState.Connecting;
    public string LastError { get; private set; } = "";
    public DateTime? ConnectedSince { get; private set; }
    public event Action<string>? Log;

    public HostLinkClient(HostConfig config, Func<string> state, string name, string version, TimeSpan? interval = null,
        Func<CancellationToken, Task<WebSocket>>? connect = null)
    {
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

    // Eine Sitzung: hello, dann Stand im Takt; Nachrichten des Servers werden gelesen (Befehle ab P4)
    private async Task SessionAsync(WebSocket ws, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await HostProtocol.SendAsync(ws, HostProtocol.Hello(_name, _version), session.Token).ConfigureAwait(false);
        var reader = Task.Run(async () =>
        {
            try { while (await HostProtocol.ReceiveAsync(ws, session.Token).ConfigureAwait(false) != null) { } }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
            session.Cancel();   // Server hat geschlossen: Sitzung beenden
        });
        try
        {
            while (!session.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await HostProtocol.SendAsync(ws, HostProtocol.State(_state()), session.Token).ConfigureAwait(false);
                await Task.Delay(_interval, session.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        finally
        {
            session.Cancel();
            await reader.ConfigureAwait(false);
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
