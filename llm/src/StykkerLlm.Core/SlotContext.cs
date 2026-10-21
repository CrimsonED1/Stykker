using System.Globalization;

namespace StykkerLlm.Core;

// Kontext-Füllstand eines Slots (llama.cpp): der Balken je Slot auf der Serverkarte. Stufen: ok unter 90 %, warn ab 90 %
// (dieselbe Schwelle wie CtxPressure.NearlyFull, Chip „ctx full“),
// full ab 100 %. Ohne Werte (CtxMax 0) gibt es keinen Balken (Level None). Die Karte zeigt nur, was hier steht.
public enum CtxFillLevel { None, Ok, Warn, Full }

public sealed record CtxFill(CtxFillLevel Level, int Percent, string Text)
{
    public bool Shown => Level != CtxFillLevel.None;

    // Name der Stufe für die Oberfläche (CSS-Klasse)
    public string Css => Level switch { CtxFillLevel.Warn => "warn", CtxFillLevel.Full => "full", _ => "ok" };
}

public static class SlotContextFill
{
    public const int WarnPercent = (int)(CtxPressure.NearlyFull * 100);

    public static CtxFill Of(int used, int max)
    {
        if (max <= 0) return new CtxFill(CtxFillLevel.None, 0, "");
        int u = Math.Max(0, used);
        int percent = (int)Math.Min(100L, (long)u * 100 / max);
        var level = u >= max ? CtxFillLevel.Full
            : (long)u * 100 >= (long)max * WarnPercent ? CtxFillLevel.Warn
            : CtxFillLevel.Ok;
        return new CtxFill(level, percent, Strings.CtxFillTip(K(u), K(max)));
    }

    // Kurzform für Zählerstände: „512“, „18.2k“, „40k“ (eine Nachkommastelle, nur wenn sie etwas bringt)
    public static string K(int n)
    {
        if (n < 1000) return n.ToString(CultureInfo.InvariantCulture);
        double k = Math.Round(n / 1000.0, 1, MidpointRounding.AwayFromZero);
        return k == Math.Floor(k)
            ? k.ToString("0", CultureInfo.InvariantCulture) + "k"
            : k.ToString("0.0", CultureInfo.InvariantCulture) + "k";
    }
}
