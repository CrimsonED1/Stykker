namespace StykkerMeshShare.Core;

// Ein Eintrag im Index: eine Datei, wie dieser PC sie kennt. Gelöschte Dateien bleiben als Grabstein (Deleted = true)
// stehen, damit die Löschung bei den anderen ankommt.
//
// To: die PCs (NodeId), die die Datei haben sollen; null = alle (der Spiegel). Ein PC, der nicht darin steht, lädt sie
// nicht, und hat er sie, gibt er sie frei, sobald alle Ziele sie haben (siehe MeshNode.Manage.cs).
// Away: der Eintrag ist hier bekannt, die Datei liegt aber nicht auf diesem PC (nicht Ziel, oder schon freigegeben).
// Die anderen sehen daran, wer die Datei wirklich hat.
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
    public List<string>? To { get; init; }
    public bool Away { get; init; }
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

public enum ActivityKind { Added, Changed, Deleted, Received, Removed, Conflict, Kept, Error, Folder, Sent, Moved }

// Message: Erklärung für Fehler und Ordnerwechsel (englisch, für die Oberfläche).
public sealed record Activity(DateTime Time, ActivityKind Kind, string Path, string? From, string? Message = null);

public sealed record MeshOptions
{
    public required string Folder { get; init; }
    public required string DataDir { get; init; }
    public required string NodeName { get; init; }
    // Eine Datei, die sich in dieser Zeit noch geändert hat, wird erst beim nächsten Durchgang aufgenommen.
    public TimeSpan StableDelay { get; init; } = TimeSpan.FromSeconds(2);
    // Teildateien werden in Blöcken dieser Größe gesichert (auf die Platte gespült und mit eigenem Hash gemerkt).
    public int TransferChunkSize { get; init; } = 8 * 1024 * 1024;
    // Kommen so lange keine Daten, gilt die Übertragung als abgerissen (sie geht später an derselben Stelle weiter).
    public TimeSpan TransferIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);
    // Kam ein Inhalt kaputt an, wird er so lange nicht neu geladen (sonst lädt ein PC dieselben GB endlos).
    public TimeSpan BadHashPause { get; init; } = TimeSpan.FromMinutes(10);
    // Für Tests: misst den freien Platz statt DriveInfo (Ordner -> Platz).
    public Func<string, DiskSpace?>? MeasureDisk { get; init; }
}
