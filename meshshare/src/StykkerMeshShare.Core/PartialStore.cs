using System.Security.Cryptography;
using System.Text.Json;

namespace StykkerMeshShare.Core;

// Angefangene Downloads, die einen Abbruch und einen Neustart überleben. Je Inhalt (SHA-256 der ganzen Datei) gibt
// es eine Teildatei <hash>.partial und eine Beschreibung <hash>.json. Die Beschreibung listet die Blöcke, die schon
// sicher auf der Platte stehen (geschrieben, auf die Platte gespült, mit eigenem SHA-256). Beim Fortsetzen wird jeder
// Block noch einmal gelesen und geprüft; was danach kommt oder nicht stimmt, wird abgeschnitten und neu geladen.
// Am Ende entscheidet der Hash der ganzen Datei: stimmt er nicht, wird die Teildatei verworfen.
//
// Die Beschreibungen werden einmal gelesen und dann im Speicher geführt (die Seite fragt alle 1,5 s danach).
internal sealed class PartialStore
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(14);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _dir;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Meta> _metas = new(StringComparer.Ordinal);

    public PartialStore(string dir, int chunkSize)
    {
        _dir = dir;
        ChunkSize = chunkSize;
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<Meta>(File.ReadAllText(file), Json) is { } meta && PathRules.IsHash(meta.Hash))
                    _metas[meta.Hash] = meta;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
    }

    public int ChunkSize { get; }

    internal sealed record Meta(string Path, string Hash, long Size, List<string> Chunks, DateTime Updated);

    // Was die Oberfläche braucht: welche Datei, wie groß, wie viel schon sicher da ist.
    internal sealed record Info(string Path, string Hash, long Size, long Bytes, DateTime Updated);

    // Der Hash wird Teil des Dateinamens: nur echte SHA-256-Hashes, sonst könnte ein fremder Wert aus dem Ordner führen.
    private string DataPath(string hash) => System.IO.Path.Combine(_dir, Checked(hash) + ".partial");
    private string MetaPath(string hash) => System.IO.Path.Combine(_dir, Checked(hash) + ".json");

    private static string Checked(string hash) =>
        PathRules.IsHash(hash) ? hash : throw new ArgumentException("not a SHA-256 hash", nameof(hash));

    // Eine geöffnete Teildatei: steht am Ende des geprüften Teils, der Hash kennt diesen Teil schon.
    internal sealed class Handle : IDisposable
    {
        public required FileStream Stream { get; init; }
        public required IncrementalHash FileHash { get; init; }
        public required Meta Meta { get; set; }
        public required string DataPath { get; init; }
        public required string MetaPath { get; init; }
        public long Resumed { get; init; }
        public IncrementalHash ChunkHash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long ChunkFill { get; set; }

        public void Dispose()
        {
            Stream.Dispose();
            FileHash.Dispose();
            ChunkHash.Dispose();
        }
    }

    public IReadOnlyList<Info> All()
    {
        lock (_lock)
            return _metas.Values.Select(m => new Info(m.Path, m.Hash, m.Size, (long)m.Chunks.Count * ChunkSize, m.Updated)).ToList();
    }

    // Wie viel von diesem Inhalt schon sicher da ist (die geprüften Blöcke).
    public long BytesOf(string hash)
    {
        lock (_lock) return _metas.TryGetValue(hash, out var meta) ? (long)meta.Chunks.Count * ChunkSize : 0;
    }

    // Öffnet die Teildatei für diesen Inhalt (oder legt sie an) und prüft, was schon da ist.
    // Teildateien für denselben Pfad mit anderem Inhalt sind veraltet (die Datei hat sich dort geändert) und gehen weg.
    public Handle Open(string path, string hash, long size)
    {
        Checked(hash);
        Directory.CreateDirectory(_dir);
        List<string> stale;
        Meta? meta;
        lock (_lock)
        {
            stale = _metas.Values.Where(m => m.Hash != hash && string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)).Select(m => m.Hash).ToList();
            _metas.TryGetValue(hash, out meta);
        }
        foreach (var other in stale) Discard(other);
        if (meta == null || meta.Size != size) meta = new Meta(path, hash, size, new List<string>(), DateTime.UtcNow);
        else meta = meta with { Path = path, Chunks = new List<string>(meta.Chunks) };

        var stream = new FileStream(DataPath(hash), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 81920);
        var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            // Jeden gemerkten Block lesen und prüfen; beim ersten Fehler hier abschneiden.
            var good = new List<string>();
            var buffer = new byte[Math.Min(ChunkSize, 1 << 20)];
            using var chunk = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            stream.Position = 0;
            foreach (var expected in meta.Chunks)
            {
                long want = ChunkSize;
                if (stream.Length - stream.Position < want) break;
                using var before = fileHash.Clone();
                while (want > 0)
                {
                    int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, want));
                    if (read <= 0) break;
                    chunk.AppendData(buffer, 0, read);
                    fileHash.AppendData(buffer, 0, read);
                    want -= read;
                }
                if (want > 0 || Convert.ToHexStringLower(chunk.GetHashAndReset()) != expected)
                {
                    // Dieser Block ist kaputt: den Datei-Hash auf den Stand davor zurücksetzen.
                    fileHash.Dispose();
                    fileHash = before.Clone();
                    break;
                }
                good.Add(expected);
            }
            long verified = (long)good.Count * ChunkSize;
            stream.SetLength(verified);
            stream.Position = verified;
            var handle = new Handle
            {
                Stream = stream, FileHash = fileHash, Meta = meta with { Chunks = good }, DataPath = DataPath(hash), MetaPath = MetaPath(hash),
                Resumed = verified,
            };
            SaveMeta(handle);
            return handle;
        }
        catch
        {
            stream.Dispose();
            fileHash.Dispose();
            throw;
        }
    }

    // Schreibt Daten und schließt volle Blöcke ab (auf die Platte spülen, Block-Hash merken). Ohne Abbruch: ein halb
    // geschriebener Puffer würde Datei und Hash auseinanderbringen.
    public async Task WriteAsync(Handle handle, ReadOnlyMemory<byte> data)
    {
        while (!data.IsEmpty)
        {
            int take = (int)Math.Min(data.Length, ChunkSize - handle.ChunkFill);
            var part = data[..take];
            await handle.Stream.WriteAsync(part, CancellationToken.None).ConfigureAwait(false);
            handle.FileHash.AppendData(part.Span);
            handle.ChunkHash.AppendData(part.Span);
            handle.ChunkFill += take;
            data = data[take..];
            if (handle.ChunkFill == ChunkSize)
            {
                handle.Stream.Flush(flushToDisk: true);
                handle.Meta.Chunks.Add(Convert.ToHexStringLower(handle.ChunkHash.GetHashAndReset()));
                handle.ChunkFill = 0;
                SaveMeta(handle);
            }
        }
    }

    private void SaveMeta(Handle handle)
    {
        handle.Meta = handle.Meta with { Updated = DateTime.UtcNow };
        var copy = handle.Meta with { Chunks = new List<string>(handle.Meta.Chunks) };
        AtomicFile.WriteJson(handle.MetaPath, copy, Json);
        lock (_lock) _metas[copy.Hash] = copy;
    }

    // Nach dem Übernehmen (die Teildatei wurde verschoben) oder wenn der Inhalt nicht stimmt.
    public void Discard(string hash)
    {
        lock (_lock) _metas.Remove(hash);
        if (!PathRules.IsHash(hash)) return;
        TryDelete(DataPath(hash));
        TryDelete(MetaPath(hash));
    }

    // Alte Reste (zwei Wochen nicht angefasst) und Teildateien ohne Beschreibung.
    public void Purge()
    {
        if (!Directory.Exists(_dir)) return;
        foreach (var info in All().Where(m => DateTime.UtcNow - m.Updated > KeepFor)) Discard(info.Hash);
        foreach (var file in Directory.EnumerateFiles(_dir, "*.partial").ToList())
        {
            var hash = System.IO.Path.GetFileNameWithoutExtension(file);
            bool known;
            lock (_lock) known = _metas.ContainsKey(hash);
            if (!known && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(1)) TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
