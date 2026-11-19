namespace StykkerMeshShare.Core;

// Ein Eintrag im Index: eine Datei, wie dieser PC sie kennt. Gelöschte Dateien bleiben als Grabstein (Deleted = true)
// stehen, damit die Löschung bei den anderen ankommt.
public sealed record FileEntry
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public long MtimeTicks { get; init; }          // Änderungszeit, UTC-Ticks
    public string? Hash { get; init; }             // SHA-256, klein geschrieben; null bei Grabsteinen
    public bool Deleted { get; init; }
    public Dictionary<string, long> Version { get; init; } = new();
    public long Seq { get; init; }                 // laufende Nummer dieses PCs: wann der Eintrag sich hier zuletzt änderte
    public string? OriginId { get; init; }         // welcher PC den Inhalt zuletzt geändert hat
    public string? OriginName { get; init; }
}

// Was eine Gegenstelle auf „was hat sich seit Nummer N geändert?“ antwortet.
public sealed record ChangePage(string NodeId, string NodeName, long Seq, IReadOnlyList<FileEntry> Entries);

// Die Gegenstelle für den Abgleich. Über HTTP (Server) oder direkt im Prozess (Tests).
public interface IPeerSource
{
    Task<ChangePage> GetChangesAsync(long since, CancellationToken ct);

    // Die Datei mit genau diesem Inhalt, ab Byte offset (zum Fortsetzen); null, wenn die Gegenstelle sie nicht (mehr)
    // so hat.
    Task<Stream?> OpenFileAsync(string path, string hash, long offset, CancellationToken ct);
}

public enum ActivityKind { Added, Changed, Deleted, Received, Removed, Conflict, Kept, Error }

public sealed record Activity(DateTime Time, ActivityKind Kind, string Path, string? From);

public sealed record MeshOptions
{
    public required string Folder { get; init; }
    public required string DataDir { get; init; }
    public required string NodeName { get; init; }
    // Eine Datei, die sich in dieser Zeit noch geändert hat, wird erst beim nächsten Durchgang aufgenommen.
    public TimeSpan StableDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan TrashKeep { get; init; } = TimeSpan.FromDays(30);
    // Teildateien werden in Blöcken dieser Größe gesichert (auf die Platte gespült und mit eigenem Hash gemerkt).
    public int TransferChunkSize { get; init; } = 8 * 1024 * 1024;
}
