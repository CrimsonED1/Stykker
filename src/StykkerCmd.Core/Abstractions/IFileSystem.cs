using StykkerCmd.Core.Model;

namespace StykkerCmd.Core.Abstractions;

// Dateisystem der Core-Schicht. Windows und Linux implementieren es, Tests nutzen eine Attrappe.
// Fehler kommen als Ausnahmen; der Operationslauf ordnet sie ein (Zugriff, gesperrt, nicht gefunden, ...).
public interface IFileSystem
{
    bool CaseSensitive { get; }

    char Separator { get; }

    // Absoluter Pfad ohne abschließenden Trenner. Dient nur dem Vergleich, nicht dem Zugriff.
    string Normalize(string path);

    IReadOnlyList<FileEntry> List(string directory);

    bool FileExists(string path);

    bool DirectoryExists(string path);

    // Symbolischer Link oder Junction. Ziele werden nicht aufgelöst.
    bool IsLink(string path);

    long FileLength(string path);

    DateTimeOffset LastWriteTime(string path);

    void SetLastWriteTime(string path, DateTimeOffset time);

    // Kopiert Plattform-Attribute (Linux: Rechte). Unter Windows ohne Wirkung.
    void CopyAttributes(string source, string target);

    void CreateDirectory(string path);

    Stream OpenRead(string path);

    // Legt eine neue Datei an und wirft, wenn sie schon existiert.
    Stream CreateNewFile(string path);

    // Entfernt eine Datei oder einen Link auf eine Datei. Niemals rekursiv.
    void DeleteFile(string path);

    // Entfernt einen leeren Ordner oder einen Ordner-Link (der Link, nicht das Ziel). Niemals rekursiv.
    void DeleteEmptyDirectory(string path);

    // Benennt auf demselben Datenträger um. Das Ziel darf nicht existieren.
    void Rename(string source, string target);

    // Schlüssel des Datenträgers, auf dem der Pfad liegt. Gleicher Schlüssel = gleicher Datenträger.
    string VolumeOf(string path);

    // Stabiler Schlüssel für einen Ordner (folgt Links), für die Schleifenerkennung.
    string IdentityOf(string directory);

    string Combine(string directory, string name)
        => directory.EndsWith(Separator) ? directory + name : directory + Separator + name;

    string NameOf(string path)
    {
        var trimmed = path.TrimEnd(Separator);
        return trimmed[(trimmed.LastIndexOf(Separator) + 1)..];
    }

    // Übergeordneter Ordner. Eine Wurzel ist ihr eigener Elternteil ("C:\" bzw. "/").
    string ParentOf(string path)
    {
        var trimmed = path.TrimEnd(Separator);
        int i = trimmed.LastIndexOf(Separator);
        if (i < 0)
            return path;
        var parent = trimmed[..i];
        return parent.Length == 0 || parent.EndsWith(':') ? parent + Separator : parent;
    }

    // Prüft, ob candidate gleich parent ist oder darin liegt.
    bool IsSameOrInside(string candidate, string parent)
    {
        var comparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var c = Normalize(candidate);
        var p = Normalize(parent);
        return c.Equals(p, comparison) || c.StartsWith(p + Separator, comparison);
    }
}
