namespace StykkerLlm.Core;

// Kontext fast voll: ab dieser Belegung eines Slots ist der Kontext bald erschöpft – der Server
// kürzt dann Prompt oder Antwort. Eine Regel für alle Oberflächen (Fenster, Web, TUI), damit überall dieselbe
// Schwelle gilt und derselbe Text erscheint.
public static class CtxPressure
{
    public const double NearlyFull = 0.9;

    // Nur ein Slot, der wirklich arbeitet, zählt: ein leerer Slot mit altem Zählerstand ist kein Hinweis.
    public static double Fraction(SlotView v) => v.Busy && v.CtxMax > 0 ? Math.Clamp((double)v.CtxUsed / v.CtxMax, 0, 1) : 0;

    public static bool IsNearlyFull(SlotView v) => Fraction(v) >= NearlyFull;

    // Der vollste gerade arbeitende Slot – für Karte und TUI, wo nicht je Slot eine Zeile Platz ist
    public static SlotView? Worst(IEnumerable<SlotView> slots)
    {
        SlotView? worst = null;
        double best = 0;
        foreach (var s in slots)
        {
            double f = Fraction(s);
            if (f > best) { best = f; worst = s; }
        }
        return best > 0 ? worst : null;
    }
}