using System.Threading.Channels;

namespace StykkerLlm.Core;

// Ein lesbarer Stream aus Stücken, die nach und nach ankommen (Antwort eines Modellservers durch den Host-Tunnel).
// Der Schreiber legt Stücke hinein und schließt ab (ggf. mit Fehler); der Leser bekommt sie in der Reihenfolge.
// Wird der Stream vorher entsorgt (Client weg), meldet OnAbandoned das dem Tunnel, damit der Host aufhören kann.
public sealed class ChunkStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private byte[] _current = Array.Empty<byte>();
    private int _offset;
    private bool _completed, _disposed;

    public Action? OnAbandoned { get; set; }

    public void Write(byte[] chunk) { if (chunk.Length > 0) _chunks.Writer.TryWrite(chunk); }

    public void Complete(Exception? error = null)
    {
        _completed = true;
        _chunks.Writer.TryComplete(error);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_offset >= _current.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) return 0;   // fertig
            if (_chunks.Reader.TryRead(out var next)) { _current = next; _offset = 0; }
        }
        int n = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, n).CopyTo(buffer);
        _offset += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (!_completed) OnAbandoned?.Invoke();
            _chunks.Writer.TryComplete();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
