namespace StykkerMeshShare.Core;

// Dateien gezielt verteilen und verwalten, von jedem PC (und vom Handy) aus.
//
// Ziel (FileEntry.To): die PCs, die eine Datei haben sollen; null = alle. Wer kein Ziel ist, lädt sie nicht. Hat er sie
// trotzdem (der Absender, oder ein PC, auf dem sie gelöscht wurde, während ein Ziel sie noch nicht hatte), behält er
// sie, bis alle Ziele sie haben, und gibt sie dann frei (Prune). So wird aus „von A nach B“: A legt die Datei mit Ziel B
// ab, B holt sie, A löscht sie danach bei sich.
//
// Wer was hat, erfährt jeder PC aus den Änderungslisten seiner Gegenstellen (NotePresence): ein Eintrag ohne Away mit
// Hash heißt „liegt dort“. Das reicht für die Freigabe und für die Spalte „On“ der Dateiliste.
//
// Alle Änderungen hier sind gewöhnliche neue Fassungen (Versionsvektor + 1) und laufen wie jede andere Änderung durchs
// Mesh. Gelöscht wird endgültig, ohne Papierkorb.
public sealed partial class MeshNode
{
    private readonly Dictionary<string, Dictionary<string, string>> _presence = new(StringComparer.Ordinal);   // Gegenstelle -> Pfad -> Hash
    private readonly Dictionary<string, string> _peerNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (List<string>? To, DateTime At)> _pendingTo = new(PathRules.Comparer);

    private void LoadPresence(Dictionary<string, Dictionary<string, string>>? presence, Dictionary<string, string>? names)
    {
        foreach (var (peer, map) in presence ?? new()) _presence[peer] = new Dictionary<string, string>(map, PathRules.Comparer);
        foreach (var (peer, name) in names ?? new()) _peerNames[peer] = name;
    }

    // Soll diese Datei auf diesem PC liegen?
    private bool WantsHere(FileEntry e) => !e.Deleted && (e.To == null || e.To.Contains(NodeId));

    private bool PeerHas(string peerId, FileEntry e) =>
        e.Hash != null && _presence.TryGetValue(peerId, out var map) && map.TryGetValue(e.Path, out var hash) && hash == e.Hash;

    // Unter der Sperre: merkt sich, was die Gegenstelle laut ihrer Änderungsliste hat. fromStart: die Liste ist
    // vollständig (Abruf ab 0), dann gilt nur noch sie.
    private void NotePresence(ChangePage page, bool fromStart)
    {
        _peerNames[page.NodeId] = page.NodeName;
        if (!_presence.TryGetValue(page.NodeId, out var map) || fromStart)
            _presence[page.NodeId] = map = new Dictionary<string, string>(PathRules.Comparer);
        foreach (var e in page.Entries)
        {
            var path = PathRules.Normalize(e.Path);
            if (path == null) continue;
            if (e.Deleted || e.Away || e.Hash == null) { map.Remove(path); continue; }
            bool had = map.TryGetValue(path, out var before) && before == e.Hash;
            map[path] = e.Hash;
            // Angekommen bei einem Ziel, das diesen PC als Quelle hatte: in der Aktivität zeigen.
            if (!had && _entries.TryGetValue(path, out var mine) && mine is { Deleted: false, Away: false, To: not null }
                && mine.Hash == e.Hash && mine.To.Contains(page.NodeId))
                Record(ActivityKind.Sent, path, page.NodeName);
        }
    }

    // Unter der Sperre: gibt Dateien frei, die nicht hierher gehören und bei allen Zielen angekommen sind.
    private bool Prune()
    {
        bool changed = false;
        foreach (var e in _entries.Values.Where(e => e is { Deleted: false, Away: false, To: not null } && !e.To.Contains(NodeId)).ToList())
        {
            if (!e.To!.All(id => PeerHas(id, e))) continue;   // noch nicht überall angekommen: hier behalten
            var full = Full(e.Path);
            if (full == null || !DiskMatches(e, full)) continue;   // hier geändert: der nächste Scan nimmt es auf
            try { RemoveFile(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }   // offen: später
            _entries[e.Path] = e with { Away = true, Seq = ++_seq };
            Record(ActivityKind.Removed, e.Path, null, "Arrived at every target; removed here");
            changed = true;
        }
        return changed;
    }

    // ── Für die Oberfläche ──

    public sealed record Node(string Id, string Name, bool Self);

    // Dieser PC und alle Gegenstellen, die er je abgefragt hat (Name wie zuletzt gemeldet).
    public IReadOnlyList<Node> KnownNodes
    {
        get
        {
            _gate.Wait();
            try { return KnownNodesCore(); }
            finally { _gate.Release(); }
        }
    }

    private List<Node> KnownNodesCore() =>
        [new Node(NodeId, NodeName, true), .. _peerNames.Where(p => p.Key != NodeId).OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase).Select(p => new Node(p.Key, p.Value, false))];

    public sealed record FileView(FileEntry Entry, IReadOnlyList<string> Holders);

