using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StykkerFirewall.Core.Ipc;

// Liest und schreibt IpcMessage zeilenweise auf einem Stream (Named Pipe im Betrieb, beliebiger Stream im Test).
// Schreiben ist threadsicher; Lesen gehört genau einem Leser.
public sealed class IpcChannel : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        // "type" darf irgendwo im Objekt stehen (andere Clients, Skripte), nicht nur als erstes Feld.
        AllowOutOfOrderMetadataProperties = true,
    };

    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public IpcChannel(Stream stream)
    {
        _stream = stream;
        _reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
    }

    public static string Encode(IpcMessage message) => JsonSerializer.Serialize(message, Json);

    public static IpcMessage? Decode(string line) => JsonSerializer.Deserialize<IpcMessage>(line, Json);

    public async Task SendAsync(IpcMessage message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(Encode(message) + "\n");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Kein FlushAsync: auf einer Named Pipe wartet es (FlushFileBuffers), bis die Gegenstelle alles gelesen
            // hat – ein langsamer Client würde den Dienst aufhalten. Pipe-Schreibvorgänge sind ohnehin ungepuffert.
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // null = Gegenstelle hat die Verbindung geschlossen.
    public async Task<IpcMessage?> ReceiveAsync(CancellationToken ct)
    {
        while (true)
        {
            var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return null;
            if (line.Length == 0) continue;
            return Decode(line);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
