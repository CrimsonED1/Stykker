using System.Security.Cryptography;
using System.Text.Json;

namespace StykkerMeshShare.Core;

// Ein PC im Mesh: sein Ordner, sein Index und der Abgleich mit den anderen.
//
// Ablauf: Scan() nimmt lokale Änderungen in den Index auf (neue Versionsnummer für diesen PC). PullAsync() holt bei
// einer Gegenstelle alles, was sich dort seit dem letzten Abruf geändert hat, und übernimmt es nach den Regeln der
// Versionsvektoren. Niemand schiebt Dateien zu einem anderen: jeder PC holt selbst ab.
//
// Sicherheitsnetze: Wird eine Datei hier überschrieben oder gelöscht, landet die alte Fassung im Papierkorb
// (DataDir/trash). Verschwinden auf einen Schlag (fast) alle Dateien, hält der Scan die Löschung an (HeldDeletes),
// bis jemand sie bestätigt – zum Beispiel, wenn ein Laufwerk nicht eingehängt ist.
public sealed partial class MeshNode : IDisposable
{
    private const int ActivityLimit = 200;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly MeshOptions _options;
    private string _folder;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, FileEntry> _entries = new(PathRules.Comparer);
    private readonly Dictionary<string, long> _cursors = new(StringComparer.Ordinal);
    private readonly LinkedList<Activity> _activity = new();
    private readonly object _activityLock = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _seq;
    private bool _allowMassDelete;
    private DateTime _lastTrashPurge = DateTime.MinValue;
    private readonly object _scanSerial = new();          // nur ein Scan zur Zeit
    private int _folderGen;
    private int _holdTransfers;                            // > 0: keine Downloads (Ordnerwechsel läuft)                                // zählt Ordnerwechsel: ein Abruf von vorher setzt keinen Zeiger
    private Totals _totals = new(0, 0);

    public string NodeId { get; }
    public string NodeName => _options.NodeName;
    public string Folder => _folder;
    public event Action<string>? FolderChanged;
    public string DataDir => _options.DataDir;

    public DateTime? LastScan { get; private set; }
    public string? FolderProblem { get; private set; }
    public int HeldDeletes { get; private set; }
    public bool PendingRescan { get; private set; }      // eine Datei war noch in Arbeit: der nächste Scan holt sie

    // Jeder Eintrag der Aktivität, sobald er entsteht (für Meldungen im Tray). Läuft außerhalb der Sperren.
    public event Action<Activity>? Recorded;

    public MeshNode(MeshOptions options)
    {
        _options = options;
        _folder = Path.GetFullPath(options.Folder);
        Directory.CreateDirectory(options.DataDir);
        Directory.CreateDirectory(options.Folder);
        var state = Load();
        NodeId = state?.NodeId ?? Guid.NewGuid().ToString("N");
        if (state != null)
        {
            _seq = state.Seq;
            foreach (var entry in state.Entries) _entries[entry.Path] = entry;
            foreach (var (peer, seq) in state.Cursors) _cursors[peer] = seq;
        }
        InitTransfers();
        Save();
    }

    private string StateFile => Path.Combine(_options.DataDir, "index.json");
    public string TrashDir => Path.Combine(_options.DataDir, "trash");

    public long Seq => Interlocked.Read(ref _seq);

    // Anzahl und Größe der Dateien; wird beim Speichern des Index neu gezählt (für die Statuszeile, ohne Sperre).
    public sealed record Totals(int Files, long Bytes);
    public Totals Summary => _totals;

    public IReadOnlyList<FileEntry> Files
    {
        get
        {
            _gate.Wait();
            try { return _entries.Values.Where(e => !e.Deleted).OrderBy(e => e.Path, PathRules.Comparer).ToList(); }
            finally { _gate.Release(); }
        }
    }

    public IReadOnlyList<Activity> RecentActivity
    {
        get { lock (_activityLock) return _activity.ToList(); }
    }

    // ── Lokale Änderungen ──

    public void Scan()
    {
        lock (_scanSerial) ScanCore();
        Notify();
    }

    // Der Nutzer hat bestätigt, dass die angehaltenen Löschungen gewollt sind.
    public void AllowHeldDeletes()
    {
        _allowMassDelete = true;
        Scan();
    }

    private sealed record SeenFile(string Relative, string Full, long Size, long MtimeTicks);

    // In vier Schritten, damit die Sperre nie lange gehalten wird (sonst warten die anderen PCs, die Seite und der
    // Abgleich, während eine große Datei gehasht wird):
    //  1. Ordner lesen (ohne Sperre)  2. vergleichen, was neu ist (Sperre)  3. hashen (ohne Sperre)
    //  4. übernehmen (Sperre) – aber nur, was sich in der Zwischenzeit weder im Index noch auf der Platte geändert hat.
    private void ScanCore()
    {
        var folder = _folder;
        if (!Directory.Exists(folder))
        {
            FolderProblem = "The shared folder does not exist.";
            return;
        }
        FolderProblem = null;
        bool pending = false;
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(PathRules.Comparer);
        var found = new List<SeenFile>();
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Verknüpfungen und Junctions: nicht folgen, sonst landet ein fremder Ordner im Mesh.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var full in Directory.EnumerateFiles(folder, "*", enumeration))
        {
            var relative = PathRules.Normalize(PathRules.ToRelative(folder, full));
            if (relative == null || PathRules.IsIgnored(relative)) continue;
            seen.Add(relative);
            FileInfo info;
            try { info = new FileInfo(full); _ = info.Length; }
            catch (IOException) { continue; }
            var mtime = info.LastWriteTimeUtc;
            // Gerade geändert: erst beim nächsten Mal. Eine Zeit in der Zukunft (falsche Uhr) gilt als ruhig.
            var age = now - mtime;
            if (age >= TimeSpan.Zero && age < _options.StableDelay) { pending = true; continue; }
            found.Add(new SeenFile(relative, full, info.Length, mtime.Ticks));
        }

