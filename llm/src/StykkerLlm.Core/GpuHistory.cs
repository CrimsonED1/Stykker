namespace StykkerLlm.Core;

// Ein Messpunkt der GPU für die Verlaufsanzeige: Auslastung (%), belegter VRAM (GB), Leistung (W), Temperatur (°C)
public readonly record struct GpuPoint(double Util, double MemUsedGb, double PowerW, double TempC);

// Der Verlauf der GPU: die letzten Messpunkte. Der Takt schreibt bei jeder Lesung der Grafikkarte (im Leerlauf etwa alle
// zwei Sekunden, bei Aufnahme jede Sekunde), die Seite liest eine Kopie. Ohne Grafikkarte bleibt der Verlauf leer.
public sealed class GpuHistory
{
    public const int Length = 300;

    private readonly object _gate = new();
    private readonly List<GpuPoint> _points = new();

    public void Add(GpuSample sample)
    {
        lock (_gate)
        {
            _points.Add(new GpuPoint(sample.Util, sample.MemUsedGb, sample.PowerW, sample.TempC));
            if (_points.Count > Length) _points.RemoveRange(0, _points.Count - Length);
        }
    }

    // Die Punkte, ältester zuerst (eine Kopie)
    public IReadOnlyList<GpuPoint> Points()
    {
        lock (_gate) return _points.ToArray();
    }
}
