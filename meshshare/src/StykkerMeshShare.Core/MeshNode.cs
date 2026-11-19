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
public sealed partial class MeshNode
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

    public string NodeId { get; }
    public string NodeName => _options.NodeName;
    public string Folder => _folder;
    public event Action<string>? FolderChanged;
    public string DataDir => _options.DataDir;

    public DateTime? LastScan { get; private set; }
    public string? FolderProblem { get; private set; }
    public int HeldDeletes { get; private set; }
    public bool PendingRescan { get; private set; }      // eine Datei war noch in Arbeit: der nächste Scan holt sie

    public event Action? Changed;

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
    private string TempDir => Path.Combine(_options.DataDir, "tmp");
    public string TrashDir => Path.Combine(_options.DataDir, "trash");

    public long Seq { get { _gate.Wait(); try { return _seq; } finally { _gate.Release(); } } }

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
        _gate.Wait();
        try { ScanLocked(); }
        finally { _gate.Release(); }
        Notify();
    }

    // Der Nutzer hat bestätigt, dass die angehaltenen Löschungen gewollt sind.
    public void AllowHeldDeletes()
    {
        _allowMassDelete = true;
        Scan();
    }

    private void ScanLocked()
    {
        if (!Directory.Exists(_folder))
        {
            FolderProblem = "The shared folder does not exist.";
            return;
        }
        FolderProblem = null;
        PendingRescan = false;
        bool changed = false;
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(PathRules.Comparer);
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Verknüpfungen und Junctions: nicht folgen, sonst landet ein fremder Ordner im Mesh.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var full in Directory.EnumerateFiles(_folder, "*", enumeration))
        {
            var relative = PathRules.Normalize(PathRules.ToRelative(_folder, full));
            if (relative == null || PathRules.IsIgnored(relative)) continue;
            seen.Add(relative);
            FileInfo info;
            try { info = new FileInfo(full); _ = info.Length; }
            catch (IOException) { continue; }
            var mtime = info.LastWriteTimeUtc;
            if (now - mtime < _options.StableDelay) { PendingRescan = true; continue; }

            _entries.TryGetValue(relative, out var entry);
            if (entry is { Deleted: false } && entry.Size == info.Length && entry.MtimeTicks == mtime.Ticks) continue;

            var hash = TryHash(full);
            if (hash == null) { PendingRescan = true; continue; }   // gesperrt: später noch einmal

            if (entry is { Deleted: false } && entry.Hash == hash)
            {
                _entries[relative] = entry with { Size = info.Length, MtimeTicks = mtime.Ticks };
                changed = true;
                continue;
            }
            _entries[relative] = new FileEntry
            {
                Path = relative,
                Size = info.Length,
                MtimeTicks = mtime.Ticks,
                Hash = hash,
                Version = VersionVector.Bump(entry?.Version ?? new(), NodeId),
                Seq = ++_seq,
                OriginId = NodeId,
                OriginName = NodeName,
            };
            Record(entry is { Deleted: false } ? ActivityKind.Changed : ActivityKind.Added, relative, null);
            changed = true;
        }

        var gone = _entries.Values.Where(e => !e.Deleted && !seen.Contains(e.Path)).ToList();
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
        PurgeTrash();
        if (changed) Save();
    }

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
            var full = PathRules.ToFull(_folder, relative);
            if (full == null || !File.Exists(full)) return null;
            // Hat sich die Datei seit dem Scan geändert, wird sie nicht ausgeliefert; der nächste Scan nimmt sie auf.
            if (!DiskMatches(entry, full)) { PendingRescan = true; return null; }
            try { return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (IOException) { return null; }
        }
        finally { _gate.Release(); }
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
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { since = _cursors.GetValueOrDefault(probe.NodeId); }
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
            .Where(x => x.Path != null && !PathRules.IsIgnored(x.Path))
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
                else if (outcome == Outcome.Retry) complete = false;
            }

            var byPath = downloads.ToDictionary(d => d.Path, PathRules.Comparer);
            var left = new HashSet<string>(byPath.Keys, PathRules.Comparer);
            while (left.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
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
            if (complete) _cursors[page.NodeId] = page.Seq;
            Save();
        }
        finally { _gate.Release(); }
        if (applied > 0) Notify();
        return new PullResult(applied, complete);
    }

    private enum Outcome { Nothing, Applied, Retry, Deferred }

    // Grobe Vorauswahl (unter der Sperre): ein Eintrag mit Inhalt, der hier unter diesem Pfad noch fehlt und nicht
    // älter ist als der eigene. Was wirklich passiert, entscheidet Decide() erst direkt vor dem Laden.
    private bool NeedsDownload(FileEntry remote)
    {
        if (remote.Deleted || remote.Hash == null) return false;
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
        finally { _gate.Release(); }

        if (plan.Kind == PlanKind.Done) return plan.Changed ? Outcome.Applied : Outcome.Nothing;
        if (plan.Kind == PlanKind.Retry) return Outcome.Retry;

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
                    long kept = stopped.Done / _partials.ChunkSize * _partials.ChunkSize;
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
        var full = PathRules.ToFull(_folder, remote.Path);
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
                    var loserName = PathRules.ConflictName(remote.Path, local.OriginName, local.MtimeTicks);
                    return new Plan(PlanKind.Download, Target: remote.Path, Version: merged, ExpectedAtTarget: local,
                        LoserToMove: local, LoserTarget: loserName);
                }
                var copyName = PathRules.ConflictName(remote.Path, remote.OriginName, remote.MtimeTicks);
                if (_entries.TryGetValue(copyName, out var existingCopy) && !existingCopy.Deleted && existingCopy.Hash == remote.Hash)
                {
                    _entries[remote.Path] = local with { Version = merged, Seq = ++_seq };
                    return new Plan(PlanKind.Done);
                }
                var copyFull = PathRules.ToFull(_folder, copyName);
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

    // Lädt den Inhalt in seine Teildatei und setzt dort fort, wo die letzte Übertragung aufgehört hat (auch nach einem
    // Neustart oder von einer anderen Gegenstelle mit demselben Inhalt). Stimmt am Ende der SHA-256 der ganzen Datei
    // nicht, wird die Teildatei verworfen und die Datei beim nächsten Abruf neu geladen.
    private async Task<LoadResult> LoadAsync(FileEntry remote, string peerName, IPeerSource peer, CancellationToken ct)
    {
        if (remote.Hash == null) return new(LoadStatus.Failed);
        string? localCopy = null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var same = _entries.Values.FirstOrDefault(e => !e.Deleted && e.Hash == remote.Hash);
            if (same != null)
            {
                var full = PathRules.ToFull(_folder, same.Path);
                if (full != null && DiskMatches(same, full)) localCopy = full;
            }
        }
        finally { _gate.Release(); }

        // Eine angefangene Teildatei wird erst ganz gelesen und geprüft; bei großen Dateien dauert das einen Moment.
        long already = _partials.BytesOf(remote.Hash);
        if (already > 0) _progress = new TransferProgress(remote.Path, peerName, remote.Size, already, already, 0, Checking: true);
        PartialStore.Handle handle;
        // Ohne ct: die Prüfung läuft zu Ende, sonst bliebe die Teildatei offen. Abgebrochen wird gleich danach.
        try { handle = await Task.Run(() => _partials.Open(remote.Path, remote.Hash, remote.Size)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(LoadStatus.Failed); }

        Stream? source = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            long done = handle.Stream.Position;
            ReportProgress(remote.Path, localCopy != null ? NodeName : peerName, remote.Size, done, handle.Resumed);
            bool tooLong = false;
            if (done < remote.Size)
            {
                if (localCopy != null)
                {
                    try { source = new FileStream(localCopy, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete) { Position = done }; }
                    catch (IOException) { source = null; }
                }
                source ??= await peer.OpenFileAsync(remote.Path, remote.Hash, done, ct).ConfigureAwait(false);
                if (source == null) return new(LoadStatus.Failed);

                var buffer = new byte[256 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if (done + read > remote.Size) { tooLong = true; break; }
                    await _partials.WriteAsync(handle, buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    _meter.Add(read);
                    ReportProgress(remote.Path, localCopy != null ? NodeName : peerName, remote.Size, done, handle.Resumed);
                }
            }
            await handle.Stream.FlushAsync(ct).ConfigureAwait(false);

            // Zu kurz (Verbindung weg): Teildatei behalten, beim nächsten Mal weiter.
            if (!tooLong && done < remote.Size) return new(LoadStatus.Failed);

            var hash = Convert.ToHexStringLower(handle.FileHash.GetHashAndReset());
            if (!tooLong && hash == remote.Hash)
            {
                handle.Dispose();
                return new(LoadStatus.Loaded, handle.DataPath);
            }
            // Inhalt stimmt nicht (die Datei hat sich dort während der Übertragung geändert, oder Daten sind
            // unterwegs kaputtgegangen): verwerfen und neu laden.
            handle.Dispose();
            _partials.Discard(remote.Hash);
            Record(ActivityKind.Error, remote.Path, "Check failed (SHA-256); loading again");
            return new(LoadStatus.Failed);
        }
        catch (IOException) { return new(LoadStatus.Failed); }
        catch (HttpRequestException) { return new(LoadStatus.Failed); }
        finally
        {
            handle.Dispose();
            if (source != null) await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool Commit(Plan plan, FileEntry remote, string temp)
    {
        var target = plan.Target!;
        var full = PathRules.ToFull(_folder, target);
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
                var loserFull = PathRules.ToFull(_folder, plan.LoserTarget);
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
            Record(ActivityKind.Error, target, ex.Message);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Record(ActivityKind.Error, target, ex.Message);
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
        string target;
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
            var old = _folder;
            _folder = target;
            HeldDeletes = 0;
            _allowMassDelete = false;
            Record(ActivityKind.Changed, move ? $"Folder moved from {old}" : "Folder changed: fetching the files from the other PCs", null);
            Save();
        }
        finally { _gate.Release(); }
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
                var full = PathRules.ToFull(_folder, name)!;
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
        var full = PathRules.ToFull(_folder, relative);
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
            var candidateFull = PathRules.ToFull(_folder, candidate);
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

    private void Record(ActivityKind kind, string path, string? from)
    {
        lock (_activityLock)
        {
            _activity.AddFirst(new Activity(DateTime.UtcNow, kind, path, from));
            while (_activity.Count > ActivityLimit) _activity.RemoveLast();
        }
    }

    private void Notify()
    {
        var previous = Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        previous.TrySetResult();
        Changed?.Invoke();
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
        var state = new StoredState(NodeId, _seq, _entries.Values.ToList(), new Dictionary<string, long>(_cursors));
        var temp = StateFile + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, StateFile, overwrite: true);
    }
}
