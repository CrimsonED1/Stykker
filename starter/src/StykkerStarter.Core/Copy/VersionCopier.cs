using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using StykkerStarter.Core.Source;

namespace StykkerStarter.Core.Copy;

public sealed record CopyProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string? CurrentFile, TimeSpan Elapsed)
{
    public double Percent => BytesTotal == 0 ? 100 : 100.0 * BytesDone / BytesTotal;
    public double BytesPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : BytesDone / Elapsed.TotalSeconds;
    public TimeSpan? Remaining => BytesPerSecond <= 0 ? null : TimeSpan.FromSeconds((BytesTotal - BytesDone) / BytesPerSecond);
}

public sealed record CopiedFile(string RelativePath, long Size, string Sha256);

public sealed record CopyResult(string Folder, IReadOnlyList<CopiedFile> Files, long Bytes);

// Eine Kopie ist fehlgeschlagen (Datei weg, Platte voll, Prüfsumme falsch). Der Abbruch durch den Benutzer ist
// dagegen OperationCanceledException.
public sealed class CopyFailedException : Exception
{
    public CopyFailedException(string message, Exception? inner = null) : base(message, inner) { }
}

// Kopiert den ganzen Ordner einer Quelle in einen neuen Ordner. Ablauf:
//   1. in <Ordner>.partial schreiben, Datei für Datei, mit Fortschritt
//   2. jede Datei gegen die Quelle prüfen: Größe und SHA-256
//   3. erst danach <Ordner>.partial in den endgültigen Namen umbenennen
// Scheitert etwas oder wird abgebrochen, wird .partial gelöscht. Ein fertiger Ordner entsteht nie halb.
// Ein bestehender Zielordner wird nie überschrieben.
public static class VersionCopier
{
    public const int BufferSize = 81920;

    public static CopyResult Copy(SourceTree source, string targetDir, IProgress<CopyProgress>? progress = null, CancellationToken ct = default)
    {
        if (Directory.Exists(targetDir) || File.Exists(targetDir))
            throw new InvalidOperationException("the target folder already exists");
        var partial = targetDir + DataLayout.PartialSuffix;
        DeleteTree(partial);

        var watch = Stopwatch.StartNew();
        var copied = new List<CopiedFile>();
        long bytesDone = 0;
        int filesDone = 0;
        var total = source.TotalBytes;

        void Report(string? file) => progress?.Report(new CopyProgress(filesDone, source.Files.Count, bytesDone, total, file, watch.Elapsed));

        try
        {
            Directory.CreateDirectory(partial);
            Report(null);
            foreach (var file in source.Files)
            {
                ct.ThrowIfCancellationRequested();
                var src = Path.Combine(source.Folder, file.RelativePath);
                var dst = Path.Combine(partial, file.RelativePath);
                var hash = CopyOne(src, dst, file, ct, n => { bytesDone += n; Report(file.RelativePath); });
                copied.Add(new CopiedFile(file.RelativePath, file.Size, hash));
                filesDone++;
                Report(file.RelativePath);
            }

            // Ganz am Ende: alles noch einmal gegen die Quelle, bevor der Ordner seinen Namen bekommt
            foreach (var entry in copied)
            {
                ct.ThrowIfCancellationRequested();
                var problem = CheckFile(Path.Combine(partial, entry.RelativePath), entry);
                if (problem != null) throw new CopyFailedException($"check failed for {entry.RelativePath}: {problem}");
            }

            Directory.Move(partial, targetDir);
            Report(null);
            return new CopyResult(targetDir, copied, bytesDone);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteTree(partial);
            throw new CopyFailedException("the copy failed: " + ex.Message, ex);
        }
        catch
        {
            DeleteTree(partial);
            throw;
        }
    }

    // Kopiert eine Datei, rechnet die Prüfsumme der Quelle mit und setzt die Änderungszeit der Quelle auf die Kopie.
    private static string CopyOne(string src, string dst, SourceFile file, CancellationToken ct, Action<int> onChunk)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
            using var output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, n);
                output.Write(buffer, 0, n);
                written += n;
                onChunk(n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (written != file.Size)
            throw new CopyFailedException($"{file.RelativePath} changed while it was copied");
        File.SetLastWriteTimeUtc(dst, file.LastWriteUtc);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    // Prüft eine Datei gegen den Eintrag: Größe und SHA-256. Liefert null, wenn alles passt, sonst den Grund.
    public static string? CheckFile(string path, CopiedFile expected)
    {
        if (!File.Exists(path)) return "file is missing";
        var info = new FileInfo(path);
        if (info.Length != expected.Size) return $"size is {info.Length}, expected {expected.Size}";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return actual == expected.Sha256 ? null : "checksum differs";
    }

    // Prüft einen fertigen Ordner gegen die Liste der Kopie. Liefert die Dateien, die nicht stimmen (leer = alles gut).
    public static IReadOnlyList<string> Verify(string folder, IReadOnlyList<CopiedFile> expected)
    {
        var problems = new List<string>();
        foreach (var entry in expected)
        {
            var reason = CheckFile(Path.Combine(folder, entry.RelativePath), entry);
            if (reason != null) problems.Add($"{entry.RelativePath}: {reason}");
        }
        return problems;
    }

    // Löscht einen Ordner samt Inhalt; Schreibschutz wird vorher aufgehoben. Fehler werden verschluckt: was danach
    // übrig bleibt, räumt der nächste Versuch weg.
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
