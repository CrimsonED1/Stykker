namespace Stykker.Shared.Sampling;

// Was ein Sampler liefern muss: einen Neubeginn nach einer Pause und eine Messung.
public interface ISampler<T>
{
    void Resume();
    T Sample();
}

// Die Messschleife eines Werkzeugs. Sie gehört in den Dienst, nicht in eine Seite – aber sie liest nur, solange jemand
// zusieht: der erste Abruf (Seite oder Fenster) wirft sie an, und wenn 15 Sekunden niemand mehr gefragt hat, hält sie
// wieder an. So kostet eine geschlossene Anwendung nichts, und der Server startet davon unabhängig sofort,
// weil er beim Start nichts messen muss.
//
// Gemessen wird außerhalb des Schlosses, das Abrufe nehmen: ein Abruf bekommt sofort die letzte Messung und wartet nie
// auf eine laufende (eine Messung kann Hunderte Millisekunden dauern, ein Abruf kommt etwa aus einer Blazor-Verbindung).
// Nur der allererste Abruf misst selbst. Es misst immer nur einer zugleich (_sampleGate); ein Takt, der eine laufende
// Messung antrifft, fällt aus. Wirft die erste Messung, bleibt die Schleife aus und der nächste Abruf versucht es neu.
public class ViewerLoop<T> : IDisposable where T : class
{
    private readonly ISampler<T> _sampler;
    private readonly TimeSpan _idleStop;
    private readonly Timer _timer;
    private readonly object _gate = new();         // _current, _lastViewer, _running – nur kurz gehalten
    private readonly object _sampleGate = new();   // eine Messung zur Zeit
    private T? _current;
    private DateTime _lastViewer = DateTime.MinValue;
    private bool _running;
    private bool _disposed;

    // idleStop: wie lange nach dem letzten Abruf noch gemessen wird. Vorgabe: 15 Sekunden.
    public ViewerLoop(ISampler<T> sampler, TimeSpan? idleStop = null)
    {
        _sampler = sampler;
        _idleStop = idleStop ?? TimeSpan.FromSeconds(15);
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    // Eine Messung abholen. Jeder Abruf gilt als „es sieht jemand zu" und hält die Schleife am Laufen.
    public T Snapshot()
    {
        lock (_gate)
        {
            _lastViewer = DateTime.Now;
            if (_running && _current != null) return _current;
        }
        // Anfang (oder Wiederanfang nach einer Pause): selbst messen, mit neuen Grundwerten, damit die erste Zahl stimmt.
        lock (_sampleGate)
        {
            bool resume;
            lock (_gate)
            {
                if (_running && _current != null) return _current;   // ein anderer Abruf war schneller
                resume = !_running;
            }
            if (resume) _sampler.Resume();
            var value = _sampler.Sample();   // wirft sie, bleibt die Schleife aus; der nächste Abruf versucht es neu
            lock (_gate)
            {
                _current = value;
                if (!_running && !_disposed)
                {
                    _running = true;
                    _timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                }
                return value;
            }
        }
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (!_running) return;
            if (DateTime.Now - _lastViewer > _idleStop)
            {
                _running = false;
                _current = null;   // nach der Pause keine alte Zahl zeigen
                if (!_disposed) _timer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }
        }
        // Läuft noch eine Messung (länger als ein Takt), fällt dieser Takt aus.
        if (!Monitor.TryEnter(_sampleGate)) return;
        try
        {
            var value = _sampler.Sample();
            lock (_gate) if (_running) _current = value;
        }
        catch (Exception) { /* eine ausgefallene Messung darf die Schleife nicht anhalten */ }
        finally { Monitor.Exit(_sampleGate); }
    }

    // Hält die Schleife an und gibt den Sampler frei, wenn er etwas hält (Zähler, Treiber).
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;
        }
        _timer.Dispose();
        lock (_sampleGate) (_sampler as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }
}
