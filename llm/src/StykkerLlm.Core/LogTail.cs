namespace StykkerLlm.Core;

// Ein Serverlog ansehen, ohne es zu öffnen: „Open log“ beim anhaltenden Hinweis und auf der Detailseite (Web, Telefon).
// Gelesen werden nur Dateien im Datenordner – ein Gerät im Netz darf keine beliebigen Pfade über den Server ziehen.
public static class LogTail
{
    public const int MaxBytes = 256 * 1024;
    public const int MaxLines = 200;

    // Liegt die Datei im Datenordner? ( derselbe Vergleich wie beim Löschen von Aufnahmen)
    public static bool Inside(AppPaths paths, string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return false;
        try
        {
            var root = Path.GetFullPath(paths.Root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(file);
            return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }   // kaputter Pfad, Laufwerk weg: lieber nichts anzeigen
    }

    // Die letzten Zeilen der Logdatei; null = nicht im Datenordner, nicht vorhanden oder nicht lesbar
    public static string? Read(AppPaths paths, string? file, int maxLines = MaxLines)
    {
        if (!Inside(paths, file)) return null;
        try
        {
            using var fs = new FileStream(file!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length > MaxBytes) fs.Seek(-MaxBytes, SeekOrigin.End);
            using var reader = new StreamReader(fs);
            var lines = reader.ReadToEnd().Replace("\r\n", "\n").Split('\n');
            return string.Join(Environment.NewLine, lines.TakeLast(Math.Max(1, maxLines)));
        }
        catch { return null; }
    }
}