    // Die Dateiliste mit den PCs, auf denen jede Datei liegt (soweit dieser PC es weiß), in einem Zug unter der Sperre.
    public (IReadOnlyList<Node> Nodes, IReadOnlyList<FileView> Files) Overview()
    {
        _gate.Wait();
        try
        {
            var nodes = KnownNodesCore();
            var files = _entries.Values.Where(e => !e.Deleted).OrderBy(e => e.Path, PathRules.Comparer).Select(e =>
            {
                var holders = new List<string>(nodes.Count);
                if (!e.Away) holders.Add(NodeId);
                foreach (var n in nodes) if (!n.Self && PeerHas(n.Id, e)) holders.Add(n.Id);
                return new FileView(e, holders);
            }).ToList();
            return (nodes, files);
        }
        finally { _gate.Release(); }
    }

    // ── Senden ──

    // Beim Hochladen (unter der Sperre): die neue Datei unter diesem Pfad geht nur an diese PCs. keepHere: dieser PC
    // behält sie, sonst gibt er sie frei, sobald alle Ziele sie haben.
    // Unter der Sperre. Ein Pfad, der im Mesh schon eine Datei hat (auch nur auf anderen PCs), ist nicht frei.
    private bool KnownElsewhere(string relative) => _entries.TryGetValue(relative, out var e) && !e.Deleted;

    private void SetPendingTo(string relative, IReadOnlyCollection<string> to, bool keepHere)
    {
        List<string>? list = to.Count == 0 ? null : to.Distinct(StringComparer.Ordinal).ToList();
        if (list != null)
        {
            if (keepHere && !list.Contains(NodeId)) list.Add(NodeId);
            if (!keepHere) list.Remove(NodeId);
            if (list.Count == 0) list = null;
        }
        foreach (var old in _pendingTo.Where(p => DateTime.UtcNow - p.Value.At > TimeSpan.FromHours(6)).Select(p => p.Key).ToList()) _pendingTo.Remove(old);
        _pendingTo[relative] = (list, DateTime.UtcNow);
    }

    // Unter der Sperre (Scan): das Ziel einer eben hochgeladenen Datei, falls eins angegeben war.
    private bool TryTakePendingTo(string relative, out List<string>? to)
    {
        if (_pendingTo.Remove(relative, out var pending)) { to = pending.To; return true; }
        to = null;
        return false;
    }

    // Vorhandene Dateien zusätzlich an diese PCs schicken. removeHere: dieser PC gibt sie danach frei.
    public string? Send(IReadOnlyCollection<string> paths, IReadOnlyCollection<string> to, bool removeHere)
    {
        if (to.Count == 0 && !removeHere) return "Choose at least one PC.";
        return Change(paths, (e, all) =>
        {
            var target = new HashSet<string>(e.To ?? all, StringComparer.Ordinal);
            target.UnionWith(to);
            if (removeHere) target.Remove(NodeId);
            return target;
        }, removeNow: false);
    }

    // Löscht die Dateien auf diesen PCs. Bleibt kein PC übrig, ist die Datei überall weg. Hier wird sofort gelöscht,
    // auf den anderen PCs, sobald sie die Änderung holen. Endgültig, kein Papierkorb.
    public string? DeleteOn(IReadOnlyCollection<string> paths, IReadOnlyCollection<string> on)
    {
        if (on.Count == 0) return "Choose at least one PC.";
        return Change(paths, (e, all) =>
        {
            var target = new HashSet<string>(e.To ?? all, StringComparer.Ordinal);
            target.ExceptWith(on);
            // Liegt die Datei auf keinem übrig gebliebenen Ziel und auch hier nicht mehr, wäre sie verloren: dann ganz weg.
            return target;
        }, removeNow: on.Contains(NodeId));
    }

    // Gemeinsamer Weg für Senden und Löschen: neues Ziel je Datei. Leeres Ziel = überall löschen.
    private string? Change(IReadOnlyCollection<string> paths, Func<FileEntry, IEnumerable<string>, HashSet<string>> newTarget, bool removeNow)
    {
        string? problem = null;
        bool changed = false;
        _gate.Wait();
        try
        {
            var all = KnownNodesCore().Select(n => n.Id).ToList();
            foreach (var path in paths)
            {
                var relative = PathRules.Normalize(path);
                if (relative == null || !_entries.TryGetValue(relative, out var e) || e.Deleted) { problem ??= $"{path} is not in the shared folder any more."; continue; }
                var target = newTarget(e, all);
                var full = Full(relative);
                try
                {
                    if (target.Count == 0)
                    {
                        if (!e.Away && full != null) RemoveFile(full);
                        _entries[relative] = Tombstone(e, VersionVector.Bump(e.Version, NodeId), NodeId, NodeName);
                        Record(ActivityKind.Deleted, relative, null);
                        changed = true;
                        continue;
                    }
                    // An alle, die es gibt: wieder der Spiegel (null), damit auch später dazukommende PCs sie bekommen.
                    List<string>? to = target.SetEquals(all) ? null : target.OrderBy(x => x, StringComparer.Ordinal).ToList();
                    bool same = to == null ? e.To == null : e.To != null && to.SequenceEqual(e.To.OrderBy(x => x, StringComparer.Ordinal));
                    bool dropHere = removeNow && !e.Away && !target.Contains(NodeId);
                    if (same && !dropHere) continue;
                    if (dropHere && full != null) RemoveFile(full);
                    _entries[relative] = e with
                    {
                        To = to,
                        Away = e.Away || dropHere,
                        Version = same ? e.Version : VersionVector.Bump(e.Version, NodeId),
                        Seq = ++_seq,
                    };
                    if (dropHere) Record(ActivityKind.Deleted, relative, null, "Deleted on this PC");
                    changed = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problem ??= $"{relative}: {ex.Message}";
                }
            }
            if (changed) { Prune(); Save(); }
        }
        finally { _gate.Release(); }
        if (changed) Notify();
        return problem;
    }

