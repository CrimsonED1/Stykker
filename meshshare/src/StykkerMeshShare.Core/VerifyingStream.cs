using System.Security.Cryptography;

namespace StykkerMeshShare.Core;

// Liefert eine Datei aus und hasht dabei mit. Wird sie von vorn bis zum Ende gelesen und stimmt der SHA-256 nicht mit
// dem Index überein, meldet sie das (onMismatch). So fällt eine Datei auf, die sich geändert hat, ohne dass sich Größe
// oder Änderungszeit bewegt haben. Springt jemand (Range-Anfrage zum Fortsetzen), wird nicht geprüft.
internal sealed class VerifyingStream(FileStream inner, string expected, Action onMismatch) : Stream
{
    private IncrementalHash? _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set
        {
            if (value != inner.Position) StopChecking();
            inner.Position = value;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var result = inner.Seek(offset, origin);
        if (!(origin == SeekOrigin.Begin && offset == 0 && _sha != null && result == 0)) StopChecking();
        return result;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        Track(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int read = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        Track(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    private void Track(ReadOnlySpan<byte> data)
    {
        if (_sha == null) return;
        if (data.Length > 0) { _sha.AppendData(data); return; }
        // Ende erreicht
        var hash = Convert.ToHexStringLower(_sha.GetHashAndReset());
        StopChecking();
        if (hash != expected) onMismatch();
    }

    private void StopChecking()
    {
        _sha?.Dispose();
        _sha = null;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopChecking();
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
