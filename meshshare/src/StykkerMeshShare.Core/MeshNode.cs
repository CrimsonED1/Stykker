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

        // In Portionen (etwa 1 GB oder 500 Dateien): so sehen die anderen PCs die ersten Dateien schon, während der
        // Rest eines großen Ordners noch gehasht wird.
        bool changed = false;
        int at = 0;
        while (at < toHash.Count)
        {
            var hashed = new List<(SeenFile File, FileEntry? Entry, string Hash)>();
            long batchBytes = 0;
            for (; at < toHash.Count && hashed.Count < 500 && batchBytes < (1L << 30); at++)
            {
                var (file, entry) = toHash[at];
                var hash = TryHash(file.Full);
                if (hash == null) { pending = true; continue; }   // gesperrt: später noch einmal
                hashed.Add((file, entry, hash));
                batchBytes += file.Size;
            }
            if (hashed.Count == 0) continue;
            _gate.Wait();
            try
            {
                if (!PathRules.Comparer.Equals(folder, _folder)) return;
                bool batchChanged = false;
                foreach (var (file, before, hash) in hashed)
                {
                    _entries.TryGetValue(file.Relative, out var entry);
                    // Inzwischen hat der Abgleich den Eintrag geändert, oder die Datei hat sich noch einmal geändert.
                    if (!ReferenceEquals(entry, before) || !SameOnDisk(file)) { pending = true; continue; }
                    if (entry is { Deleted: false } && entry.Hash == hash)
                    {
                        _entries[file.Relative] = entry with { Size = file.Size, MtimeTicks = file.MtimeTicks };
                        batchChanged = true;
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
                    batchChanged = true;
                }
                if (batchChanged && at < toHash.Count) { Save(); Notify(); }
                changed |= batchChanged;
            }
            finally { _gate.Release(); }
        }

        _gate.Wait();
        try
        {
            if (!PathRules.Comparer.Equals(folder, _folder)) return;

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
    public void Dispose()
    {
        CloseParked();
        _gate.Wait();
        try { Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { _gate.Release(); }
    }

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

    // Höchstens alle 2 Sekunden; was dazwischen liegt, schreibt der nächste Save() (spätestens am Ende des Abrufs).
    // Geht dabei etwas verloren (Absturz), findet der nächste Scan die Dateien wieder und gleicht sie über den Hash ab.
    private long _savedAt;
    private void SaveSoon()
    {
        if (Environment.TickCount64 - _savedAt < 2000) { RefreshTotals(); return; }
        Save();
    }

    private void RefreshTotals()
    {
        int files = 0;
        long bytes = 0;
        foreach (var e in _entries.Values) if (!e.Deleted) { files++; bytes += e.Size; }
        _totals = new Totals(files, bytes);
    }

    private void Save()
    {
        _savedAt = Environment.TickCount64;
        RefreshTotals();
        var state = new StoredState(NodeId, _seq, _entries.Values.ToList(), new Dictionary<string, long>(_cursors));
        AtomicFile.WriteJson(StateFile, state, Json);
    }
}
