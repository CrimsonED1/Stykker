using System.Diagnostics;
using System.Text.Json;

namespace StykkerMeshShare.Core;

// Was gerade übertragen wird und was danach kommt.
// - Warteschlange: die Downloads des laufenden Abrufs. Die Reihenfolge legt man in der Oberfläche fest (SetOrder);
//   sie steht in queue.json und gilt auch nach einem Neustart. Kommt eine andere Datei vor die laufende, wird die
//   laufende angehalten und später an derselben Stelle fortgesetzt.
// - Alles anhalten (SetPaused) und „nicht laden“ (Skip) stehen ebenfalls in queue.json.
// - Fortschritt: Bytes, Rate (gleitender Mittelwert) und Restzeit der laufenden Datei.
// - Teildateien liegen im geteilten Ordner unter .stykker\partial (wird nie abgeglichen). So liegen sie auf demselben
//   Laufwerk wie das Ziel, und das Übernehmen ist ein Umbenennen statt einer Kopie.
public sealed record QueueItem(string Path, long Size, string From, long Done);

public sealed record TransferProgress(string Path, string From, long Size, long Done, long Resumed, double BytesPerSecond, bool Checking = false);

public sealed partial class MeshNode
{
    private const int MaxParked = 3;

    private readonly Lock _queueLock = new();
    private readonly List<QueueItem> _queue = new();          // offene Downloads des laufenden Abrufs, in Abrufreihenfolge
    private List<string> _order = new();                       // vom Nutzer festgelegte Reihenfolge (Pfade)
    private HashSet<string> _skip = new(PathRules.Comparer);   // „nicht laden“
    private bool _paused;                                      // alle Downloads angehalten
    private readonly Dictionary<string, DateTime> _hashHold = new(StringComparer.Ordinal);
    private string? _currentPath;
    private CancellationTokenSource? _currentCancel;
    private TransferProgress? _progress;
    private long _progressAt;
    private readonly RateMeter _meter = new();
    private PartialStore _partials = null!;
    // Angehaltene Downloads behalten ihre offene Teildatei und den Hash-Stand: beim Weitermachen muss nichts neu
    // gelesen werden. Nur die letzten paar; die übrigen werden geschlossen (dann wird beim Fortsetzen neu geprüft).
    private readonly List<(string Hash, PartialStore.Handle Handle)> _parked = new();

    private string QueueFile => Path.Combine(_options.DataDir, "queue.json");
    private static string WorkDir(string folder) => Path.Combine(folder, ".stykker");
    private string PartialDir => Path.Combine(WorkDir(_folder), "partial");
    private string TempDir => Path.Combine(WorkDir(_folder), "tmp");

    public TransferProgress? Progress => _progress;
    // Der Abgleich soll sofort wieder laufen (nach „Resume“ oder „Load“): die Gegenstellen warten sonst bis zu 25 s.
    public event Action? WakeRequested;
    public bool TransfersPaused => _paused;
    public IReadOnlyList<string> Skipped { get { lock (_queueLock) return _skip.Order(PathRules.Comparer).ToList(); } }

    // Die Warteschlange in der Reihenfolge, in der sie abgearbeitet wird (die laufende Datei zuerst).
    public IReadOnlyList<QueueItem> Queue
    {
        get
        {
            lock (_queueLock)
            {
                var ordered = Ordered(_queue);
                if (_currentPath != null)
                {
                    var current = ordered.FindIndex(q => PathRules.Comparer.Equals(q.Path, _currentPath));
                    if (current > 0) { var item = ordered[current]; ordered.RemoveAt(current); ordered.Insert(0, item); }
                }
                return ordered.Select(q => q with { Done = PathRules.Comparer.Equals(q.Path, _currentPath) ? _progress?.Done ?? q.Done : q.Done }).ToList();
            }
        }
    }

