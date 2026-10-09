namespace StykkerCmd.Core.Model;

// Ein Eintrag einer Verzeichnisliste. Bei Links bezeichnet IsDirectory das Ziel, IsLink markiert den Link selbst.
public sealed record FileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    bool IsLink,
    long Size,
    DateTimeOffset Modified)
{
    // Erweiterung ohne Punkt. ".gitignore" und "Name." haben keine Erweiterung.
    public string Extension => ExtensionOf(Name);

    public static string ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..] : string.Empty;
    }
}