        List<(SeenFile File, FileEntry? Entry)> toHash;
        _gate.Wait();
        try
        {
            if (!PathRules.Comparer.Equals(folder, _folder)) return;   // Ordner inzwischen gewechselt
            toHash = new List<(SeenFile, FileEntry?)>();
            foreach (var file in found)
            {
                _entries.TryGetValue(file.Relative, out var entry);
                if (entry is { Deleted: false } && entry.Size == file.Size && entry.MtimeTicks == file.MtimeTicks) continue;
                toHash.Add((file, entry));
            }
        }
        finally { _gate.Release(); }

        var hashed = new List<(SeenFile File, FileEntry? Entry, string Hash)>();
        foreach (var (file, entry) in toHash)
        {
            var hash = TryHash(file.Full);
            if (hash == null) { pending = true; continue; }   // gesperrt: später noch einmal
            hashed.Add((file, entry, hash));
        }

        _gate.Wait();
        try
        {
            if (!PathRules.Comparer.Equals(folder, _folder)) return;
            bool changed = false;
            foreach (var (file, before, hash) in hashed)
            {
                _entries.TryGetValue(file.Relative, out var entry);
                // Inzwischen hat der Abgleich den Eintrag geändert, oder die Datei hat sich noch einmal geändert.
                if (!ReferenceEquals(entry, before) || !SameOnDisk(file)) { pending = true; continue; }
                if (entry is { Deleted: false } && entry.Hash == hash)
                {
                    _entries[file.Relative] = entry with { Size = file.Size, MtimeTicks = file.MtimeTicks };
                    changed = true;
                    continue;
                }
                _entries[file.Relative] = new FileEntry
                {
                    Path = file.Relative,
                    Size = file.Size,
                    MtimeTicks = file.MtimeTicks,
                    Hash = hash,
                    Version = VersionVector.Bump(entry?.Version ?? new(), NodeId),
                    Seq = ++_seq,
                    OriginId = NodeId,
                    OriginName = NodeName,
                };
                Record(entry is { Deleted: false } ? ActivityKind.Changed : ActivityKind.Added, file.Relative, null);
                changed = true;
            }

            // Verschwunden: nicht gesehen und auch jetzt nicht auf der Platte (eine Datei kann während des Scans vom
            // Abgleich gekommen sein).
            var gone = _entries.Values.Where(e => !e.Deleted && !seen.Contains(e.Path) && !OnDisk(e.Path)).ToList();
            int live = _entries.Values.Count(e => !e.Deleted);
            bool massive = gone.Count > 0 && ((live >= 3 && gone.Count == live) || (gone.Count >= 50 && gone.Count * 2 > live));
            if (massive && !_allowMassDelete)
            {
                HeldDeletes = gone.Count;
            }
            else
            {
                HeldDeletes = 0;
                _allowMassDelete = false;
                foreach (var entry in gone)
                {
                    _entries[entry.Path] = Tombstone(entry, VersionVector.Bump(entry.Version, NodeId), NodeId, NodeName);
                    Record(ActivityKind.Deleted, entry.Path, null);
                    changed = true;
                }
            }

            LastScan = DateTime.UtcNow;
            PendingRescan = pending;
            PurgeTrash();
            if (changed) Save();
        }
        finally { _gate.Release(); }
    }

    private static bool SameOnDisk(SeenFile file)
    {
        try
        {
            var info = new FileInfo(file.Full);
            return info.Exists && info.Length == file.Size && info.LastWriteTimeUtc.Ticks == file.MtimeTicks;
        }
        catch (IOException) { return false; }
    }

    private bool OnDisk(string relative) => Full(relative) is { } full && File.Exists(full);

    // Der volle Pfad im Ordner, ohne Verknüpfungen auf dem Weg (siehe PathRules.ToFullNoLinks).
    private string? Full(string relative) => PathRules.ToFullNoLinks(_folder, relative);

    private FileEntry Tombstone(FileEntry entry, Dictionary<string, long> version, string? originId, string? originName) => entry with
    {
        Deleted = true,
        Size = 0,
        Hash = null,
        Version = version,
        Seq = ++_seq,
        OriginId = originId,
        OriginName = originName,
    };

    // ── Was die Gegenstellen abfragen ──

    public ChangePage GetChanges(long since)
    {
        _gate.Wait();
        try
        {
            var list = _entries.Values.Where(e => e.Seq > since).OrderBy(e => e.Seq).ToList();
            return new ChangePage(NodeId, NodeName, _seq, list);
        }
        finally { _gate.Release(); }
    }

    // Öffnet die Datei nur, wenn sie hier genau diesen Inhalt hat.
    public Stream? OpenFile(string path, string hash)
    {
        var relative = PathRules.Normalize(path);
        if (relative == null) return null;
        _gate.Wait();
        try
        {
            if (!_entries.TryGetValue(relative, out var entry) || entry.Deleted || entry.Hash != hash) return null;
            var full = Full(relative);
            if (full == null || !File.Exists(full)) return null;
            // Hat sich die Datei seit dem Scan geändert, wird sie nicht ausgeliefert; der nächste Scan nimmt sie auf.
            if (!DiskMatches(entry, full)) { PendingRescan = true; return null; }
            try
            {
                var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // Ganz ausgeliefert wird mitgehasht: stimmt der Inhalt nicht mehr (geändert, ohne dass sich Größe oder
                // Zeit bewegt haben), wird der Eintrag zum Neuhashen markiert und erst danach wieder ausgeliefert.
                return new VerifyingStream(stream, hash, () => Invalidate(relative, hash));
            }
            catch (IOException) { return null; }
        }
        finally { _gate.Release(); }
    }

    private void Invalidate(string relative, string hash)
    {
        _gate.Wait();
        try
        {
            if (_entries.TryGetValue(relative, out var entry) && entry.Hash == hash) _entries[relative] = entry with { MtimeTicks = 0 };
            PendingRescan = true;
        }
        finally { _gate.Release(); }
    }

    // Für die Oberfläche: eine Datei im Ordner zum Herunterladen. Hilfsdateien (.stykker…) und Pfade über
    // Verknüpfungen gibt es nicht.
    public FileStream? OpenForDownload(string path)
    {
        var relative = PathRules.Normalize(path);
        if (relative == null || PathRules.IsIgnored(relative)) return null;
        var full = Full(relative);
        if (full == null || !File.Exists(full)) return null;
        try { return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // Wartet, bis sich der Index über die Nummer since hinaus ändert (lange Abfrage der Gegenstellen).
    public async Task<long> WaitForChangeAsync(long since, TimeSpan timeout, CancellationToken ct)
    {
        Task wait;
        _gate.Wait(ct);
        try
        {
            if (_seq > since) return _seq;
            wait = _changed.Task;
        }
        finally { _gate.Release(); }
        await Task.WhenAny(wait, Task.Delay(timeout, ct)).ConfigureAwait(false);
        return Seq;
    }

    // ── Abgleich mit einer Gegenstelle ──

    public sealed record PullResult(int Applied, bool Complete);

    // Holt bei einer Gegenstelle alles, was sich seit dem letzten Abruf geändert hat. Vorher ein Scan: eine lokale
    // Änderung muss im Index stehen, bevor etwas von außen darüber entschieden wird.
    public async Task<PullResult> PullAsync(IPeerSource peer, CancellationToken ct = default)
    {
        Scan();
        // Erst nur fragen, wer die Gegenstelle ist und wo sie steht (keine Einträge über long.MaxValue).
        var probe = await peer.GetChangesAsync(long.MaxValue, ct).ConfigureAwait(false);
        if (probe.NodeId == NodeId) return new PullResult(0, true);
        long since;
        int generation;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            since = _cursors.GetValueOrDefault(probe.NodeId);
            generation = _folderGen;
        }
        finally { _gate.Release(); }
        // Hat die Gegenstelle neu angefangen (kleinere Nummer als gemerkt), alles neu lesen.
        if (probe.Seq < since) since = 0;
        if (probe.Seq == since) return new PullResult(0, true);
        var page = await peer.GetChangesAsync(since, ct).ConfigureAwait(false);

        int applied = 0;
        bool complete = true;

        // Zuerst alles ohne Download (Löschungen, schon vorhandener Inhalt), dann die Downloads in der Reihenfolge
        // der Warteschlange. Die Reihenfolge wird vor jeder Datei neu gelesen: so gilt ein Umsortieren sofort.
        var entries = page.Entries
            .Select(e => (Entry: e, Path: PathRules.Normalize(e.Path)))
            .Where(x => x.Path != null && !PathRules.IsIgnored(x.Path) && Plausible(x.Entry))
            .Select(x => x.Entry with { Path = x.Path! }).ToList();
        List<FileEntry> downloads;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { downloads = entries.Where(NeedsDownload).ToList(); }
        finally { _gate.Release(); }
        var quick = entries.Except(downloads).ToList();
        lock (_queueLock)
        {
            _queue.Clear();
            _queue.AddRange(downloads.Select(d => new QueueItem(d.Path, d.Size, page.NodeName, _partials.BytesOf(d.Hash!))));
        }

        try
        {
            foreach (var remote in quick)
            {
                ct.ThrowIfCancellationRequested();
                var outcome = await ApplyAsync(remote, page.NodeName, peer, ct).ConfigureAwait(false);
                if (outcome == Outcome.Applied) applied++;
                else if (outcome is Outcome.Retry or Outcome.Deferred) complete = false;
            }

            var byPath = downloads.ToDictionary(d => d.Path, PathRules.Comparer);
            var left = new HashSet<string>(byPath.Keys, PathRules.Comparer);
            while (left.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (TransfersPaused || _holdTransfers > 0) { complete = false; break; }
                FileEntry remote;
                lock (_queueLock) remote = byPath[Ordered(_queue.Where(q => left.Contains(q.Path))).First().Path];
                var outcome = await ApplyAsync(remote, page.NodeName, peer, ct).ConfigureAwait(false);
                if (outcome == Outcome.Deferred) continue;   // eine andere Datei wurde vorgezogen; diese kommt später
                left.Remove(remote.Path);
                if (outcome == Outcome.Retry)
                {
                    complete = false;
                    lock (_queueLock) _queue.RemoveAll(q => PathRules.Comparer.Equals(q.Path, remote.Path));
                }
                else
                {
                    if (outcome == Outcome.Applied) applied++;
                    Dequeue(remote.Path);
                }
            }
        }
        finally
        {
            lock (_queueLock) _queue.Clear();
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Wurde inzwischen der Ordner gewechselt („Start empty“), muss alles neu gelesen werden: Zeiger nicht setzen.
            if (complete && generation == _folderGen) _cursors[page.NodeId] = page.Seq;
            Save();
        }
        finally { _gate.Release(); }
        if (applied > 0) Notify();
        return new PullResult(applied, complete);
    }

    private enum Outcome { Nothing, Applied, Retry, Deferred }

    // Einträge einer Gegenstelle, die so nicht stimmen können, werden übergangen: der Hash geht in Dateinamen
    // (Teildateien), die Zeit in DateTime, die Größe in die Platzprüfung.
    private static bool Plausible(FileEntry remote) =>
        remote.Version != null
        && remote.Size >= 0
        && remote.MtimeTicks > 0 && remote.MtimeTicks < DateTime.MaxValue.Ticks
        && (remote.Deleted ? remote.Hash == null : PathRules.IsHash(remote.Hash));

    // Grobe Vorauswahl (unter der Sperre): ein Eintrag mit Inhalt, der hier unter diesem Pfad noch fehlt und nicht
    // älter ist als der eigene. Was wirklich passiert, entscheidet Decide() erst direkt vor dem Laden.
    private bool NeedsDownload(FileEntry remote)
    {
        if (remote.Deleted || remote.Hash == null || IsSkipped(remote.Path)) return false;
        if (!_entries.TryGetValue(remote.Path, out var local)) return true;
        if (!local.Deleted && local.Hash == remote.Hash) return false;
        return VersionVector.Compare(local.Version, remote.Version) is not (Order.Equal or Order.LocalNewer);
    }

    // Was mit einem Eintrag der Gegenstelle zu tun ist. Entschieden wird unter der Sperre, geladen wird ohne sie, und
    // vor dem Übernehmen wird geprüft, dass sich hier in der Zwischenzeit nichts geändert hat.
    private async Task<Outcome> ApplyAsync(FileEntry remote, string peerName, IPeerSource peer, CancellationToken ct)
    {
        Plan plan;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { plan = Decide(remote, peerName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Zum Beispiel: die Datei soll gelöscht werden, ist hier aber in Word offen. Später noch einmal; die
            // übrigen Änderungen dieser Gegenstelle kommen trotzdem an.
            Record(ActivityKind.Error, remote.Path, null, ex.Message);
            return Outcome.Retry;
        }
        finally { _gate.Release(); }

        if (plan.Kind == PlanKind.Done) return plan.Changed ? Outcome.Applied : Outcome.Nothing;
        if (plan.Kind == PlanKind.Retry) return Outcome.Retry;
        if (IsSkipped(remote.Path)) return Outcome.Nothing;        // „nicht laden“ in der Oberfläche
        if (HashOnHold(remote.Hash!)) return Outcome.Retry;        // kam eben kaputt an: eine Weile nicht neu laden

        // Laden: zuerst eine Datei mit demselben Inhalt hier am PC suchen, sonst bei der Gegenstelle. Geladen wird in
        // eine Teildatei, die einen Abbruch übersteht; SetOrder() kann diese Datei anhalten (Deferred).
        using var current = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_queueLock)
        {
            _currentPath = remote.Path;
            _currentCancel = current;
        }
        try
        {
            var result = await LoadAsync(remote, peerName, peer, current.Token).ConfigureAwait(false);
            if (result.Status != LoadStatus.Loaded) return Outcome.Retry;

            bool committed;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { committed = Commit(plan, remote, result.DataPath!); }
            finally { _gate.Release(); }
            if (committed) _partials.Discard(remote.Hash!);
            return committed ? Outcome.Applied : Outcome.Retry;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Outcome.Deferred; }
        finally
        {
            lock (_queueLock)
            {
                // Angehalten: die Zeile zeigt den Stand, ab dem es weitergeht (der letzte gesicherte Block).
                if (_progress is { Checking: false } stopped && PathRules.Comparer.Equals(stopped.Path, remote.Path))
                {
                    long kept = stopped.Done;
                    var at = _queue.FindIndex(q => PathRules.Comparer.Equals(q.Path, remote.Path));
                    if (at >= 0) _queue[at] = _queue[at] with { Done = kept };
                }
                _currentPath = null;
                _currentCancel = null;
            }
            _progress = null;
        }
    }

    private enum PlanKind { Done, Retry, Download }

    // Download: remote-Inhalt nach Target schreiben. Expected* beschreibt, was hier vor dem Schreiben liegen muss.
    // Bei einer Konfliktkopie (AsConflictCopy) bekommt die eigene Datei unter remote.Path den zusammengeführten Vektor
    // (MainVersion) erst, wenn die Kopie wirklich angekommen ist – sonst ginge die fremde Fassung verloren.
    private sealed record Plan(PlanKind Kind, bool Changed = false, string? Target = null, Dictionary<string, long>? Version = null,
        FileEntry? ExpectedAtTarget = null, FileEntry? LoserToMove = null, string? LoserTarget = null, bool AsConflictCopy = false,
        FileEntry? Main = null, Dictionary<string, long>? MainVersion = null);

    private Plan Decide(FileEntry remote, string peerName)
    {
        _entries.TryGetValue(remote.Path, out var local);
        var full = Full(remote.Path);
        if (full == null) return new Plan(PlanKind.Done);

        if (local == null)
        {
            // Liegt dort schon eine Datei, die der Scan noch nicht kennt, erst scannen – nie blind überschreiben.
            if (File.Exists(full)) return new Plan(PlanKind.Retry);
            if (remote.Deleted)
            {
                _entries[remote.Path] = remote with { Seq = ++_seq };
                return new Plan(PlanKind.Done, Changed: false);
            }
            return new Plan(PlanKind.Download, Target: remote.Path, Version: remote.Version);
        }

        // Die Datei auf der Platte muss noch dem Index entsprechen, sonst erst scannen.
        if (!DiskMatches(local, full)) return new Plan(PlanKind.Retry);

        switch (VersionVector.Compare(local.Version, remote.Version))
        {
            case Order.Equal:
            case Order.LocalNewer:
                return new Plan(PlanKind.Done);

            case Order.RemoteNewer:
                if (remote.Deleted)
                {
                    if (!local.Deleted)
                    {
                        MoveToTrash(full, remote.Path);
                        Record(ActivityKind.Removed, remote.Path, peerName);
                    }
                    _entries[remote.Path] = Tombstone(local, remote.Version, remote.OriginId, remote.OriginName);
                    return new Plan(PlanKind.Done, Changed: !local.Deleted);
                }
                if (!local.Deleted && local.Hash == remote.Hash)
                {
                    _entries[remote.Path] = local with { Version = remote.Version, Seq = ++_seq };
                    return new Plan(PlanKind.Done);
                }
                return new Plan(PlanKind.Download, Target: remote.Path, Version: remote.Version, ExpectedAtTarget: local);

            default: // gleichzeitig geändert
                var merged = VersionVector.Merge(local.Version, remote.Version);
                if (local.Deleted && remote.Deleted)
                {
                    _entries[remote.Path] = local with { Version = merged, Seq = ++_seq };
                    return new Plan(PlanKind.Done);
                }
                if (!local.Deleted && !remote.Deleted && local.Hash == remote.Hash)
                {
                    _entries[remote.Path] = local with { Version = merged, Seq = ++_seq };
                    return new Plan(PlanKind.Done);
                }
                if (remote.Deleted)
                {
                    // Hier geändert, dort gelöscht: die Änderung gewinnt und geht mit einer neuen Nummer zurück.
                    _entries[remote.Path] = local with { Version = VersionVector.Bump(merged, NodeId), Seq = ++_seq };
                    Record(ActivityKind.Kept, remote.Path, peerName);
                    return new Plan(PlanKind.Done, Changed: true);
                }
                if (local.Deleted)
                {
                    // Hier gelöscht, dort geändert: die Änderung gewinnt, die Datei kommt zurück.
                    return new Plan(PlanKind.Download, Target: remote.Path, Version: merged, ExpectedAtTarget: local);
                }
                // Beide geändert: Der Gewinner bleibt unter dem Namen, der Verlierer wird Konfliktkopie. Die Regel
                // (spätere Änderungszeit, bei Gleichstand der größere Hash) ist auf beiden Seiten dieselbe.
                bool remoteWins = remote.MtimeTicks != local.MtimeTicks
                    ? remote.MtimeTicks > local.MtimeTicks
                    : string.CompareOrdinal(remote.Hash, local.Hash) > 0;
                if (remoteWins)
                {
                    var loserName = PathRules.ConflictName(remote.Path, local.OriginName, local.MtimeTicks, local.Hash);
                    return new Plan(PlanKind.Download, Target: remote.Path, Version: merged, ExpectedAtTarget: local,
                        LoserToMove: local, LoserTarget: loserName);
                }
                var copyName = PathRules.ConflictName(remote.Path, remote.OriginName, remote.MtimeTicks, remote.Hash);
                if (_entries.TryGetValue(copyName, out var existingCopy) && !existingCopy.Deleted && existingCopy.Hash == remote.Hash)
                {
                    _entries[remote.Path] = local with { Version = merged, Seq = ++_seq };
                    return new Plan(PlanKind.Done);
                }
                var copyFull = Full(copyName);
                if (copyFull == null) return new Plan(PlanKind.Done);
                if (File.Exists(copyFull)) return new Plan(PlanKind.Retry);   // erst scannen
                return new Plan(PlanKind.Download, Target: copyName,
                    Version: VersionVector.Bump(existingCopy?.Version ?? new(), NodeId),
                    ExpectedAtTarget: existingCopy, AsConflictCopy: true, Main: local, MainVersion: merged);
        }
    }

    private bool DiskMatches(FileEntry entry, string full)
    {
        if (entry.Deleted) return !File.Exists(full);
        try
        {
            var info = new FileInfo(full);
            return info.Exists && info.Length == entry.Size && info.LastWriteTimeUtc.Ticks == entry.MtimeTicks;
        }
        catch (IOException) { return false; }
    }

    private enum LoadStatus { Loaded, Failed }
    private sealed record LoadResult(LoadStatus Status, string? DataPath = null);
    private DateTime _spaceWarned = DateTime.MinValue;

    // Lädt den Inhalt in seine Teildatei und setzt dort fort, wo die letzte Übertragung aufgehört hat (auch nach einem
    // Neustart oder von einer anderen Gegenstelle mit demselben Inhalt). Stimmt am Ende der SHA-256 der ganzen Datei
    // nicht, wird die Teildatei verworfen und der Inhalt eine Weile nicht neu geladen.
    // Abgebrochen (vorgezogene Datei, alles anhalten) oder abgerissen (keine Daten mehr): die Teildatei bleibt offen
    // geparkt und geht ohne neues Prüfen weiter.
    private async Task<LoadResult> LoadAsync(FileEntry remote, string peerName, IPeerSource peer, CancellationToken ct)
    {
        var hash = remote.Hash!;
        string? localCopy = null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var same = _entries.Values.FirstOrDefault(e => !e.Deleted && e.Hash == hash);
            if (same != null)
            {
                var full = Full(same.Path);
                if (full != null && DiskMatches(same, full)) localCopy = full;
            }
        }
        finally { _gate.Release(); }
        string from = localCopy != null ? NodeName : peerName;

        var handle = TakeParked(hash);
        if (handle == null)
        {
            // Eine angefangene Teildatei wird erst ganz gelesen und geprüft; bei großen Dateien dauert das einen Moment.
            long already = _partials.BytesOf(hash);
            if (already > 0) _progress = new TransferProgress(remote.Path, from, remote.Size, already, already, 0, Checking: true);
            // Ohne ct: die Prüfung läuft zu Ende, sonst bliebe die Teildatei offen. Abgebrochen wird gleich danach.
            try { handle = await Task.Run(() => _partials.Open(remote.Path, hash, remote.Size)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return new(LoadStatus.Failed); }
        }

        Stream? source = null;
        bool keep = false;          // Teildatei offen parken statt schließen
        bool writeFailed = false;   // die eigene Platte hat versagt: die Teildatei ist nicht mehr sicher
        try
        {
            ct.ThrowIfCancellationRequested();
            long done = handle.Stream.Position;
            ReportProgress(remote.Path, from, remote.Size, done, handle.Resumed, force: true);
            if (!EnoughSpace(remote.Size - done))
            {
                if (DateTime.UtcNow - _spaceWarned > TimeSpan.FromMinutes(10))
                {
                    _spaceWarned = DateTime.UtcNow;
                    Record(ActivityKind.Error, remote.Path, peerName, "Not enough free disk space for this file");
                }
                keep = true;
                return new(LoadStatus.Failed);
            }

            bool tooLong = false;
            if (done < remote.Size)
            {
                if (localCopy != null)
                {
                    try { source = new FileStream(localCopy, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete) { Position = done }; }
                    catch (IOException) { source = null; }
                }
                source ??= await peer.OpenFileAsync(remote.Path, hash, done, ct).ConfigureAwait(false);
                if (source == null) { keep = true; return new(LoadStatus.Failed); }

                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(256 * 1024);
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    while (true)
                    {
                        idle.CancelAfter(_options.TransferIdleTimeout);
                        int read;
                        try { read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            // So lange keine Daten: Verbindung hängt. Später an derselben Stelle weiter.
                            keep = true;
                            return new(LoadStatus.Failed);
                        }
                        if (read <= 0) break;
                        if (done + read > remote.Size) { tooLong = true; break; }
                        try { await _partials.WriteAsync(handle, buffer.AsMemory(0, read)).ConfigureAwait(false); }
                        catch (IOException) { writeFailed = true; throw; }
                        done += read;
                        _meter.Add(read);
                        ReportProgress(remote.Path, from, remote.Size, done, handle.Resumed);
                        ct.ThrowIfCancellationRequested();
                    }
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
            }
            await handle.Stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);

            // Zu kurz (Verbindung weg): Teildatei behalten, beim nächsten Mal weiter.
            if (!tooLong && done < remote.Size) { keep = true; return new(LoadStatus.Failed); }

            var actual = Convert.ToHexStringLower(handle.FileHash.GetHashAndReset());
            if (!tooLong && actual == hash)
            {
                handle.Dispose();
                return new(LoadStatus.Loaded, handle.DataPath);
            }
            // Inhalt stimmt nicht (die Datei hat sich dort während der Übertragung geändert, oder Daten sind
            // unterwegs kaputtgegangen): verwerfen und eine Weile nicht neu laden.
            handle.Dispose();
            _partials.Discard(hash);
            Record(ActivityKind.Error, remote.Path, peerName, "Check failed (SHA-256); loading again");
            HoldHash(hash);
            return new(LoadStatus.Failed);
        }
        catch (OperationCanceledException)
        {
            keep = true;   // angehalten: geht später weiter
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // Ein Lesefehler der Verbindung lässt die Teildatei heil (geschrieben wird nur, was ganz gelesen wurde).
            keep = true;
            return new(LoadStatus.Failed);
        }
        finally
        {
            if (source != null) await source.DisposeAsync().ConfigureAwait(false);
            if (keep && !writeFailed && handle.Stream.CanWrite) Park(hash, handle);
            else handle.Dispose();
        }
    }

    // Genug Platz für den Rest der Datei, und es bleibt noch etwas übrig (die Platte soll nicht volllaufen).
    private bool EnoughSpace(long needed)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_folder));
            if (string.IsNullOrEmpty(root)) return true;
            return new DriveInfo(root).AvailableFreeSpace >= needed + 512L * 1024 * 1024;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return true; }
    }

    private bool Commit(Plan plan, FileEntry remote, string temp)
    {
        var target = plan.Target!;
        var full = Full(target);
        if (full == null) return false;
        _entries.TryGetValue(target, out var now);
        // Seit der Entscheidung darf sich am Ziel nichts geändert haben – weder im Index noch auf der Platte.
        if (plan.ExpectedAtTarget == null ? now != null && !now.Deleted : now?.Seq != plan.ExpectedAtTarget.Seq) return false;
        if (now != null ? !DiskMatches(now, full) : File.Exists(full)) return false;

        try
        {
            if (plan.LoserToMove != null && plan.LoserTarget != null && File.Exists(full))
            {
                // Die eigene Fassung wird zur Konfliktkopie; der nächste Scan nimmt sie als neue Datei auf.
                var loserFull = Full(plan.LoserTarget);
                if (loserFull == null) return false;
                if (File.Exists(loserFull)) MoveToTrash(full, target);
                else File.Move(full, loserFull);
                Record(ActivityKind.Conflict, plan.LoserTarget, remote.OriginName);
            }
            else if (File.Exists(full))
            {
                MoveToTrash(full, target);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Move(temp, full, overwrite: false);
            File.SetLastWriteTimeUtc(full, new DateTime(remote.MtimeTicks, DateTimeKind.Utc));
            var info = new FileInfo(full);
            _entries[target] = new FileEntry
            {
                Path = target,
                Size = info.Length,
                MtimeTicks = info.LastWriteTimeUtc.Ticks,
                Hash = remote.Hash,
                Version = plan.Version!,
                Seq = ++_seq,
                OriginId = plan.AsConflictCopy ? NodeId : remote.OriginId,
                OriginName = plan.AsConflictCopy ? NodeName : remote.OriginName,
            };
            if (plan is { AsConflictCopy: true, Main: not null, MainVersion: not null }
                && _entries.TryGetValue(plan.Main.Path, out var main) && main.Seq == plan.Main.Seq)
                _entries[plan.Main.Path] = main with { Version = plan.MainVersion, Seq = ++_seq };
            Record(plan.AsConflictCopy ? ActivityKind.Conflict : ActivityKind.Received, target, remote.OriginName);
            Save();
            return true;
        }
        catch (IOException ex)
        {
            Record(ActivityKind.Error, target, null, ex.Message);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Record(ActivityKind.Error, target, null, ex.Message);
            return false;
        }
    }

    // ── Ordner wechseln ──

    // Der neue Ordner muss leer sein oder noch nicht bestehen.
    //  move = true:  alle Dateien ziehen in den neuen Ordner um (Zeiten bleiben). Der Index gilt weiter; die anderen PCs
    //                merken nichts davon.
    //  move = false: der neue Ordner fängt leer an und holt alles von den anderen PCs. Der alte Ordner bleibt, wie er
    //                ist; an die anderen PCs geht keine Löschung. Dateien, die nur hier lagen, bleiben im alten Ordner.
    // Rückgabe: null bei Erfolg, sonst der Grund (englisch, für die Oberfläche).
    public string? ChangeFolder(string newFolder, bool move)
    {
        // Erst den laufenden Download anhalten und die offenen Teildateien schließen: sie liegen im Ordner
        // (.stykker\partial) und müssen mit umziehen bzw. weg.
        Interlocked.Increment(ref _holdTransfers);
        try
        {
            CancellationTokenSource? running;
            lock (_queueLock) running = _currentCancel;
            Cancel(running);
            for (int i = 0; i < 100 && _currentPath != null; i++) Thread.Sleep(50);
            CloseParked();
            return ChangeFolderCore(newFolder, move);
        }
        finally { Interlocked.Decrement(ref _holdTransfers); }
    }

    // Prüft einen Ordner, bevor er gewählt wird (für die Oberfläche beim Tippen): null, wenn er taugt.
    public string? CheckFolder(string newFolder)
    {
        var problem = FolderRules.Check(newFolder, _folder, _options.DataDir);
        if (problem != null) return problem;
        try
        {
            var target = Path.GetFullPath(newFolder);
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                return "The new folder is not empty. Choose an empty folder, or one that does not exist yet.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return ex.Message; }
        return null;
    }

    private string? ChangeFolderCore(string newFolder, bool move)
    {
        string target;
        string old;
        _gate.Wait();
        try
        {
            var problem = FolderRules.Check(newFolder, _folder, _options.DataDir);
            if (problem != null) return problem;
            target = Path.GetFullPath(newFolder).TrimEnd(Path.DirectorySeparatorChar);
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                return "The new folder is not empty. Choose an empty folder, or one that does not exist yet.";

            Directory.CreateDirectory(target);
            if (move)
            {
                var error = MoveAll(_folder, target);
                if (error != null) return error;
            }
            else
            {
                // Ohne die Einträge der vorhandenen Dateien holt der Abgleich sie neu; Grabsteine bleiben, damit eine
                // alte Löschung nicht zurückkommt. Alle Gegenstellen werden von vorn gelesen.
                foreach (var entry in _entries.Values.Where(e => !e.Deleted).ToList()) _entries.Remove(entry.Path);
                _cursors.Clear();
            }
            old = _folder;
            _folder = target;
            _folderGen++;
            OpenPartialStore();
            HeldDeletes = 0;
            _allowMassDelete = false;
            Record(ActivityKind.Folder, "", null, move ? $"Folder moved from {old}" : "Folder changed: fetching the files from the other PCs");
            Save();
        }
        finally { _gate.Release(); }
        // Beim Neuanfang bleiben die alten Dateien liegen; die Teildateien im alten Ordner braucht niemand mehr.
        if (!move)
        {
            try { Directory.Delete(WorkDir(old), recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        FolderChanged?.Invoke(target);
        Scan();
        return null;
    }

    // Zieht alle Dateien um. Geht unterwegs etwas schief, werden die schon verschobenen zurückgelegt.
    private static string? MoveAll(string from, string to)
    {
        if (!Directory.Exists(from)) return null;
        var moved = new List<(string From, string To)>();
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var source in Directory.EnumerateFiles(from, "*", enumeration).ToList())
            {
                var destination = Path.Combine(to, Path.GetRelativePath(from, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var time = File.GetLastWriteTimeUtc(source);
                File.Move(source, destination);
                File.SetLastWriteTimeUtc(destination, time);
                moved.Add((source, destination));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var (source, destination) in Enumerable.Reverse(moved))
            {
                try { File.Move(destination, source); }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException) { }
            }
            return $"Could not move the files: {ex.Message} Nothing was changed.";
        }
        // Leere Unterordner bleiben sonst im alten Ordner stehen; der alte Ordner selbst auch, wenn er leer ist.
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(from, "*", enumeration).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            if (!Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    // ── Hochladen aus der Oberfläche ──

    // Legt eine Datei in den Ordner (über eine Temp-Datei). Eine vorhandene Datei gleichen Namens bekommt " (2)" usw.
    public async Task<string?> AddFileAsync(string relativePath, Stream content, CancellationToken ct)
    {
        var relative = PathRules.Normalize(relativePath);
        if (relative == null || PathRules.IsIgnored(relative)) return null;
        Directory.CreateDirectory(TempDir);
        var temp = Path.Combine(TempDir, Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                await content.CopyToAsync(target, ct).ConfigureAwait(false);
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var name = FreeName(relative);
                if (name == null) return null;
                var full = Full(name);
                if (full == null) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.Move(temp, full);
                return name;
            }
            finally { _gate.Release(); }
        }
        finally { TryDelete(temp); }
    }

    private string? FreeName(string relative)
    {
        var full = Full(relative);
        if (full == null) return null;
        if (!File.Exists(full) && !Directory.Exists(full)) return relative;
        var slash = relative.LastIndexOf('/');
        var dir = slash >= 0 ? relative[..(slash + 1)] : "";
        var name = relative[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var ext = dot > 0 ? name[dot..] : "";
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{dir}{stem} ({i}){ext}";
            var candidateFull = Full(candidate);
            if (candidateFull != null && !File.Exists(candidateFull)) return candidate;
        }
        return null;
    }

    // ── Papierkorb ──

    private void MoveToTrash(string full, string relative)
    {
        if (!File.Exists(full)) return;
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var target = Path.Combine(TrashDir, day, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) target += "." + Guid.NewGuid().ToString("N")[..8];
        File.Move(full, target);
    }

    private void PurgeTrash()
    {
        if (DateTime.UtcNow - _lastTrashPurge < TimeSpan.FromHours(1)) return;
        _lastTrashPurge = DateTime.UtcNow;
        try { _partials.Purge(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!Directory.Exists(TrashDir)) return;
        foreach (var day in Directory.GetDirectories(TrashDir))
        {
            if (!DateTime.TryParseExact(Path.GetFileName(day), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var date)) continue;
            if (DateTime.UtcNow - date <= _options.TrashKeep) continue;
            try { Directory.Delete(day, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ── Hilfen ──

    private static string? TryHash(string full)
    {
        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Record(ActivityKind kind, string path, string? from, string? message = null)
    {
        var activity = new Activity(DateTime.UtcNow, kind, path, from, message);
        lock (_activityLock)
        {
            _activity.AddFirst(activity);
            while (_activity.Count > ActivityLimit) _activity.RemoveLast();
        }
        var handler = Recorded;
        if (handler != null) ThreadPool.QueueUserWorkItem(_ => handler(activity));
    }

    // Schließt offene Teildateien (beim Beenden; Tests stellen damit einen Neustart nach).
    public void Dispose() => CloseParked();

    private void Notify()
    {
        var previous = Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        previous.TrySetResult();
    }

    private sealed record StoredState(string NodeId, long Seq, List<FileEntry> Entries, Dictionary<string, long> Cursors);

    private StoredState? Load()
    {
        try
        {
            if (!File.Exists(StateFile)) return null;
            using var stream = File.OpenRead(StateFile);
            return JsonSerializer.Deserialize<StoredState>(stream, Json);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private void Save()
    {
        int files = 0;
        long bytes = 0;
        foreach (var e in _entries.Values) if (!e.Deleted) { files++; bytes += e.Size; }
        _totals = new Totals(files, bytes);
        var state = new StoredState(NodeId, _seq, _entries.Values.ToList(), new Dictionary<string, long>(_cursors));
        AtomicFile.WriteJson(StateFile, state, Json);
    }
}
