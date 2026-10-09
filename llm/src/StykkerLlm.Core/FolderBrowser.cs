namespace StykkerLlm.Core;

// Ordner des Servers durchblättern, z. B. für den Arbeitsordner der Prompt-Seite. Gezeigt werden nur die Namen der
// Unterordner: keine Dateien, keine versteckten und keine Systemordner. Der leere Pfad meint die Laufwerke.
public static class FolderBrowser
{
    public static FolderListing Browse(string? path)
    {
        var given = (path ?? "").Trim();
        if (given.Length == 0) return new FolderListing("", null, Array.Empty<string>(), Drives(), false);

        string full;
        try { full = Path.GetFullPath(given); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new FolderListing(given, null, Array.Empty<string>(), Array.Empty<string>(), true);
        }

        // Auch bei einem Fehler bleibt der Ordner darüber erreichbar
        var parent = ParentOf(full);
        if (!Directory.Exists(full)) return new FolderListing(full, parent, Array.Empty<string>(), Array.Empty<string>(), true);
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            var folders = new DirectoryInfo(full).EnumerateDirectories("*", options).Select(d => d.Name)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
            return new FolderListing(full, parent, folders, Array.Empty<string>(), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FolderListing(full, parent, Array.Empty<string>(), Array.Empty<string>(), true);
        }
    }

    // Der Ordner darüber; für eine Laufwerkswurzel ("C:\") ist das die Laufwerksliste (leerer Pfad)
    private static string ParentOf(string full)
    {
        var up = Path.GetDirectoryName(full);
        return string.IsNullOrEmpty(up) ? "" : up;
    }

    private static IReadOnlyList<string> Drives()
    {
        try { return DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.Name).ToList(); }
        catch (IOException) { return Array.Empty<string>(); }
    }
}

// Current: der geprüfte Pfad ("" = Laufwerksliste). Parent: der Ordner darüber, null nur bei der Laufwerksliste.
// Folders: Namen der Unterordner. Drives: die bereiten Laufwerke. Failed: der Ordner fehlt oder ist nicht lesbar.
public sealed record FolderListing(string Current, string? Parent, IReadOnlyList<string> Folders, IReadOnlyList<string> Drives, bool Failed);
