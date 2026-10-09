namespace StykkerCmd.Core.Operations;

public enum OperationKind
{
    Copy,
    Move,
    DeleteToTrash,
    DeletePermanent,
    CreateDirectory,
    Zip,
    Unzip,
}

// Ein einzelner, nicht weiter zerlegbarer Schritt. Er ist Grundlage für Fehlerberichte und für die Rechte-Wiederholung.
public enum LeafKind
{
    CopyFile,
    MoveFile,
    CreateDirectory,
    DeleteFile,
    DeleteEmptyDirectory,
    Trash,
    ZipEntry,
    ExtractEntry,
}

public sealed record LeafOperation(LeafKind Kind, string Source, string? Target);

// Ein Auftrag der Oberfläche. Bei Copy/Move ist TargetDirectory der Zielordner; bei CreateDirectory ist Sources[0] der neue Ordner.
// Zip: Sources sind die Einträge, TargetDirectory der Ordner des Archivs, ArchiveName dessen Dateiname.
// Unzip: Sources[0] ist das Archiv, TargetDirectory der Ordner, in dem der Entpack-Ordner entsteht.
public sealed record OperationRequest(
    OperationKind Kind,
    IReadOnlyList<string> Sources,
    string? TargetDirectory = null,
    string? ArchiveName = null);

public sealed record OperationProgress(
    string Phase,
    string? CurrentPath,
    int FilesDone,
    int FilesTotal,
    long BytesDone,
    long BytesTotal);

public enum IssueReason
{
    AccessDenied,
    Locked,
    NotFound,
    Loop,
    Verification,
    SameLocation,
    Exists,
    NoTrash,
    Other,
}

public sealed record OperationIssue(LeafOperation Leaf, IssueReason Reason, string Message);

public sealed record OperationResult(
    bool Cancelled,
    int FilesDone,
    long BytesDone,
    int Skipped,
    IReadOnlyList<OperationIssue> Issues,
    IReadOnlyList<string> PartialFiles);

public sealed record ConflictRequest(
    string Source,
    string Target,
    bool SourceIsDirectory,
    bool TargetIsDirectory,
    bool CanOverwrite,
    long SourceSize,
    long TargetSize,
    DateTimeOffset SourceModified,
    DateTimeOffset TargetModified);

public enum ConflictChoice
{
    Overwrite,
    Skip,
    Rename,
    Cancel,
}

// ApplyToAll gilt für alle weiteren Konflikte dieses Laufs.
public sealed record ConflictAnswer(ConflictChoice Choice, bool ApplyToAll = false);

public delegate Task<ConflictAnswer> ConflictHandler(ConflictRequest request);
