namespace StykkerHud.Core;

// Die Messschleife. Sie gehört in den Dienst, nicht in eine Seite – aber sie liest nur, solange jemand zusieht:
// der erste Abruf (Seite oder Fenster) wirft sie an, und wenn 15 Sekunden niemand mehr gefragt hat, hält sie
// wieder an. So kostet eine geschlossene Anwendung nichts, und der Server startet davon unabhängig sofort,
// weil er beim Start nichts messen muss.
public sealed class HudService : IDisposable
{
    private static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(15);

    private readonly MetricsSampler _sampler;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private HudSnapshot? _current;
    private DateTime _lastViewer = DateTime.MinValue;
    private bool _running;

    public HudService(MetricsSampler sampler)
    {
        _sampler = sampler;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    // Eine Messung abholen. Jeder Abruf gilt als „es sieht jemand zu" und hält die Schleife am Laufen.
    public HudSnapshot Snapshot()
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
                if (DateTime.Now - _lastViewer > IdleStop)
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