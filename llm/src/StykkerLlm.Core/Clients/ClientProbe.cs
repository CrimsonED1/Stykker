namespace StykkerLlm.Core;

// Wo die Erkennung nach Programmen sucht. Im Betrieb sind es die echten Ordner und der PATH; Tests geben Temp-Ordner vor.
public sealed class ClientProbe
{
    // %LOCALAPPDATA%, %APPDATA%, %USERPROFILE% und die Ordner des PATH in ihrer Reihenfolge
    public string? LocalAppData { get; init; }
    public string? AppData { get; init; }
    public string? UserProfile { get; init; }
    public IReadOnlyList<string> PathDirs { get; init; } = Array.Empty<string>();

    public static ClientProbe System() => new()
    {
        LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        PathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    };

    // Die erste Datei der Namen, die in einem PATH-Ordner liegt. Die Namen gelten der Reihe nach über alle Ordner.
    public string? FindOnPath(params string[] names)
    {
        foreach (var name in names)
            foreach (var dir in PathDirs)
            {
                try
                {
                    var file = Path.Combine(dir, name);
                    if (File.Exists(file)) return file;
                }
                catch (ArgumentException) { }
            }
        return null;
    }

    // Unterordner eines Ordners, die dem Muster entsprechen (leer, wenn der Ordner fehlt oder nicht lesbar ist)
    public static IReadOnlyList<string> SubDirs(string? parent, string pattern)
    {
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return Array.Empty<string>();
        try { return Directory.GetDirectories(parent, pattern); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }
}
