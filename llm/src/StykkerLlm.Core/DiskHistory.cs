using System.Globalization;
using System.Text;
using Stykker.Shared.Disk;

namespace StykkerLlm.Core;

// Ein Takt eines Volumes für die Verlaufsanzeige: Lesen und Schreiben (Bytes je Sekunde), Auslastung (%). null = nicht gemessen.
public readonly record struct DiskPoint(double? Read, double? Write, double? Busy);

// Der Verlauf je Volume: die letzten Takte (ein Takt je Sekunde, also fünf Minuten). Der Takt schreibt, die Seite liest eine Kopie.
public sealed class DiskHistory
{
    public const int Length = 300;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<DiskPoint>> _series = new(StringComparer.Ordinal);

    public void Add(DiskSample sample)
    {
        lock (_gate)
        {
            foreach (var v in sample.Volumes)
            {
                if (!_series.TryGetValue(v.Letter, out var list)) _series[v.Letter] = list = new List<DiskPoint>();
                list.Add(new DiskPoint(v.ReadBytesPerSec, v.WriteBytesPerSec, v.BusyPercent));
                if (list.Count > Length) list.RemoveRange(0, list.Count - Length);
            }
        }
    }

    // Die Punkte eines Volumes, ältester zuerst (eine Kopie)
    public IReadOnlyList<DiskPoint> Of(string letter)
    {
        lock (_gate) return _series.TryGetValue(letter, out var list) ? list.ToArray() : Array.Empty<DiskPoint>();
    }

    // Die Buchstaben der Volumes mit Verlauf, alphabetisch
    public IReadOnlyList<string> Letters()
    {
        lock (_gate) return _series.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }
}

// Die Punkte einer Sparkline im Koordinatensystem der Karten (300 × 70, wie .spark in app.css): der neueste Wert steht rechts.
// Fehlende Werte fallen aus der Linie heraus; max ist der Wert oben (über max wird begrenzt).
public static class Sparkline
{
    public const double Width = 300, Height = 70;

    public static string Points(IReadOnlyList<double?> values, double max)
    {
        if (values.Count < 2 || !(max > 0)) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is not double v || !double.IsFinite(v)) continue;
            double x = i * Width / (values.Count - 1);
            double y = Height - Math.Clamp(v / max, 0, 1) * Height;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(x.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(y.ToString("0.#", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // Der größte Wert einer Reihe, mindestens floor (damit eine ruhige Leitung nicht die ganze Höhe füllt)
    public static double Scale(IEnumerable<double?> values, double floor) =>
        Math.Max(floor, values.Where(v => v is double d && double.IsFinite(d)).Select(v => v!.Value).DefaultIfEmpty(0).Max());
}
