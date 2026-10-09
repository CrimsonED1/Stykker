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
public class ViewerLoop<T> : IDisposable where T : class
{
    private readonly ISampler<T> _sampler;
    private readonly TimeSpan _idleStop;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private T? _current;
    private DateTime _lastViewer = DateTime.MinValue;
    private bool _running;

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
            if (_running) return _current ??= _sampler.Sample();

            // Anfang (oder Wiederanfang nach einer Pause): Grundwerte neu setzen, damit die erste Zahl stimmt.
            _running = true;
            _sampler.Resume();
            _current = _sampler.Sample();
            _timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            return _current;
        }
    }

    private void Tick()
    {
        try
        {
            lock (_gate)
            {
                if (DateTime.Now - _lastViewer > _idleStop)
                {
                    _running = false;
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                    return;
                }
                _current = _sampler.Sample();
            }
        }
        catch (Exception) { /* eine ausgefallene Messung darf die Schleife nicht anhalten */ }
    }

    public void Dispose() => _timer.Dispose();
}
