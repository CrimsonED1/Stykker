using System.Globalization;

namespace StykkerMeshShare.Core;

// Jeder Pfad, der von außen kommt (Gegenstelle, Hochladen, Herunterladen), geht hier durch. Ein relativer Pfad wird
// mit "/" geschrieben und muss im geteilten Ordner bleiben. Das ist die größte Angriffsfläche des Werkzeugs:
// Ein fremdes Gerät darf nie eine Datei außerhalb des Ordners lesen oder schreiben.
public static class PathRules
{
    public const int MaxLength = 400;

    // Namen, die Windows für Geräte reserviert – auch mit Endung („nul.txt“).
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] Invalid = ['<', '>', ':', '"', '|', '?', '*', '\0'];

    // Pfade werden ohne Rücksicht auf Groß/Klein verglichen, wie unter Windows.
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    // Normalisiert einen relativen Pfad ("a\b/c.txt" -> "a/b/c.txt"); null, wenn er nicht erlaubt ist.
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxLength) return null;
        var parts = path.Replace('\\', '/').Split('/');
        var kept = new List<string>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            // Ein führender Schrägstrich ist ein absoluter Pfad; doppelte Schrägstriche in der Mitte auch nicht.
            if (part.Length == 0) return null;
            if (!ValidSegment(part)) return null;
            kept.Add(part);
        }
        return string.Join('/', kept);
    }

    private static bool ValidSegment(string part)
    {
        if (part is "." or "..") return false;
        if (part.IndexOfAny(Invalid) >= 0) return false;
        foreach (var c in part) if (c < 32) return false;
        // Windows schneidet Punkte und Leerzeichen am Ende ab: „a.“ wäre dieselbe Datei wie „a“.
        if (part.EndsWith('.') || part.EndsWith(' ') || part.StartsWith(' ')) return false;
        var stem = part.Split('.')[0];
        if (Reserved.Contains(stem)) return false;
        return true;
    }

    // Der volle Pfad im Ordner; null, wenn er (trotz Normalisierung) außerhalb landen würde.
    public static string? ToFull(string root, string? relative)
    {
        var normalized = Normalize(relative);
        if (normalized == null) return null;
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    // Wie ToFull, aber kein Teil des Pfads unterhalb des Ordners darf eine Verknüpfung oder Junction sein. Sonst führte
    // „docs/x.txt“ mit „docs“ als Junction auf C:\Users\… aus dem Ordner hinaus – zum Lesen wie zum Schreiben.
    // Teile, die es noch nicht gibt, sind in Ordnung (sie werden als normale Ordner angelegt).
    public static string? ToFullNoLinks(string root, string? relative)
    {
        var full = ToFull(root, relative);
        if (full == null) return null;
        var current = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var part in Normalize(relative)!.Split('/'))
        {
            current = Path.Combine(current, part);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
        return full;
    }

    // Ein Inhalts-Hash, wie ihn der Index führt: SHA-256 als 64 Kleinbuchstaben-Hex. Hashes von außen gehen in
    // Dateinamen (Teildateien) – alles andere wird abgewiesen.
    public static bool IsHash(string? hash)
    {
        if (hash is not { Length: 64 }) return false;
        foreach (var c in hash) if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    public static string ToRelative(string root, string full)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetRelativePath(rootFull, full).Replace(Path.DirectorySeparatorChar, '/');
    }

    // Dateien, die nie abgeglichen werden: Sperr- und Temp-Dateien, Explorer-Beiwerk, eigene Hilfsdateien.
    public static bool IsIgnored(string relative)
    {
        foreach (var part in relative.Split('/'))
            if (part.StartsWith(".stykker", StringComparison.OrdinalIgnoreCase)) return true;
        var name = relative[(relative.LastIndexOf('/') + 1)..];
        if (name.StartsWith("~$", StringComparison.Ordinal)) return true;
        if (name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var ext in new[] { ".tmp", ".partial", ".crdownload", ".part" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Name der Konfliktkopie: „Bericht (conflict PC2 2026-11-19 143205 3fa2c1).docx“. Er hängt nur am Verlierer
    // (Gerätename, Änderungszeit, Anfang des Hashs), damit beide Seiten dieselbe Kopie anlegen und sie danach als gleich
    // erkennen. Mit dem Hash bekommen zwei verschiedene Fassungen aus derselben Sekunde nie denselben Namen.
    public static string ConflictName(string relative, string? deviceName, long mtimeTicks, string? hash = null)
    {
        var slash = relative.LastIndexOf('/');
        var dir = slash >= 0 ? relative[..(slash + 1)] : "";
        var name = relative[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var ext = dot > 0 ? name[dot..] : "";
        var device = Clean(deviceName);
        var stamp = new DateTime(Math.Clamp(mtimeTicks, 0, DateTime.MaxValue.Ticks), DateTimeKind.Utc).ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        var tag = hash is { Length: >= 6 } ? " " + hash[..6] : "";
        return $"{dir}{stem} (conflict {device} {stamp}{tag}){ext}";
    }

    private static string Clean(string? deviceName)
    {
        var chars = (deviceName ?? "unknown").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }
}