    // Angefangene Downloads, die gerade nicht in der Warteschlange stehen (zum Beispiel direkt nach dem Start).
    public IReadOnlyList<QueueItem> PausedFiles(IReadOnlyList<QueueItem> queue)
    {
        var queued = new HashSet<string>(queue.Select(q => q.Path), PathRules.Comparer);
        return _partials.All().Where(m => !queued.Contains(m.Path))
            .Select(m => new QueueItem(m.Path, m.Size, "", m.Bytes)).ToList();
    }

    // Legt die Reihenfolge fest: die genannten Pfade zuerst, in dieser Reihenfolge. Steht danach eine andere Datei vor
    // der laufenden, wird die laufende angehalten (ihr Stand bleibt) und die neue erste kommt dran.
    public void SetOrder(IEnumerable<string> paths)
    {
        CancellationTokenSource? interrupt = null;
        lock (_queueLock)
        {
            _order = paths.Take(10_000).Select(PathRules.Normalize).OfType<string>().Distinct(PathRules.Comparer).ToList();
            SaveQueueState();
            if (_currentPath != null && _queue.Count > 1)
            {
                var first = Ordered(_queue)[0];
                if (!PathRules.Comparer.Equals(first.Path, _currentPath)) interrupt = _currentCancel;
            }
        }
        Cancel(interrupt);
        Notify();
    }

    // Alle Downloads anhalten oder wieder freigeben. Löschungen und schon vorhandene Inhalte gleichen weiter ab.
    public void SetPaused(bool paused)
    {
        CancellationTokenSource? interrupt;
        lock (_queueLock)
        {
            _paused = paused;
            SaveQueueState();
            interrupt = paused ? _currentCancel : null;
        }
        Cancel(interrupt);
        Notify();
        if (!paused) WakeRequested?.Invoke();
    }

    // „Nicht laden“: die Datei wird übergangen, bis sie wieder freigegeben wird. Freigeben liest alle Gegenstellen von
    // vorn, denn ihr Eintrag liegt dort schon hinter dem gemerkten Zeiger.
    public void Skip(string path, bool skip)
    {
        var relative = PathRules.Normalize(path);
        if (relative == null) return;
        CancellationTokenSource? interrupt = null;
        lock (_queueLock)
        {
            if (skip)
            {
                if (!_skip.Add(relative)) return;
                _queue.RemoveAll(q => PathRules.Comparer.Equals(q.Path, relative));
                if (PathRules.Comparer.Equals(_currentPath, relative)) interrupt = _currentCancel;
            }
            else if (!_skip.Remove(relative)) return;
            SaveQueueState();
        }
        Cancel(interrupt);
        if (!skip)
        {
            _gate.Wait();
            try { _cursors.Clear(); Save(); }
            finally { _gate.Release(); }
            WakeRequested?.Invoke();
        }
        Notify();
    }

    private bool IsSkipped(string path) { lock (_queueLock) return _skip.Contains(path); }

