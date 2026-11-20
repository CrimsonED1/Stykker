namespace StykkerMeshShare.Core;

// Der Abgleich mit einer Gegenstelle: was holen, was entscheiden, laden, übernehmen. Siehe MeshNode.cs.
public sealed partial class MeshNode
{
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
            // Pausiert: die Liste bleibt sichtbar (und lässt sich umsortieren); der nächste Abruf baut sie neu.
            lock (_queueLock) if (!TransfersPaused) _queue.Clear();
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
            SaveSoon();   // viele kleine Dateien: nicht nach jeder den ganzen Index schreiben
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
}
