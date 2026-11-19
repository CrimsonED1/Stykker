namespace StykkerMeshShare.Core;

// Welche Ordner als geteilter Ordner taugen. Alles, was ganze Systembereiche ins Netz stellen würde, ist gesperrt:
// Laufwerkswurzeln, das Benutzerprofil selbst, Windows, die Programmordner und AppData. Dazu darf der geteilte Ordner
// weder im Datenordner (Index, Papierkorb) liegen noch ihn enthalten.
public static class FolderRules
{
    public static string? Check(string? candidate, string current, string dataDir)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return "Enter a folder.";
        if (!Path.IsPathFullyQualified(candidate)) return "Enter a full path, for example D:\\Shared.";
        string full;
        try { full = Normalize(candidate); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "This is not a valid path."; }

        if (Path.GetPathRoot(full) is { } root && Same(full, root)) return "A whole drive cannot be the shared folder.";
        foreach (var folder in Forbidden())
            if (Same(full, folder)) return $"{folder} cannot be the shared folder.";
        foreach (var folder in ForbiddenTrees())
            if (Same(full, folder) || Inside(full, folder)) return $"Folders inside {folder} cannot be shared.";

        var data = Normalize(dataDir);
        if (Same(full, data) || Inside(full, data) || Inside(data, full)) return "The shared folder and the data folder of StykkerMeshShare must be apart.";
        var now = Normalize(current);
        if (Same(full, now)) return "This is already the shared folder.";
        if (Inside(full, now) || Inside(now, full)) return "The new folder cannot be inside the current one, or the other way round.";
        return null;
    }

    // Nur genau diese Ordner: darunter liegen gewöhnliche Ordner (Dokumente, und unter LocalAppData der Temp-Ordner).
    private static IEnumerable<string> Forbidden() => Folders(Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData);

    private static IEnumerable<string> ForbiddenTrees() => Folders(
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.CommonApplicationData,
        Environment.SpecialFolder.ApplicationData);

    private static IEnumerable<string> Folders(params Environment.SpecialFolder[] folders)
    {
        foreach (var folder in folders)
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path)) yield return Normalize(path);
        }
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return root != null && full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    private static bool Same(string a, string b) => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool Inside(string path, string parent) =>
        path.StartsWith(parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
