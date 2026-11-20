using System.Globalization;
using System.Text;

namespace Stykker.Shared.Sampling;

// Die Punkte einer Sparkline im Koordinatensystem der Karten (300 × 70, wie .hw-spark im Design-System): der neueste Wert steht rechts.
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
