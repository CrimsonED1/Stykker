using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace StykkerLlm.Core;

// Ziel einer weitergeleiteten Anfrage. LocalBody != null: der Proxy antwortet selbst (kein Weiterleiten).
// ServerKey ist der Server, dem die Aufzeichnung der Anfrage zugeordnet wird (beim Router das tatsächliche Ziel).
// Headers sind Kopfzeilen, die der Proxy selbst setzt (z. B. der API-Schlüssel eines Cloud-Anbieters); ein Authorization des
// Clients wird dann nicht weitergereicht. Client: ein eigener Weg zum Ziel (Tunnel zu einem Model-Host), sonst der des Proxys.
// LocalStream: der Proxy antwortet selbst, schreibt die Antwort aber Stück für Stück (Anthropic-Brücke). Der Handler bekommt
// den Ausgabestrom und den Mitschreiber, schreibt Kopfzeilen und Rumpf selbst und liefert den Status für die Aufzeichnung.
// TargetKey: die Kennung des Router-Ziels (ProxyTarget.Key), damit ein Fehlschlag diesem Ziel zugeordnet werden kann (Failover).
public sealed record ProxyRoute(string Upstream, string ServerKey, byte[]? Body,
    string? LocalContentType = null, byte[]? LocalBody = null, int LocalStatus = 200,
    IReadOnlyList<(string Name, string Value)>? Headers = null, HttpClient? Client = null,
    Func<Stream, ProxyTap, CancellationToken, Task<int>>? LocalStream = null, string? Route = null, string? TargetKey = null);

// Warum eine Weiterleitung scheiterte, bevor ein Byte an den Client ging (dann darf der Router ein anderes Ziel nennen):
// Unreachable = keine Verbindung (Server abgestürzt, startet neu), Unavailable = 503 (lädt noch), ContextOverflow = 400 „Kontext zu klein“,
// Overloaded = 429 oder 500/502/529 (bei Cloud-Anbietern: Rate-Limit oder Störung; der Router leitet das nur bei Cloud-Zielen um).
public enum ProxyFailure { Unreachable, Unavailable, ContextOverflow, Overloaded }

// Ein Versuch einer Anfrage: welche Ziele schon scheiterten und seit wann gewartet wird (Failover und Warten auf den Neustart)
public sealed class ProxyAttempts
{
    public DateTime Started { get; } = DateTime.UtcNow;
    public int Count { get; set; }
    public HashSet<string> Failed { get; } = new(StringComparer.Ordinal);
}

// Kleiner lokaler Durchreich-Proxy (nur Loopback) vor einem llama-server. Reicht alle Anfragen unverändert weiter (auch Streaming/SSE),
// liest bei Generierungs-Anfragen mit (ProxyTap) und meldet je Anfrage einen ProxyRecord. Aus: Server bleibt direkt erreichbar.
//
// Eigene HTTP/1.1-Schicht auf TcpListener (IPv4- und IPv6-Loopback), nicht HttpListener: http.sys würde den Port auf allen Adaptern
// öffnen. Hier bindet nichts außer 127.0.0.1 und ::1; zusätzlich wird die Gegenstelle und der Host-Header geprüft (DNS-Rebinding).
// Jede Verbindung bedient genau eine Anfrage (Connection: close). Der Anfragetext liegt im Speicher (Limit MaxBodyBytes).
public class RequestProxy : IDisposable
{
    public const long MaxBodyBytes = 64L * 1024 * 1024;
    private const int MaxHeadBytes = 64 * 1024;
    private const int MaxConnections = 64;

