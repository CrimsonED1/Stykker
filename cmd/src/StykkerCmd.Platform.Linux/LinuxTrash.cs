using System.Runtime.Versioning;
using System.Text;
using StykkerCmd.Core.Abstractions;

namespace StykkerCmd.Platform.Linux;

// Papierkorb nach der FreeDesktop-Spezifikation: files/ für die Einträge, info/ für die .trashinfo-Dateien.
// Es gibt nur den Papierkorb des Benutzers. Andere Datenträger ohne Papierkorb meldet IsAvailable als false.
[SupportedOSPlatform("linux")]
public sealed class LinuxTrash : ITrash
{
    private readonly string _trashHome;

    public LinuxTrash()
        : this(DefaultTrashHome())
    {
    }

    public LinuxTrash(string trashHome)
    {
        _trashHome = trashHome;
    }

    public static string DefaultTrashHome()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(dataHome, "Trash");
    }

    public bool IsAvailable(string path)
        => MountTable.VolumeOf(LinuxNative.CanonicalPath(path) ?? Path.GetFullPath(path))
           == MountTable.VolumeOf(LinuxNative.CanonicalPath(_trashHome) ?? Path.GetFullPath(_trashHome));

    public void MoveToTrash(string path)
    {
        var filesDir = Path.Combine(_trashHome, "files");
        var infoDir = Path.Combine(_trashHome, "info");
        Directory.CreateDirectory(filesDir);
        Directory.CreateDirectory(infoDir);

        var absolute = Path.GetFullPath(path).TrimEnd('/');
        var name = Path.GetFileName(absolute);
        var trashName = ReserveName(infoDir, filesDir, name, absolute);

        var infoPath = Path.Combine(infoDir, trashName + ".trashinfo");
        try
        {
            if (Directory.Exists(path))
                Directory.Move(path, Path.Combine(filesDir, trashName));
            else
                File.Move(path, Path.Combine(filesDir, trashName));
        }
        catch
        {
            File.Delete(infoPath); // Der Eintrag ohne Datei wäre sonst ein Geist im Papierkorb
            throw;
        }
    }

    // Reserviert einen freien Namen: die .trashinfo-Datei wird exklusiv angelegt, so kann niemand denselben Namen belegen.
    private static string ReserveName(string infoDir, string filesDir, string name, string originalPath)
    {
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : string.Empty;

        for (int n = 1; ; n++)
        {
            var candidate = n == 1 ? name : $"{stem}.{n}{extension}";
            var infoPath = Path.Combine(infoDir, candidate + ".trashinfo");
            if (File.Exists(Path.Combine(filesDir, candidate)) || Directory.Exists(Path.Combine(filesDir, candidate)))
                continue;

            try
            {
                using var stream = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write);
                var content = $"[Trash Info]\nPath={EncodePath(originalPath)}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n";
                stream.Write(Encoding.UTF8.GetBytes(content));
                return candidate;
            }
            catch (IOException) when (File.Exists(infoPath))
            {
                // Name belegt: nächsten Versuch nehmen.
            }
        }
    }

    // Pfad für die Info-Datei: prozentkodiert, Schrägstriche bleiben.
    private static string EncodePath(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}