    // ── Umbenennen und verschieben ──

    // Verschiebt Dateien im Ordner (auch umbenennen). Liegt eine Datei hier, wird sie hier verschoben; die anderen PCs
    // verschieben ihre Kopie, sobald sie die Änderung holen (siehe ApplyMoves), statt sie neu zu laden.
    public string? Move(IReadOnlyCollection<(string From, string To)> moves)
    {
        string? problem = null;
        bool changed = false;
        _gate.Wait();
        try
        {
            foreach (var (fromPath, toPath) in moves)
            {
                var from = PathRules.Normalize(fromPath);
                var to = PathRules.Normalize(toPath);
                if (from == null || !_entries.TryGetValue(from, out var e) || e.Deleted) { problem ??= $"{fromPath} is not in the shared folder any more."; continue; }
                if (to == null || PathRules.IsIgnored(to)) { problem ??= $"\"{toPath}\" is not allowed as a name."; continue; }
                if (PathRules.Comparer.Equals(from, to)) continue;
                if (_entries.TryGetValue(to, out var there) && !there.Deleted) { problem ??= $"{to} already exists."; continue; }
                try
                {
                    if (!e.Away)
                    {
                        var src = Full(from);
                        var dst = Full(to);
                        if (src == null || dst == null) { problem ??= $"{to} is not allowed."; continue; }
                        if (File.Exists(dst)) { problem ??= $"{to} already exists."; continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        File.Move(src, dst);
                        RemoveEmptyFolders(Path.GetDirectoryName(src));
                    }
                    _entries[to] = e with
                    {
                        Path = to,
                        Version = VersionVector.Bump(there?.Version ?? new(), NodeId),
                        Seq = ++_seq,
                        OriginId = NodeId,
                        OriginName = NodeName,
                    };
                    _entries[from] = Tombstone(e, VersionVector.Bump(e.Version, NodeId), NodeId, NodeName);
                    Record(ActivityKind.Moved, to, null, $"from {from}");
                    changed = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problem ??= $"{from}: {ex.Message}";
                }
            }
            if (changed) Save();
        }
        finally { _gate.Release(); }
        if (changed) Notify();
        return problem;
    }

    // Unter der Sperre (Abruf): eine Gegenstelle hat Dateien verschoben – neuer Pfad mit demselben Inhalt, alter Pfad
    // gelöscht. Liegt die Datei hier noch unter dem alten Pfad, wird sie hier verschoben statt neu geladen und kopiert.
    // Gibt die erledigten Einträge zurück (die neuen Pfade und die Grabsteine).
    private HashSet<FileEntry> ApplyMoves(IReadOnlyList<FileEntry> entries, IReadOnlyList<FileEntry> downloads)
    {
        var done = new HashSet<FileEntry>();
        var tombs = entries.Where(e => e.Deleted).ToDictionary(e => e.Path, PathRules.Comparer);
        if (tombs.Count == 0) return done;
        foreach (var d in downloads)
        {
            if (_entries.TryGetValue(d.Path, out var atTarget) && !atTarget.Deleted) continue;
            var old = _entries.Values.FirstOrDefault(e => e is { Deleted: false, Away: false } && e.Hash == d.Hash && tombs.ContainsKey(e.Path));
            if (old == null) continue;
            var tomb = tombs[old.Path];
            if (VersionVector.Compare(old.Version, tomb.Version) != Order.RemoteNewer) continue;
            var src = Full(old.Path);
            var dst = Full(d.Path);
            if (src == null || dst == null || File.Exists(dst) || !DiskMatches(old, src)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Move(src, dst);
                RemoveEmptyFolders(Path.GetDirectoryName(src));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            var info = new FileInfo(dst);
            _entries[d.Path] = d with { Away = false, Size = info.Length, MtimeTicks = info.LastWriteTimeUtc.Ticks, Seq = ++_seq };
            _entries[old.Path] = Tombstone(old, tomb.Version, tomb.OriginId, tomb.OriginName);
            Record(ActivityKind.Moved, d.Path, tomb.OriginName, $"from {old.Path}");
            done.Add(d);
            done.Add(tomb);
            tombs.Remove(old.Path);
        }
        return done;
    }
}