    // Kopfzeilen, die nur für eine Verbindung gelten (werden nicht weitergereicht, Länge und Kodierung setzt der Proxy selbst)
    internal static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization", "Transfer-Encoding", "Upgrade", "TE", "Trailer",
        "Host", "Content-Length", "Expect",
    };

    // Kopfzeilen, die zum Inhalt gehören (HttpContent.Headers statt HttpRequestMessage.Headers)
    internal static readonly HashSet<string> ContentHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Allow", "Content-Disposition", "Content-Encoding", "Content-Language", "Content-Location", "Content-MD5", "Content-Range", "Content-Type", "Expires", "Last-Modified",
    };

    private static readonly string[] GenerationPaths = { "/v1/chat/completions", "/chat/completions", "/v1/completions", "/completions", "/completion", "/infill", "/v1/messages" };

    private static readonly byte[] Crlf = { (byte)'\r', (byte)'\n' };
    private static readonly byte[] LastChunk = Encoding.ASCII.GetBytes("0\r\n\r\n");

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly List<TcpListener> _listeners = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private int _active;
    private int _requests;
    private bool _disposed;

    public string ServerKey { get; }
    public string Upstream { get; }
    // 0 = freien Port wählen; nach Start() der tatsächliche Port
    public int ListenPort { get; private set; }
    public virtual bool Running => _listeners.Count > 0 && !_disposed;
    public int Requests => Volatile.Read(ref _requests);
    public int ActiveRequests => Volatile.Read(ref _active);
    // Der Client des Proxys (keine Umleitung, kein System-Proxy, keine Dekompression) – für Antworten, die der Proxy
    // selbst erzeugt, aber aus einer eigenen Weiterleitung speist (Anthropic-Brücke)
    protected HttpClient UpstreamClient => _http;
    // Wie lange auf die Antwortköpfe des Servers gewartet wird (Prompt-Verarbeitung), und wie lange der Stream still sein darf
    public TimeSpan HeaderTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan RequestHeadTimeout { get; set; } = TimeSpan.FromSeconds(30);
    // Höchstzeit ohne neue Bytes beim Lesen der Anfrage (Kopf und Text); die Gesamtzeit begrenzt RequestHeadTimeout
    public TimeSpan RequestIdleTimeout { get; set; } = TimeSpan.FromSeconds(15);
    // Auch auf dem LAN anbieten (0.0.0.0/::) statt nur Loopback. Für angehängte Rechner; ohne Zugangscode.
    public bool BindLan { get; set; }
    public event Action<ProxyRecord>? Recorded;
    // Nur zur Fehlersuche (Einstellung „Proxy debug“): bekommt den vollen Anfragetext. null = aus, dann wird nichts kopiert.
    public Action<ProxyRecord, byte[]>? DebugBody { get; set; }
    // Fehlerantwort eines Backends auf eine getrackte Generierungs-Anfrage (z. B. Status 400): der Text wird ausgewertet,
    // um „Kontext zu klein" verständlich zu melden. Die Antwort selbst wird unverändert weitergeleitet.
    public event Action<string>? UpstreamError;

    // Für den simulierten Proxy: eine Beobachtung melden, ohne dass eine Anfrage durch einen Socket lief
    protected void Report(ProxyRecord rec)
    {
        Interlocked.Increment(ref _requests);
        try { Recorded?.Invoke(rec); } catch { }
    }

    public RequestProxy(string serverKey, string upstreamUrl, int listenPort, HttpClient? http = null)
    {
        ServerKey = serverKey;
        Upstream = upstreamUrl.TrimEnd('/');
        ListenPort = listenPort;
        _ownsHttp = http == null;
        _http = http ?? CreateClient();
    }

    // Kein Weiterleiten, keine Cookies, kein System-Proxy, keine automatische Dekompression (der Proxy reicht Bytes unverändert durch)
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    }) { Timeout = Timeout.InfiniteTimeSpan };   // Zeitgrenzen setzt der Proxy je Anfrage selbst (Header-/Leerlauf-Timeout)

    public static bool IsGenerationPath(string path) => GenerationPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase));

    // Bestimmt Ziel und (ggf. umgeschriebenen) Anfragetext. Der einfache Proxy leitet immer an seinen einen Upstream weiter;
    // der Router (RouterProxy) überschreibt das, um nach Modell weiterzuleiten, /v1/models selbst zu beantworten und auf ein
    // noch ladendes Ziel zu warten.
    protected virtual Task<ProxyRoute> RouteAsync(string method, string target, IReadOnlyList<(string Name, string Value)> headers, byte[]? body, CancellationToken ct)
        => Task.FromResult(new ProxyRoute(Upstream, ServerKey, body));

    // Eine Generierungs-Anfrage ist gescheitert, bevor ein Byte an den Client ging. Der Router darf ein anderes Ziel (oder
    // dasselbe nach seinem Neustart) nennen; body ist der unveränderte Anfragetext des Clients. null = Fehler wie bisher melden.
    protected virtual Task<ProxyRoute?> RerouteAsync(ProxyRoute failed, ProxyFailure failure, byte[]? body, ProxyAttempts attempts, CancellationToken ct)
        => Task.FromResult<ProxyRoute?>(null);

    // Größte Fehlerantwort, die zum Prüfen auf „Kontext zu klein" gelesen wird (der Rest bleibt im Strom)
    private const int OverflowProbeBytes = 64 * 1024;

    // Freier Port für einen Proxy: Serverport + 1000, dann weiter hinauf; null, wenn nichts frei ist
    public static int? PickPort(int serverPort, Func<int, bool> isUsed)
    {
        for (int i = 0; i < 40; i++)
        {
            int p = serverPort + 1000 + i;
            if (p > 65000) break;
            if (!isUsed(p)) return p;
        }
        return null;
    }

    public virtual void Start()
    {
        var v4 = new TcpListener(BindLan ? IPAddress.Any : IPAddress.Loopback, ListenPort);
        v4.Start(64);   // wirft, wenn der Port belegt ist
        ListenPort = ((IPEndPoint)v4.LocalEndpoint).Port;
        _listeners.Add(v4);
        try
        {
            if (Socket.OSSupportsIPv6)
            {
                var v6 = new TcpListener(BindLan ? IPAddress.IPv6Any : IPAddress.IPv6Loopback, ListenPort);
                v6.Start(64);
                _listeners.Add(v6);
            }
        }
        catch { /* ::1 ist optional (kein IPv6 oder Port dort belegt) */ }
        foreach (var l in _listeners.ToList()) _ = Task.Run(() => AcceptLoop(l));
    }

    private async Task AcceptLoop(TcpListener listener)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            // Standard nur Loopback-Gegenstellen; mit LAN-Freigabe auch andere Adressen (der Listener bindet sonst ohnehin nur dort)
            if (client.Client.RemoteEndPoint is not IPEndPoint ep || (!BindLan && !IPAddress.IsLoopback(ep.Address)) || Volatile.Read(ref _active) >= MaxConnections)
            {
                try { client.Dispose(); } catch { }
                continue;
            }
            _clients[client] = 0;
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    // ── HTTP lesen ──

    private sealed class ProxyHttpException : Exception
    {
        public int Status { get; }
        public ProxyHttpException(int status, string message) : base(message) => Status = status;
    }

    // Gepufferter Leser über dem Netzwerkstrom (Kopfzeilen zeilenweise, danach Rohbytes)
    private sealed class Wire
    {
        private readonly Stream _s;
        private readonly byte[] _buf = new byte[16 * 1024];
        private int _pos, _len;
        public Wire(Stream s) => _s = s;
        // Höchstzeit ohne neue Bytes pro Lesevorgang (Schutz vor Verbindungen, die tröpfchenweise senden und alle Plätze belegen)
        public TimeSpan IdleTimeout { get; init; } = Timeout.InfiniteTimeSpan;

        private async ValueTask<int> ReadStreamAsync(Memory<byte> dst, CancellationToken ct)
        {
            if (IdleTimeout == Timeout.InfiniteTimeSpan) return await _s.ReadAsync(dst, ct);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(IdleTimeout);
            try { return await _s.ReadAsync(dst, idle.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProxyHttpException(408, "Request timeout (no data received)"); }
        }

        public async ValueTask<int> ReadAsync(byte[] dst, int off, int count, CancellationToken ct)
        {
            if (_pos < _len)
            {
                int n = Math.Min(count, _len - _pos);
                Buffer.BlockCopy(_buf, _pos, dst, off, n);
                _pos += n;
                return n;
            }
            return await ReadStreamAsync(dst.AsMemory(off, count), ct);
        }

        // Eine Zeile bis LF (CR wird abgeschnitten); null = Verbindung vor dem ersten Byte beendet
        public async Task<string?> ReadLineAsync(int maxLen, CancellationToken ct)
        {
            var line = new List<byte>(128);
            while (true)
            {
                if (_pos >= _len)
                {
                    _len = await ReadStreamAsync(_buf.AsMemory(), ct);
                    _pos = 0;
                    if (_len <= 0) { _len = 0; if (line.Count == 0) return null; throw new ProxyHttpException(400, "Incomplete request"); }
                }
                byte b = _buf[_pos++];
                if (b == (byte)'\n')
                {
                    if (line.Count > 0 && line[^1] == (byte)'\r') line.RemoveAt(line.Count - 1);
                    return Encoding.Latin1.GetString(line.ToArray());
                }
                line.Add(b);
                if (line.Count > maxLen) throw new ProxyHttpException(431, "Header too large");
            }
        }
    }

    private sealed class ParsedRequest
    {
        public string Method = "", Target = "", Version = "HTTP/1.1";
        public List<(string Name, string Value)> Headers = new();
        public byte[]? Body;
        public string? Header(string name) => Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static async Task<ParsedRequest?> ReadRequestAsync(Wire wire, Stream net, bool allowAnyHost, CancellationToken ct)
    {
        string? first;
        do { first = await wire.ReadLineAsync(8192, ct); } while (first is { Length: 0 });   // führende Leerzeilen überspringen
        if (first == null) return null;
        var parts = first.Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) throw new ProxyHttpException(400, "Bad request line");
        var req = new ParsedRequest { Method = parts[0].ToUpperInvariant(), Target = parts[1], Version = parts[2] };
        if (req.Method.Length == 0 || req.Method.Any(c => !char.IsAsciiLetter(c))) throw new ProxyHttpException(400, "Bad method");
        if (!req.Target.StartsWith('/')) throw new ProxyHttpException(400, "Only origin-form request targets are supported");

        int total = first.Length;
        while (true)
        {
            var line = await wire.ReadLineAsync(MaxHeadBytes, ct) ?? throw new ProxyHttpException(400, "Incomplete request");
            if (line.Length == 0) break;
            total += line.Length;
            if (total > MaxHeadBytes) throw new ProxyHttpException(431, "Header too large");
            int colon = line.IndexOf(':');
            if (colon <= 0 || line[0] is ' ' or '\t') throw new ProxyHttpException(400, "Bad header line");
            req.Headers.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }

        // Host-Header: nur Loopback-Namen (schützt vor DNS-Rebinding aus dem Browser); mit LAN-Freigabe entfällt die Prüfung
        if (!allowAnyHost && req.Header("Host") is { } host && !IsLoopbackHost(host)) throw new ProxyHttpException(403, "Forbidden host");

        // Anfragetext: Transfer-Encoding: chunked oder Content-Length, höchstens MaxBodyBytes
        bool chunked = req.Header("Transfer-Encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true;
        long length = 0;
        if (!chunked && req.Header("Content-Length") is { } cl && (!long.TryParse(cl, out length) || length < 0)) throw new ProxyHttpException(400, "Bad Content-Length");
        if (length > MaxBodyBytes) throw new ProxyHttpException(413, "Request body too large (limit " + MaxBodyBytes / (1024 * 1024) + " MB)");
        bool hasBody = chunked || length > 0;
        if (hasBody && req.Header("Expect")?.Contains("100-continue", StringComparison.OrdinalIgnoreCase) == true)
            await net.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), ct);

        if (chunked) req.Body = await ReadChunkedAsync(wire, ct);
        else if (length > 0)
        {
            var body = new byte[length];
            int got = 0;
            while (got < body.Length)
            {
                int n = await wire.ReadAsync(body, got, (int)Math.Min(body.Length - got, 1 << 20), ct);
                if (n <= 0) throw new ProxyHttpException(400, "Incomplete request body");
                got += n;
            }
            req.Body = body;
        }
        return req;
    }

    private static async Task<byte[]> ReadChunkedAsync(Wire wire, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        while (true)
        {
            var sizeLine = await wire.ReadLineAsync(256, ct) ?? throw new ProxyHttpException(400, "Incomplete chunked body");
            int semi = sizeLine.IndexOf(';');
            if (semi >= 0) sizeLine = sizeLine[..semi];
            if (!long.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out long size) || size < 0) throw new ProxyHttpException(400, "Bad chunk size");
            if (size == 0) break;
            if (ms.Length + size > MaxBodyBytes) throw new ProxyHttpException(413, "Request body too large (limit " + MaxBodyBytes / (1024 * 1024) + " MB)");
            long left = size;
            while (left > 0)
            {
                int n = await wire.ReadAsync(buf, 0, (int)Math.Min(buf.Length, left), ct);
                if (n <= 0) throw new ProxyHttpException(400, "Incomplete chunked body");
                ms.Write(buf, 0, n);
                left -= n;
            }
            await wire.ReadLineAsync(16, ct);   // CRLF nach dem Chunk
        }
        // Trailer bis zur Leerzeile verwerfen
        while (await wire.ReadLineAsync(MaxHeadBytes, ct) is { Length: > 0 }) { }
        return ms.ToArray();
    }

    internal static bool IsLoopbackHost(string hostHeader)
    {
        var h = hostHeader.Trim();
        if (h.StartsWith('['))
        {
            int end = h.IndexOf(']');
            h = end > 0 ? h[1..end] : h;
        }
        else
        {
            int colon = h.LastIndexOf(':');
            if (colon > 0 && h.IndexOf(':') == colon) h = h[..colon];   // Host:Port (keine IPv6-Adresse ohne Klammern)
        }
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase) || h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(h, out var ip) && IPAddress.IsLoopback(ip);
    }

    // ── Eine Verbindung ──

    private static string Reason(int status) => status switch
    {
        200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 408 => "Request Timeout", 413 => "Payload Too Large", 431 => "Request Header Fields Too Large",
        502 => "Bad Gateway", 503 => "Service Unavailable", 504 => "Gateway Timeout", _ => "Error",
    };

    private static async Task WriteSimpleAsync(Stream net, int status, string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await net.WriteAsync(Encoding.ASCII.GetBytes(head));
        await net.WriteAsync(body);
    }

    // Antwort, die der Proxy selbst erzeugt (z. B. aggregiertes /v1/models oder ein Fehler ohne Ziel)
    private static async Task WriteLocalAsync(Stream net, int status, string? contentType, byte[] body, CancellationToken ct)
    {
        var head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: {contentType ?? "application/json; charset=utf-8"}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await net.WriteAsync(Encoding.Latin1.GetBytes(head), ct);
        if (body.Length > 0) await net.WriteAsync(body, ct);
    }

    private static string CleanHeader(string v) => v.Replace('\r', ' ').Replace('\n', ' ');

    private async Task HandleAsync(TcpClient client)
    {
        Interlocked.Increment(ref _active);
        using var conn = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        int clientGone = 0, done = 0;
        Task? watcher = null;
        ParsedRequest? req = null;
        ProxyRoute? route = null;
        HttpRequestMessage? upMsg = null;
        var tap = new ProxyTap();
        var start = DateTime.Now;
        bool track = false, stream = false, aborted = false;
        int status = 0;
        byte[]? errProbe = null; int errProbeLen = 0;   // kleine Probe einer Fehlerantwort (nur 400er, nicht kodiert)
        try
        {
            client.NoDelay = true;
            var net = client.GetStream();
            var wire = new Wire(net) { IdleTimeout = RequestIdleTimeout };
            try
            {
                using var head = CancellationTokenSource.CreateLinkedTokenSource(conn.Token);
                head.CancelAfter(RequestHeadTimeout);
                req = await ReadRequestAsync(wire, net, BindLan, head.Token);
            }
            catch (ProxyHttpException ex) { await WriteSimpleAsync(net, ex.Status, ex.Message); return; }
            if (req == null) return;
            start = DateTime.Now;

            // Ab jetzt wird der Client beobachtet: schließt er die Verbindung (Abbruch im Chat), wird der Server abgebrochen.
            // Die Anfrage ist komplett gelesen, jedes weitere Byte ist unerwartet und wird verworfen.
            watcher = Task.Run(async () =>
            {
                var one = new byte[256];
                try
                {
                    while (true)
                    {
                        int n = await net.ReadAsync(one, conn.Token);
                        if (n <= 0) break;
                    }
                }
                catch { }
                if (Volatile.Read(ref done) == 0) { Volatile.Write(ref clientGone, 1); try { conn.Cancel(); } catch { } }
            });

            int q = req.Target.IndexOf('?');
            string pathOnly = q >= 0 ? req.Target[..q] : req.Target;
            route = await RouteAsync(req.Method, req.Target, req.Headers, req.Body, conn.Token);
            if (route.LocalBody is { } localBody)   // der Router beantwortet diese Anfrage selbst (z. B. /v1/models oder kein Ziel)
            {
                status = route.LocalStatus;
                try { await WriteLocalAsync(net, status, route.LocalContentType, localBody, conn.Token); }
                catch { aborted = true; }
                return;
            }
            // Antwort, die der Proxy selbst schreibt, aber live aus einer Weiterleitung speist (Anthropic-Brücke):
            // der Handler schreibt Kopfzeilen und Rumpf selbst; der Mitschreiber zählt die Anfrage weiter mit.
            if (route.LocalStream is { } localStream)
            {
                track = req.Method == "POST" && IsGenerationPath(pathOnly);
                stream = true;
                tap.Stream = true;
                try { status = await localStream(net, tap, conn.Token); }
                catch { aborted = true; }
                finally { Volatile.Write(ref done, 1); }   // fertig geschrieben: ein Schließen des Clients zählt nicht als Abbruch
                return;
            }
            track = req.Method == "POST" && IsGenerationPath(pathOnly);
            // Failover: scheitert eine Generierungs-Anfrage, bevor ein Byte an den Client ging (keine Verbindung, 503, Kontext
            // zu klein), darf der Router ein anderes Ziel nennen. Der Anfragetext liegt im Speicher, ein zweiter Versuch ist sicher.
            var attempts = new ProxyAttempts();
            HttpResponseMessage resp;
            while (true)
            {
                var body = route.Body ?? Array.Empty<byte>();
                if (track && req.Header("Content-Encoding") == null) stream = BodyWantsStream(body);
                tap.Stream = stream;
                upMsg?.Dispose();
                upMsg = BuildUpstreamRequest(req, route, body);

                HttpResponseMessage got;
                try
                {
                    using var hdr = CancellationTokenSource.CreateLinkedTokenSource(conn.Token);
                    hdr.CancelAfter(HeaderTimeout);
                    got = await (route.Client ?? _http).SendAsync(upMsg, HttpCompletionOption.ResponseHeadersRead, hdr.Token);
                }
                catch (OperationCanceledException) when (conn.IsCancellationRequested) { aborted = true; return; }   // Client weg (oder Proxy beendet): Upstream ist abgebrochen
                catch (OperationCanceledException) { status = 504; await WriteSimpleAsync(net, 504, "The server did not answer in time"); return; }
                catch (Exception)
                {
                    if (track && await NextRouteAsync(route, ProxyFailure.Unreachable, req.Body, attempts, conn.Token) is { } next) { route = next; continue; }
                    status = 502; await WriteSimpleAsync(net, 502, "Upstream server not reachable"); return;
                }

                int code = (int)got.StatusCode;
                ProxyFailure? busyKind = code == 503 ? ProxyFailure.Unavailable : code is 429 or 500 or 502 or 529 ? ProxyFailure.Overloaded : null;
                if (track && busyKind is { } bk && await NextRouteAsync(route, bk, req.Body, attempts, conn.Token) is { } busy)
                {
                    got.Dispose();
                    route = busy;
                    continue;
                }
                if (track && code == 400 && got.Content.Headers.ContentEncoding.Count == 0)
                {
                    // Kontext zu klein? Den Anfang der Fehlerantwort lesen; ohne neues Ziel geht die Antwort unverändert weiter
                    var (prefix, rest) = await ReadPrefixAsync(got, OverflowProbeBytes, conn.Token);
                    if (RouterProxy.IsContextOverflow(Encoding.UTF8.GetString(prefix))
                        && await NextRouteAsync(route, ProxyFailure.ContextOverflow, req.Body, attempts, conn.Token) is { } bigger)
                    {
                        await rest.DisposeAsync();
                        got.Dispose();
                        route = bigger;
                        continue;
                    }
                    Replay(got, prefix, rest);
                }
                resp = got;
                break;
            }

            using (resp)
            {
                status = (int)resp.StatusCode;
                bool sse = resp.Content.Headers.ContentType?.MediaType?.Contains("event-stream", StringComparison.OrdinalIgnoreCase) == true;
                bool encoded = resp.Content.Headers.ContentEncoding.Count > 0;
                if (track && status == 400 && !encoded) errProbe = new byte[4096];
                if (sse) tap.Stream = true;
                bool noBody = req.Method == "HEAD" || status is 204 or 304 or < 200;
                long? len = resp.Content.Headers.ContentLength;
                bool framed = !sse && len != null;

                var head = new StringBuilder();
                head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(CleanHeader(resp.ReasonPhrase ?? Reason(status))).Append("\r\n");
                foreach (var h in resp.Headers) if (!HopHeaders.Contains(h.Key)) head.Append(h.Key).Append(": ").Append(CleanHeader(string.Join(", ", h.Value))).Append("\r\n");
                foreach (var h in resp.Content.Headers) if (!HopHeaders.Contains(h.Key)) head.Append(h.Key).Append(": ").Append(CleanHeader(string.Join(", ", h.Value))).Append("\r\n");
                if (noBody) { if (len != null) head.Append("Content-Length: ").Append(len.Value).Append("\r\n"); }
                else if (framed) head.Append("Content-Length: ").Append(len!.Value).Append("\r\n");
                else head.Append("Transfer-Encoding: chunked\r\n");
                head.Append("Connection: close\r\n\r\n");
                // "done" vor dem letzten Schreiben setzen: schließt der Client direkt nach dem Empfang, ist das kein Abbruch
                if (noBody || len == 0) Volatile.Write(ref done, 1);
                try { await net.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), conn.Token); }
                catch { aborted = true; return; }

                if (!noBody)
                {
                    try
                    {
                        await using var src = await resp.Content.ReadAsStreamAsync(conn.Token);
                        var buf = new byte[16 * 1024];
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(conn.Token);
                        long sent = 0;
                        while (true)
                        {
                            idle.CancelAfter(IdleTimeout);
                            int n = await src.ReadAsync(buf, idle.Token);
                            if (n <= 0) break;
                            if (track && !encoded) tap.Feed(buf.AsSpan(0, n), DateTime.Now);
                            if (errProbe != null && errProbeLen < errProbe.Length)
                            {
                                int c = Math.Min(n, errProbe.Length - errProbeLen);
                                Buffer.BlockCopy(buf, 0, errProbe, errProbeLen, c);
                                errProbeLen += c;
                            }
                            sent += n;
                            if (framed && sent >= len!.Value) Volatile.Write(ref done, 1);
                            try
                            {
                                if (!framed) await net.WriteAsync(Encoding.ASCII.GetBytes(n.ToString("x") + "\r\n"), conn.Token);
                                await net.WriteAsync(buf.AsMemory(0, n), conn.Token);
                                if (!framed) await net.WriteAsync(Crlf, conn.Token);
                            }
                            catch { aborted = true; return; }   // Client weg: Upstream wird durch Dispose von resp beendet
                        }
                        if (!framed)
                        {
                            Volatile.Write(ref done, 1);
                            await net.WriteAsync(LastChunk, conn.Token);
                        }
                        if (errProbe != null && errProbeLen > 0)
                            try { UpstreamError?.Invoke(Encoding.UTF8.GetString(errProbe, 0, errProbeLen)); } catch { }
                    }
                    catch (Exception)
                    {
                        // Server oder Leerlauf-Timeout: die Antwort bleibt abgeschnitten (kein Abschluss-Chunk), der Client sieht den Abbruch
                        aborted = true;
                        return;
                    }
                }
            }
        }
        catch (Exception) { aborted = true; }
        finally
        {
            upMsg?.Dispose();
            Volatile.Write(ref done, 1);
            if (Volatile.Read(ref clientGone) != 0) aborted = true;
            // Sauber schließen: erst Senden beenden, dem Client einen Moment zum Lesen lassen (sonst kann ein RST die Antwort verschlucken)
            try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
            if (watcher != null) await Task.WhenAny(watcher, Task.Delay(1000));
            try { conn.Cancel(); } catch { }
            try { client.Dispose(); } catch { }
            _clients.TryRemove(client, out _);
            Interlocked.Decrement(ref _active);

            if (track && req != null)
            {
                var end = DateTime.Now;
                tap.Complete(end);
                Interlocked.Increment(ref _requests);
                string ua = req.Header("User-Agent") ?? "";
                int sp = ua.IndexOf(' ');
                if (sp > 0) ua = ua[..sp];
                if (ua.Length > 60) ua = ua[..60];
                int reasoning = tap.Reasoning, content = tap.Content;
                bool known = stream || tap.Stream || reasoning + content > 0;
                int q2 = req.Target.IndexOf('?');
                // Hat die Anthropic-Brücke das Ziel gewechselt (Failover), steht das tatsächliche Ziel im Mitschreiber
                var rec = new ProxyRecord(tap.RoutedServerKey ?? route?.ServerKey ?? ServerKey, q2 >= 0 ? req.Target[..q2] : req.Target, start, end, tap.FirstToken, tap.FirstContent,
                    known ? reasoning : -1, known ? content : -1, tap.Tools.ToArray(), tap.FinishReason, ua, stream || tap.Stream, status, aborted,
                    RouterProxy.ReadModel(req.Body), RequestShape.Read(req.Body), tap.RoutedRoute ?? route?.Route);
                try { Recorded?.Invoke(rec); } catch { }
                if (req.Body is { Length: > 0 } dbgBody && DebugBody is { } dbg) { try { dbg(rec, dbgBody); } catch { } }
            }
        }
    }

    // Höchstzahl der Versuche einer Anfrage (Schutz vor einer Schleife, falls ein Router immer wieder ein Ziel nennt)
    public const int MaxAttempts = 8;

    private async Task<ProxyRoute?> NextRouteAsync(ProxyRoute failed, ProxyFailure failure, byte[]? body, ProxyAttempts attempts, CancellationToken ct)
    {
        if (attempts.Count >= MaxAttempts) return null;
        attempts.Count++;
        return await RerouteAsync(failed, failure, body, attempts, ct);
    }

    // Die Anfrage an das Ziel: Kopfzeilen des Clients ohne Verbindungs-Kopfzeilen; eine eigene Authorization des Proxys
    // (Cloud-Ziel) ersetzt die des Clients
    private static HttpRequestMessage BuildUpstreamRequest(ParsedRequest req, ProxyRoute route, byte[] body)
    {
        var up = new HttpRequestMessage(new HttpMethod(req.Method), NetAddr.JoinUpstream(route.Upstream, req.Target));
        if (body.Length > 0 || req.Method is "POST" or "PUT" or "PATCH") up.Content = new ByteArrayContent(body);
        var own = route.Headers;
        foreach (var (name, value) in req.Headers)
        {
            if (HopHeaders.Contains(name)) continue;
            // Setzt der Proxy selbst eine Authorization (Cloud-Ziel), gehört der Wert des Clients zu stykker und darf nicht mit
            if (own != null && name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            if (ContentHeaders.Contains(name)) up.Content?.Headers.TryAddWithoutValidation(name, value);
            else up.Headers.TryAddWithoutValidation(name, value);
        }
        if (own != null)
            foreach (var (name, value) in own) up.Headers.TryAddWithoutValidation(name, value);
        return up;
    }

    // Die ersten Bytes einer Antwort lesen (höchstens limit, höchstens 10 s); der Strom bleibt für den Rest offen
    private static async Task<(byte[] Prefix, Stream Remainder)> ReadPrefixAsync(HttpResponseMessage resp, int limit, CancellationToken ct)
    {
        var rest = await resp.Content.ReadAsStreamAsync(ct);
        var buf = new byte[limit];
        int got = 0;
        using var slow = CancellationTokenSource.CreateLinkedTokenSource(ct);
        slow.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            int n;
            while (got < limit && (n = await rest.ReadAsync(buf.AsMemory(got), slow.Token)) > 0) got += n;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* langsames Ziel: mit dem Gelesenen weiter */ }
        return (buf[..got], rest);
    }

    // Die schon gelesenen Bytes wieder vor den Rest setzen, damit die Antwort unverändert weitergeht (Kopfzeilen bleiben)
    private static void Replay(HttpResponseMessage resp, byte[] prefix, Stream rest)
    {
        var old = resp.Content;
        var content = new StreamContent(new PrefixedStream(prefix, rest));
        foreach (var h in old.Headers) content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        resp.Content = content;
    }

    // Lesestrom: erst die Bytes aus prefix, dann der Rest aus inner
    internal sealed class PrefixedStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly Stream _inner;
        private int _pos;
        public PrefixedStream(byte[] prefix, Stream inner) { _prefix = prefix; _inner = inner; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_pos < _prefix.Length)
            {
                int n = Math.Min(buffer.Length, _prefix.Length - _pos);
                _prefix.AsSpan(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }
            return _inner.Read(buffer);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < _prefix.Length)
            {
                int n = Math.Min(buffer.Length, _prefix.Length - _pos);
                _prefix.AsMemory(_pos, n).CopyTo(buffer);
                _pos += n;
                return ValueTask.FromResult(n);
            }
            return _inner.ReadAsync(buffer, ct);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    // Steht im Anfragetext "stream": true? (nur dieses eine Feld wird gelesen)
    public static bool BodyWantsStream(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        foreach (var l in _listeners) { try { l.Stop(); } catch { } }
        foreach (var c in _clients.Keys) { try { c.Dispose(); } catch { } }
        if (_ownsHttp) _http.Dispose();
    }
}
