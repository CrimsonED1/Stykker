using System.Diagnostics;
using System.Text.Json;

namespace StykkerMeshShare.Core;

// Was gerade übertragen wird und was danach kommt.
// - Warteschlange: die Downloads des laufenden Abrufs. Die Reihenfolge legt man in der Oberfläche fest (SetOrder);
//   sie steht in queue.json und gilt auch nach einem Neustart. Kommt eine andere Datei vor die laufende, wird die
//   laufende angehalten und später an derselben Stelle fortgesetzt.
// - Fortschritt: Bytes, Rate (gleitender Mittelwert) und Restzeit der laufenden Datei.
public sealed record QueueItem(string Path, long Size, string From, long Done);

public sealed record TransferProgress(string Path, string From, long Size, long Done, long Resumed, double BytesPerSecond, bool Checking = false);

public sealed partial class MeshNode
{
    private readonly Lock _queueLock = new();
    private readonly List<QueueItem> _queue = new();          // offene Downloads des laufenden Abrufs, in Abrufreihenfolge
    private List<string> _order = new();                       // vom Nutzer festgelegte Reihenfolge (Pfade)
    private string? _currentPath;
    private CancellationTokenSource? _currentCancel;
    private TransferProgress? _progress;
    private readonly RateMeter _meter = new();
    private PartialStore _partials = null!;

    private string QueueFile => Path.Combine(_options.DataDir, "queue.json");
    private string PartialDir => Path.Combine(_options.DataDir, "partial");

    public TransferProgress? Progress => _progress;
    public string? Transfer => _progress?.Path;
    public double BytesPerSecond => _meter.Rate;

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
    public IReadOnlyList<QueueItem> Paused
    {
        get
        {
            var queued = new HashSet<string>(Queue.Select(q => q.Path), PathRules.Comparer);
            return _partials.All().Where(m => !queued.Contains(m.Path))
                .Select(m => new QueueItem(m.Path, m.Size, "", _partials.BytesOf(m.Hash))).ToList();
        }
    }

    // Legt die Reihenfolge fest: die genannten Pfade zuerst, in dieser Reihenfolge. Steht danach eine andere Datei vor
    // der laufenden, wird die laufende angehalten (ihr Stand bleibt) und die neue erste kommt dran.
    public void SetOrder(IEnumerable<string> paths)
    {
        CancellationTokenSource? interrupt = null;
        lock (_queueLock)
        {
            _order = paths.Select(PathRules.Normalize).OfType<string>().Distinct(PathRules.Comparer).ToList();
            SaveOrder();
            if (_currentPath != null && _queue.Count > 1)
            {
                var first = Ordered(_queue)[0];
                if (!PathRules.Comparer.Equals(first.Path, _currentPath)) interrupt = _currentCancel;
            }
        }
        try { interrupt?.Cancel(); }
        catch (ObjectDisposedException) { }
        Notify();
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

    private void InitTransfers()
    {
        _partials = new PartialStore(PartialDir, _options.TransferChunkSize);
        try { _partials.Purge(); }
        catch (IOException) { }
        try
        {
            if (File.Exists(QueueFile)) _order = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(QueueFile)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { _order = new(); }
    }

    private void SaveOrder()
    {
        try
        {
            var temp = QueueFile + ".new";
            File.WriteAllText(temp, JsonSerializer.Serialize(_order));
            File.Move(temp, QueueFile, overwrite: true);
        }
        catch (IOException) { }
    }

    // Erledigt: aus Warteschlange und Reihenfolge nehmen.
    private void Dequeue(string path)
    {
        lock (_queueLock)
        {
            _queue.RemoveAll(q => PathRules.Comparer.Equals(q.Path, path));
            if (_order.RemoveAll(p => PathRules.Comparer.Equals(p, path)) > 0) SaveOrder();
        }
    }

    private void ReportProgress(string path, string from, long size, long done, long resumed) =>
        _progress = new TransferProgress(path, from, size, done, resumed, _meter.Rate);

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