    private static void Cancel(CancellationTokenSource? source)
    {
        try { source?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    // Ein Inhalt, der eben kaputt ankam, wird eine Weile nicht neu geladen.
    private void HoldHash(string hash) { lock (_queueLock) _hashHold[hash] = DateTime.UtcNow + _options.BadHashPause; }

    private bool HashOnHold(string hash)
    {
        lock (_queueLock)
        {
            if (!_hashHold.TryGetValue(hash, out var until)) return false;
            if (DateTime.UtcNow < until) return true;
            _hashHold.Remove(hash);
            return false;
        }
    }

    private List<QueueItem> Ordered(IEnumerable<QueueItem> items)
    {
        var rank = new Dictionary<string, int>(PathRules.Comparer);
        for (int i = 0; i < _order.Count; i++) rank.TryAdd(_order[i], i);
        return items.Select((item, index) => (item, index))
            .OrderBy(x => rank.TryGetValue(x.item.Path, out var r) ? r : int.MaxValue)
            .ThenBy(x => x.index)
            .Select(x => x.item).ToList();
    }

    private sealed record QueueState(List<string>? Order, List<string>? Skip, bool Paused);

    private void InitTransfers()
    {
        OpenPartialStore();
        try
        {
            if (File.Exists(QueueFile))
            {
                var text = File.ReadAllText(QueueFile);
                if (text.TrimStart().StartsWith('['))   // frühere Fassung: nur die Reihenfolge
                    _order = JsonSerializer.Deserialize<List<string>>(text) ?? new();
                else if (JsonSerializer.Deserialize<QueueState>(text) is { } state)
                {
                    _order = state.Order ?? new();
                    _skip = new HashSet<string>(state.Skip ?? new(), PathRules.Comparer);
                    _paused = state.Paused;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { _order = new(); }
        // Teildateien der früheren Fassung lagen im Datenordner.
        try { Directory.Delete(Path.Combine(_options.DataDir, "partial"), recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void OpenPartialStore()
    {
        _partials = new PartialStore(PartialDir, _options.TransferChunkSize);
        try
        {
            var work = Directory.CreateDirectory(WorkDir(_folder));
            work.Attributes |= FileAttributes.Hidden;
            _partials.Purge();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Vor einem Ordnerwechsel: offene Teildateien schließen (sonst lassen sie sich nicht verschieben).
    private void CloseParked()
    {
        lock (_queueLock)
        {
            foreach (var (_, handle) in _parked) handle.Dispose();
            _parked.Clear();
        }
    }

    private PartialStore.Handle? TakeParked(string hash)
    {
        lock (_queueLock)
        {
            var at = _parked.FindIndex(p => p.Hash == hash);
            if (at < 0) return null;
            var handle = _parked[at].Handle;
            _parked.RemoveAt(at);
            return handle;
        }
    }

    private void Park(string hash, PartialStore.Handle handle)
    {
        lock (_queueLock)
        {
            _parked.Add((hash, handle));
            while (_parked.Count > MaxParked)
            {
                _parked[0].Handle.Dispose();
                _parked.RemoveAt(0);
            }
        }
    }

    private void SaveQueueState()
    {
        try { AtomicFile.WriteJson(QueueFile, new QueueState(_order, _skip.ToList(), _paused)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Erledigt: aus Warteschlange und Reihenfolge nehmen.
    private void Dequeue(string path)
    {
        lock (_queueLock)
        {
            _queue.RemoveAll(q => PathRules.Comparer.Equals(q.Path, path));
            if (_order.RemoveAll(p => PathRules.Comparer.Equals(p, path)) > 0) SaveQueueState();
        }
    }

    // Höchstens zehnmal je Sekunde ein neuer Stand (die Seite fragt ohnehin nur alle 1,5 s).
    private void ReportProgress(string path, string from, long size, long done, long resumed, bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - _progressAt < 100 && done < size) return;
        _progressAt = now;
        _progress = new TransferProgress(path, from, size, done, resumed, _meter.Rate);
    }

    // Rate über die letzten Sekunden: alle 250 ms ein Messpunkt, gleitender Mittelwert. Steht die Übertragung,
    // fällt die Rate nach ein paar Sekunden auf 0.
    private sealed class RateMeter
    {
        private readonly Lock _lock = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _bytes;
        private double _lastTick;
        private double _rate;

        public void Add(long bytes)
        {
            lock (_lock)
            {
                _bytes += bytes;
                Tick();
            }
        }

        public double Rate
        {
            get { lock (_lock) { Tick(); return _rate; } }
        }

        private void Tick()
        {
            double now = _clock.Elapsed.TotalSeconds;
            double span = now - _lastTick;
            if (span < 0.25) return;
            double sample = _bytes / span;
            double weight = Math.Min(1, span / 2.0);   // etwa zwei Sekunden Gedächtnis
            _rate = _rate == 0 ? sample : _rate + (sample - _rate) * weight;
            if (span > 5) _rate = sample;
            if (_rate < 1) _rate = 0;
            _bytes = 0;
            _lastTick = now;
        }
    }
}
