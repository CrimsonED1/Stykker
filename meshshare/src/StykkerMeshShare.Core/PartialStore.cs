using System.Security.Cryptography;
using System.Text.Json;

namespace StykkerMeshShare.Core;

// Angefangene Downloads, die einen Abbruch und einen Neustart überleben. Je Inhalt (SHA-256 der ganzen Datei) gibt
// es eine Teildatei <hash>.partial und eine Beschreibung <hash>.json. Die Beschreibung listet die Blöcke, die schon
// sicher auf der Platte stehen (geschrieben, auf die Platte gespült, mit eigenem SHA-256). Beim Fortsetzen wird jeder
// Block noch einmal gelesen und geprüft; was danach kommt oder nicht stimmt, wird abgeschnitten und neu geladen.
// Am Ende entscheidet der Hash der ganzen Datei: stimmt er nicht, wird die Teildatei verworfen.
internal sealed class PartialStore(string dir, int chunkSize)
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(14);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public int ChunkSize { get; } = chunkSize;

    internal sealed record Meta(string Path, string Hash, long Size, List<string> Chunks, DateTime Updated);

    private string DataPath(string hash) => System.IO.Path.Combine(dir, hash + ".partial");
    private string MetaPath(string hash) => System.IO.Path.Combine(dir, hash + ".json");

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

    public IEnumerable<Meta> All()
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            Meta? meta = null;
            try { meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(file), Json); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            if (meta != null) yield return meta;
        }
    }

    public long BytesOf(string hash)
    {
        try { return File.Exists(DataPath(hash)) ? new FileInfo(DataPath(hash)).Length : 0; }
        catch (IOException) { return 0; }
    }

    // Öffnet die Teildatei für diesen Inhalt (oder legt sie an) und prüft, was schon da ist.
    // Teildateien für denselben Pfad mit anderem Inhalt sind veraltet (die Datei hat sich dort geändert) und gehen weg.
    public Handle Open(string path, string hash, long size)
    {
        Directory.CreateDirectory(dir);
        foreach (var other in All().Where(m => m.Hash != hash && string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)).ToList())
            Discard(other.Hash);

        Meta meta;
        try { meta = File.Exists(MetaPath(hash)) ? JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath(hash)), Json)! : null!; }
        catch (JsonException) { meta = null!; }
        if (meta == null || meta.Size != size) meta = new Meta(path, hash, size, new List<string>(), DateTime.UtcNow);
        else meta = meta with { Path = path };

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
            meta = meta with { Chunks = good, Updated = DateTime.UtcNow };
            var handle = new Handle
            {
                Stream = stream, FileHash = fileHash, Meta = meta, DataPath = DataPath(hash), MetaPath = MetaPath(hash),
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

    // Schreibt Daten und schließt volle Blöcke ab (auf die Platte spülen, Block-Hash merken).
    public async Task WriteAsync(Handle handle, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        while (!data.IsEmpty)
        {
            int take = (int)Math.Min(data.Length, ChunkSize - handle.ChunkFill);
            var part = data[..take];
            await handle.Stream.WriteAsync(part, ct).ConfigureAwait(false);
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

    private static void SaveMeta(Handle handle)
    {
        handle.Meta = handle.Meta with { Updated = DateTime.UtcNow };
        var temp = handle.MetaPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(handle.Meta, Json));
        File.Move(temp, handle.MetaPath, overwrite: true);
    }

    // Nach dem Übernehmen (die Teildatei wurde verschoben) oder wenn der Inhalt nicht stimmt.
    public void Discard(string hash)
    {
        TryDelete(DataPath(hash));
        TryDelete(MetaPath(hash));
    }

    // Alte Reste (zwei Wochen nicht angefasst) und Teildateien ohne Beschreibung.
    public void Purge()
    {
        if (!Directory.Exists(dir)) return;
        foreach (var meta in All().Where(m => DateTime.UtcNow - m.Updated > KeepFor).ToList()) Discard(meta.Hash);
        foreach (var file in Directory.EnumerateFiles(dir, "*.partial").ToList())
        {
            var hash = System.IO.Path.GetFileNameWithoutExtension(file);
            if (!File.Exists(MetaPath(hash)) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(1)) TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
