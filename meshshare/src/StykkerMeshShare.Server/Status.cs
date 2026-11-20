using StykkerMeshShare.Core;

namespace StykkerMeshShare.Server;

// Was /api/status liefert (die Seite fragt alle 1,5 s). Bewusst billig: nichts sortiert die ganze Dateiliste, nichts
// liest von der Platte; Anzahl und Größe zählt der Knoten beim Speichern mit.
public static class Status
{
    public static StatusDto Build(MeshNode node, PeerManager peers, bool local, bool localOnly)
    {
        peers.Watch();   // jemand sieht zu: Fortschritt der anderen PCs live nachfragen
        var totals = node.Summary;
        var queue = node.Queue;
        var progress = node.Progress;
        return new StatusDto(
            new NodeDto(
                node.NodeName,
                node.Folder,
                totals.Files,
                totals.Bytes,
                node.Seq,
                node.LastScan,
                node.FolderProblem,
                node.HeldDeletes,
                progress is { } t ? new TransferDto(t.Path, t.From, t.Size, t.Done, t.Resumed, t.BytesPerSecond, t.Checking) : null,
                node.PendingRescan,
                node.TransfersPaused,
                node.PausedBy,
                BuildInfo.Time,
                node.Disk is { } disk ? new DiskDto(disk.Root, disk.Free, disk.Total, node.MinFreeBytes, node.LowSpace) : null,
                localOnly,
                // Ordner, Gegenstellen und angehaltene Löschungen ändert man nur am PC selbst.
                local),
            peers.Peers.Select(p => new PeerDto(p.Address, p.Name, p.Online, p.Syncing, p.Manual, p.Duplicate,
                p.NodeId == null ? null : NetworkRoute.Label(p.Route), p.LastSync, p.LastError, p.Free, p.Total, p.Receiving, p.Paused, p.PausedBy, p.Build, p.Progress)).ToArray(),
            queue.Select(q => new QueueDto(q.Path, q.Size, q.From, q.Done)).ToArray(),
            node.PausedFiles(queue).Select(q => new QueueDto(q.Path, q.Size, null, q.Done)).ToArray(),
            node.Skipped.ToArray(),
            node.RecentActivity.Take(30).Select(a => new ActivityDto(a.Time, a.Kind.ToString().ToLowerInvariant(), a.Path, a.From, a.Message)).ToArray());
    }
}

public sealed record StatusDto(NodeDto Node, PeerDto[] Peers, QueueDto[] Queue, QueueDto[] Paused, string[] Skipped, ActivityDto[] Activity);

public sealed record NodeDto(string Name, string Folder, int Files, long Bytes, long Seq, DateTime? LastScan, string? Problem,
    int HeldDeletes, TransferDto? Transfer, bool Pending, bool TransfersPaused, string? PausedBy, string Build, DiskDto? Disk, bool LocalOnly, bool Local);

// Laufwerk des geteilten Ordners: frei, gesamt, die Grenze und ob sie unterschritten ist (dann wird nichts angenommen).
public sealed record DiskDto(string Root, long Free, long Total, long MinFree, bool Low);

public sealed record TransferDto(string Path, string From, long Size, long Done, long Resumed, double Rate, bool Checking);

public sealed record PeerDto(string Address, string? Name, bool Online, bool Syncing, bool Manual, bool Duplicate, string? Route,
    DateTime? LastSync, string? Error, long? Free, long? Total, bool? Receiving, bool? Paused, string? PausedBy,
    string? Build, SyncProgress? Progress);

public sealed record QueueDto(string Path, long Size, string? From, long Done);

public sealed record ActivityDto(DateTime Time, string Kind, string Path, string? From, string? Message);
