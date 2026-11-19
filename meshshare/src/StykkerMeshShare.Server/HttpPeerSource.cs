using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using StykkerMeshShare.Core;

namespace StykkerMeshShare.Server;

// Eine Gegenstelle über HTTP: ein anderer StykkerMeshShare-Server.
// Antworten sind begrenzt (Größe und Zeit), damit ein fremdes oder hängendes Gerät im Netz weder den Speicher füllt
// noch den Abgleich mit allen anderen aufhält. Der Datei-Strom selbst hat keine feste Zeitgrenze (große Dateien), aber
// MeshNode bricht ab, wenn zu lange keine Daten kommen; TCP-Keepalive merkt eine tote Verbindung.
public sealed class HttpPeerSource(HttpClient http, string address) : IPeerSource
{
    private const long MaxChangesBytes = 256L * 1024 * 1024;   // etwa eine Million Einträge
    private const long MaxSmallBytes = 64 * 1024;
    private static readonly TimeSpan SmallTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ChangesTimeout = TimeSpan.FromMinutes(3);

    public string Address { get; } = address;
    private Uri Url(string pathAndQuery) => new($"http://{Address}{pathAndQuery}");

    // Der HTTP-Client für alle Gegenstellen: kurze Verbindungszeit, TCP-Keepalive (30 s, dann alle 10 s, 3 Versuche).
    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                    await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<Hello> HelloAsync(CancellationToken ct) => GetJsonAsync<Hello>("/mesh/hello", MaxSmallBytes, SmallTimeout, ct);

    public Task<ChangePage> GetChangesAsync(long since, CancellationToken ct) =>
        GetJsonAsync<ChangePage>($"/mesh/changes?since={since}", MaxChangesBytes, ChangesTimeout, ct);

    // Ab offset: per Range-Anfrage. Antwortet die Gegenstelle trotzdem mit der ganzen Datei (200 statt 206),
    // wird der Anfang hier überlesen, damit der Strom wirklich bei offset steht.
    public async Task<Stream?> OpenFileAsync(string path, string hash, long offset, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url($"/mesh/file?path={Uri.EscapeDataString(path)}&hash={Uri.EscapeDataString(hash)}"));
        if (offset > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
        HttpResponseMessage response;
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(SmallTimeout);
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connect.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new HttpRequestException("No answer"); }
        }
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            response.Dispose();
            return null;
        }
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (offset == 0) return stream;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (response.Content.Headers.ContentRange?.From == offset) return stream;
            await stream.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            return null;
        }
        var skip = new byte[81920];
        long left = offset;
        while (left > 0)
        {
            int read = await stream.ReadAsync(skip.AsMemory(0, (int)Math.Min(skip.Length, left)), ct).ConfigureAwait(false);
            if (read <= 0) { await stream.DisposeAsync().ConfigureAwait(false); response.Dispose(); return null; }
            left -= read;
        }
        return stream;
    }

    // Lange Abfrage: kommt zurück, sobald sich dort etwas ändert, spätestens nach timeout.
    public async Task<long> WaitAsync(long since, TimeSpan timeout, CancellationToken ct)
    {
        var answer = await GetJsonAsync<Hello>($"/mesh/wait?since={since}&seconds={(int)timeout.TotalSeconds}", MaxSmallBytes, timeout + SmallTimeout, ct).ConfigureAwait(false);
        return answer.Seq;
    }

    private async Task<T> GetJsonAsync<T>(string pathAndQuery, long maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await http.GetAsync(Url(pathAndQuery), HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maxBytes) throw new HttpRequestException("answer too large");
            await using var body = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using var limited = new LimitedStream(body, maxBytes);
            return await System.Text.Json.JsonSerializer.DeserializeAsync<T>(limited, System.Text.Json.JsonSerializerOptions.Web, cts.Token).ConfigureAwait(false)
                ?? throw new HttpRequestException("empty answer");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new HttpRequestException("No answer"); }
    }

    // Liest höchstens max Bytes; mehr ist ein Fehler.
    private sealed class LimitedStream(Stream inner, long max) : Stream
    {
        private long _read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int n = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            _read += n;
            if (_read > max) throw new HttpRequestException("answer too large");
            return n;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            _read += n;
            if (_read > max) throw new HttpRequestException("answer too large");
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

public sealed record Hello(string App, string Id, string Name, long Seq);
