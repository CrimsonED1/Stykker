namespace StykkerMeshShare.Core;

// Papierkorb zum Ansehen und Zurückholen. Dort landet, was ein anderer PC hier überschrieben oder gelöscht hat
// (30 Tage, siehe MoveToTrash). Zurückgeholt wird in den Ordner; der nächste Scan nimmt die Datei als neue Änderung
// auf, und sie geht wieder an alle PCs.
public sealed record TrashItem(string Id, string Path, DateTime Removed, long Size);

public sealed partial class MeshNode
{
    public IReadOnlyList<TrashItem> TrashItems(int limit = 200)
    {
        var list = new List<TrashItem>();
        if (!Directory.Exists(TrashDir)) return list;
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var full in Directory.EnumerateFiles(TrashDir, "*", enumeration))
            {
                var id = PathRules.ToRelative(TrashDir, full);
                var slash = id.IndexOf('/');
                if (slash <= 0) continue;
                var info = new FileInfo(full);
                list.Add(new TrashItem(id, OriginalPath(id[(slash + 1)..]), info.LastWriteTimeUtc, info.Length));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list.OrderByDescending(t => t.Id[..10], StringComparer.Ordinal).ThenBy(t => t.Path, PathRules.Comparer).Take(limit).ToList();
    }

    // MoveToTrash hängt bei einem Namensgleichen ".<8 Zeichen>" an; das gehört nicht zum Namen der Datei.
    private static string OriginalPath(string inTrash)
    {
        var dot = inTrash.LastIndexOf('.');
        if (dot > 0 && inTrash.Length - dot == 9 && inTrash[(dot + 1)..].All(char.IsAsciiHexDigitLower)) return inTrash[..dot];
        return inTrash;
    }

    // Löscht eine Datei aus dem Papierkorb endgültig; null bei Erfolg, sonst der Grund. Nur hier am PC: die anderen PCs
    // haben ihren eigenen Papierkorb.
    public string? DeleteFromTrash(string id)
    {
        var relative = PathRules.Normalize(id);
        if (relative == null || relative.IndexOf('/') <= 0) return "Not in the trash.";
        var full = PathRules.ToFullNoLinks(TrashDir, relative);
        if (full == null || !File.Exists(full)) return "Not in the trash any more.";
        try { File.Delete(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
        RemoveEmptyTrashFolders();
        return null;
    }

    // Leert den Papierkorb endgültig. Rückgabe: wie viele Dateien weg sind.
    public int EmptyTrash()
    {
        if (!Directory.Exists(TrashDir)) return 0;
        int count = 0;
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var full in Directory.EnumerateFiles(TrashDir, "*", enumeration).ToList())
        {
            try { File.Delete(full); count++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        RemoveEmptyTrashFolders();
        return count;
    }

    private void RemoveEmptyTrashFolders()
    {
        try
        {
            if (!Directory.Exists(TrashDir)) return;
            var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var dir in Directory.EnumerateDirectories(TrashDir, "*", enumeration).OrderByDescending(d => d.Length).ToList())
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Holt eine Datei zurück; null bei Erfolg, sonst der Grund. Ist der Name belegt, kommt " (2)" usw. dazu.
    public string? Restore(string id, out string? restoredAs)
    {
        restoredAs = null;
        var relative = PathRules.Normalize(id);
        if (relative == null || relative.IndexOf('/') <= 0) return "Not in the trash.";
        var source = PathRules.ToFullNoLinks(TrashDir, relative);
        if (source == null || !File.Exists(source)) return "Not in the trash any more.";
        var original = PathRules.Normalize(OriginalPath(relative[(relative.IndexOf('/') + 1)..]));
        if (original == null || PathRules.IsIgnored(original)) return "This name is not allowed in the shared folder.";
        _gate.Wait();
        try
        {
            var name = FreeName(original);
            var target = name == null ? null : Full(name);
            if (target == null) return "This name is not allowed in the shared folder.";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
            restoredAs = name;
            Record(ActivityKind.Restored, name!, null);
        }
        finally { _gate.Release(); }
        Scan();
        return null;
    }
}
