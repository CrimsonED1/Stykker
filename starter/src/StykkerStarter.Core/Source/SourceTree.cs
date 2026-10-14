using System.Security.Cryptography;
using System.Text;

namespace StykkerStarter.Core.Source;

public sealed record SourceFile(string RelativePath, long Size, DateTime LastWriteUtc);

// Die Quelle zur Zeit der Prüfung: der Ordner der .exe mit allen Dateien darin (relativer Pfad, Größe, Änderungszeit).
// Die Liste wird nicht gehasht, um zu erkennen, ob es neu ist, nur um den Stand zu vergleichen; die Hashes der
// Dateien kommen beim Kopieren (siehe VersionCopier).
public sealed class SourceTree
{
    public SourceTree(string exePath, string folder, IReadOnlyList<SourceFile> files)
    {
        ExePath = exePath;
        Folder = folder;
        Files = files;
    }

    public string ExePath { get; }
    public string Folder { get; }
    public IReadOnlyList<SourceFile> Files { get; }

    public long TotalBytes => Files.Sum(f => f.Size);

    private string? _listHash;

    // SHA-256 der Liste (Pfad, Größe, Änderungszeit, sortiert). Gleiche Liste heißt: es ist derselbe Stand.
    public string ListHash => _listHash ??= ComputeListHash(Files);

    public static string ComputeListHash(IEnumerable<SourceFile> files)
    {
        var text = new StringBuilder();
        foreach (var f in files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
            text.Append(f.RelativePath).Append('\t').Append(f.Size).Append('\t').Append(f.LastWriteUtc.Ticks).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    // Liest den Ordner der .exe. Fehlt die Datei oder der Ordner (Freigabe nicht erreichbar), wirft es
    // SourceUnreachableException – das ist kein Fehler des Programms, nur ein Stand ohne Quelle.
    public static SourceTree Read(string exePath)
    {
        string full;
        try { full = Path.GetFullPath(exePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { throw new SourceUnreachableException("the program path is not valid", ex); }

        if (!File.Exists(full)) throw new SourceUnreachableException("the program file is not there");
        var folder = Path.GetDirectoryName(full)!;
        try
        {
            var files = new List<SourceFile>();
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(path);
                files.Add(new SourceFile(Path.GetRelativePath(folder, path), info.Length, info.LastWriteTimeUtc));
            }
            files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
            return new SourceTree(full, folder, files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SourceUnreachableException("the program folder cannot be read", ex);
        }
    }
}

public sealed class SourceUnreachableException : Exception
{
    public SourceUnreachableException(string message, Exception? inner = null) : base(message, inner) { }
}
