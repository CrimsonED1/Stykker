namespace StykkerCmd.Platform.Windows;

// Lange Pfade: Mit dem Präfix "\\?\" akzeptiert Windows bis zu rund 32 000 Zeichen und umgeht das MAX_PATH-Limit.
// Alle E/A-Aufrufe laufen über diese Form; Anzeige und Core-Logik arbeiten mit der normalen Form.
internal static class WindowsPath
{
    private const string ExtendedPrefix = @"\\?\";
    private const string UncPrefix = @"\\";

    public static string ToExtended(string path)
    {
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
            return path;
        if (path.StartsWith(UncPrefix, StringComparison.Ordinal))
            return ExtendedPrefix + @"UNC\" + path[2..];
        return ExtendedPrefix + path;
    }

    public static string FromExtended(string path)
    {
        if (path.StartsWith(ExtendedPrefix + @"UNC\", StringComparison.OrdinalIgnoreCase))
            return UncPrefix + path[(ExtendedPrefix.Length + 4)..];
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
            return path[ExtendedPrefix.Length..];
        return path;
    }

    // Path.GetFullPath wirft bei langen Pfaden; ein Pfad mit Laufwerk oder UNC-Präfix ist ohnehin absolut.
    public static string Absolute(string path)
        => Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path);
}
