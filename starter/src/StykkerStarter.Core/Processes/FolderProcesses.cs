namespace StykkerStarter.Core.Processes;

// Welche Prozesse laufen aus einem Ordner? Ein Prozess gehört zu einem Ordner, wenn seine Programmdatei darunter
// liegt. Der Vergleich nimmt den Ordner mit abschließendem Trenner, damit „1.0“ nicht „1.0.1“ einschließt.
public static class FolderProcesses
{
    public static bool IsUnder(string? imagePath, string folder)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(folder)) return false;
        var dir = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var image = Path.GetFullPath(imagePath);
        return image.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    // Die Prozesse aus dem Ordner. Der eigene Prozess ist nie dabei.
    public static IReadOnlyList<ProcessInfo> RunningFrom(IProcessSystem system, string folder, int ownPid) =>
        system.List()
            .Where(p => p.Pid != ownPid && p.Pid > 4 && IsUnder(p.ImagePath, folder))
            .ToList();
}
