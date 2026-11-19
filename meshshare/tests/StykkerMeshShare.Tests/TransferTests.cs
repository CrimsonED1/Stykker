using StykkerMeshShare.Core;

namespace StykkerMeshShare.Tests;

// Übertragung: Fortsetzen nach Abbruch und Neustart, Prüfung per SHA-256, Reihenfolge der Warteschlange.
public class TransferTests
{
    private const int Chunk = 64 * 1024;

    private static MeshNode Node(Mesh mesh, string name, TimeSpan? badHashPause = null) =>
        new(mesh.Options(name) with { TransferChunkSize = Chunk, BadHashPause = badHashPause ?? TimeSpan.Zero });

    private static byte[] WriteRandom(MeshNode node, string relative, int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        var full = Path.Combine(node.Folder, relative);
        File.WriteAllBytes(full, data);
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddMinutes(-1));
        return data;
    }

    private static string PartialDir(MeshNode node) => Path.Combine(node.Folder, ".stykker", "partial");

    // Gegenstelle, die mitschreibt, was abgerufen wird, und den Inhalt verändern kann.
    private sealed class TestPeer(MeshNode node) : IPeerSource
    {
        public List<(string Path, long Offset)> Opened { get; } = new();
        public Func<string, long, Stream, Stream>? Wrap { get; set; }

        public Task<ChangePage> GetChangesAsync(long since, CancellationToken ct) => Task.FromResult(node.GetChanges(since));

        public Task<Stream?> OpenFileAsync(string path, string hash, long offset, CancellationToken ct)
        {
            lock (Opened) Opened.Add((path, offset));
            var stream = node.OpenFile(path, hash);
            if (stream == null) return Task.FromResult<Stream?>(null);
            stream.Position = offset;
            return Task.FromResult<Stream?>(Wrap?.Invoke(path, offset, stream) ?? stream);
        }
    }

    // Liefert nur limit Bytes und bricht dann ab (Verbindung weg) – oder wartet dort, bis abgebrochen wird.
    private sealed class LimitedStream(Stream inner, long limit, bool hang) : Stream
    {
        private long _read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_read >= limit)
            {
                if (hang) await Task.Delay(Timeout.Infinite, ct);
                throw new IOException("connection lost");
            }
            int n = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - _read)], ct);
            _read += n;
            return n;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

    [Fact]
    public async Task ResumesAfterBreakAndRestart()
    {
        using var mesh = new Mesh();
        var a = Node(mesh, "A");
        var data = WriteRandom(a, "big.bin", 1_000_000, 1);
        a.Scan();

        var b = Node(mesh, "B");
        var peer = new TestPeer(a) { Wrap = (_, _, s) => new LimitedStream(s, 300_000, hang: false) };
        var first = await b.PullAsync(peer);
        Assert.False(first.Complete);
        Assert.False(File.Exists(Path.Combine(b.Folder, "big.bin")));
        Assert.Single(b.PausedFiles(b.Queue));
        b.Dispose();

        // Neustart: ein neuer Knoten auf denselben Ordnern setzt nach dem letzten sicheren Block fort.
        var b2 = Node(mesh, "B");
        var again = new TestPeer(a);
        var second = await b2.PullAsync(again);
        Assert.True(second.Complete);
        var offset = Assert.Single(again.Opened).Offset;
        Assert.Equal(300_000 / Chunk * Chunk, offset);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(b2.Folder, "big.bin")));
        Assert.Empty(Directory.GetFiles(PartialDir(b2)));
    }

    [Fact]
    public async Task DamagedPartialIsCutBackAndLoadedAgain()
    {
        using var mesh = new Mesh();
        var a = Node(mesh, "A");
        var data = WriteRandom(a, "big.bin", 500_000, 2);
        a.Scan();

        var b = Node(mesh, "B");
        await b.PullAsync(new TestPeer(a) { Wrap = (_, _, s) => new LimitedStream(s, 400_000, hang: false) });
        b.Dispose();
        var partial = Directory.GetFiles(PartialDir(b), "*.partial").Single();
        using (var f = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite))
        {
            f.Position = Chunk + 10;   // im zweiten Block
            f.WriteByte(0xAA);
            f.WriteByte(0x55);
        }

        var b2 = Node(mesh, "B");
        var peer = new TestPeer(a);
        Assert.True((await b2.PullAsync(peer)).Complete);
        Assert.Equal(Chunk, Assert.Single(peer.Opened).Offset);   // erster Block gut, ab dem zweiten neu
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(b2.Folder, "big.bin")));
    }

    [Fact]
    public async Task WrongContentIsRejected()
    {
        using var mesh = new Mesh();
        var a = Node(mesh, "A");
        WriteRandom(a, "x.bin", 200_000, 3);
        a.Scan();

        var b = Node(mesh, "B");
        // Unterwegs kaputt: gleiche Länge, aber andere Bytes.
        var peer = new TestPeer(a) { Wrap = (_, _, s) => { var bytes = new byte[200_000]; return new MemoryStream(bytes); } };
        var result = await b.PullAsync(peer);
        Assert.False(result.Complete);
        Assert.False(File.Exists(Path.Combine(b.Folder, "x.bin")));
        Assert.Empty(Directory.GetFiles(PartialDir(b)));
        Assert.Contains(b.RecentActivity, x => x.Kind == ActivityKind.Error && x.Path == "x.bin");

        Assert.True((await b.PullAsync(new TestPeer(a))).Complete);
        Assert.True(File.Exists(Path.Combine(b.Folder, "x.bin")));
    }

    [Fact]
    public void ChangedFileIsNotServed()
    {
        using var mesh = new Mesh();
        var a = Node(mesh, "A");
        WriteRandom(a, "x.bin", 10_000, 4);
        a.Scan();
        var hash = a.Files.Single().Hash!;
        WriteRandom(a, "x.bin", 10_001, 5);   // geändert, noch nicht gescannt
        Assert.Null(a.OpenFile("x.bin", hash));
        Assert.True(a.PendingRescan);
    }

    [Fact]
    public async Task OrderDecidesWhatComesFirst()
    {
        using var mesh = new Mesh();
        var a = Node(mesh, "A");
        foreach (var (name, i) in new[] { "a.bin", "b.bin", "c.bin" }.Select((n, i) => (n, i))) WriteRandom(a, name, 1000, 10 + i);
        a.Scan();

        var b = Node(mesh, "B");
        b.SetOrder(["c.bin", "a.bin"]);
        var peer = new TestPeer(a);
        await b.PullAsync(peer);
        Assert.Equal(["c.bin", "a.bin", "b.bin"], peer.Opened.Select(o => o.Path));
    }

    [Fact]
    public async Task MovingAFileToTheFrontPausesTheRunningOne()
    {
        using var mesh = new Mesh();
        var a = Node(mesh, "A");
        var big = WriteRandom(a, "big.bin", 600_000, 20);
        var small = WriteRandom(a, "small.bin", 5_000, 21);
        a.Scan();

        var b = Node(mesh, "B");
        b.SetOrder(["big.bin", "small.bin"]);
        // big hängt beim ersten Mal nach 200 KB, bis es angehalten wird.
        int bigOpens = 0;
        var peer = new TestPeer(a)
        {
            Wrap = (path, _, s) => path == "big.bin" && Interlocked.Increment(ref bigOpens) == 1 ? new LimitedStream(s, 200_000, hang: true) : s,
        };
        var pull = b.PullAsync(peer);
        for (int i = 0; i < 200 && (b.Progress?.Path != "big.bin" || b.Progress.Done < 200_000); i++) await Task.Delay(20);
        Assert.Equal("big.bin", b.Queue[0].Path);

        b.SetOrder(["small.bin", "big.bin"]);
        var result = await pull.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(result.Complete);
        Assert.Equal(["big.bin", "small.bin", "big.bin"], peer.Opened.Select(o => o.Path));
        Assert.Equal(200_000, peer.Opened[2].Offset);   // die angehaltene Teildatei blieb offen: genau dort weiter
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(b.Folder, "big.bin")));
        Assert.Equal(small, File.ReadAllBytes(Path.Combine(b.Folder, "small.bin")));
    }
}
