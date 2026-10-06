using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Kontext fast voll: eine Schwelle für Fenster, Web und TUI. 90 % des Slot-Kontexts; ein Slot,
// der nicht arbeitet, zählt nicht (sonst würde ein leerer Slot mit altem Zählerstand dauerwarnen).
[TestClass]
public class CtxPressureTests
{
    private static SlotView Slot(bool busy, int used, int max) =>
        new(0, busy, false, 0, 0, used, max, 0, 0, -1, -1);

    [TestMethod]
    public void NearlyFull_OnlyFromNinetyPercent_AndOnlyWhileBusy()
    {
        Assert.IsFalse(CtxPressure.IsNearlyFull(Slot(true, 8999, 10000)), "89,99 % ist noch nicht fast voll");
        Assert.IsTrue(CtxPressure.IsNearlyFull(Slot(true, 9000, 10000)), "genau 90 % warnt");
        Assert.IsTrue(CtxPressure.IsNearlyFull(Slot(true, 9999, 10000)));
        Assert.IsFalse(CtxPressure.IsNearlyFull(Slot(false, 10000, 10000)), "ein untätiger Slot warnt nicht");
        Assert.IsFalse(CtxPressure.IsNearlyFull(Slot(true, 5000, 0)), "ohne bekannte Kontextgröße gibt es nichts zu melden");
    }

    [TestMethod]
    public void Worst_ReturnsTheFullestWorkingSlot()
    {
        var slots = new[] { Slot(true, 100, 1000), Slot(false, 999, 1000), Slot(true, 950, 1000) };
        var worst = CtxPressure.Worst(slots);
        Assert.IsNotNull(worst);
        Assert.AreEqual(950, worst!.CtxUsed, "der vollste arbeitende Slot, nicht der leerere mit dem höheren Zähler");
        Assert.AreEqual(0.95, CtxPressure.Fraction(worst), 0.001);
        Assert.IsNull(CtxPressure.Worst(new[] { Slot(false, 999, 1000) }), "nichts arbeitet → nichts zu melden");
        Assert.IsNull(CtxPressure.Worst(Array.Empty<SlotView>()));
    }

    [TestMethod]
    public void Texts_ComeFromStringsAndNameTheNumbers()
    {
        var text = Strings.CtxAlmostFullOf(9500, 10000);
        StringAssert.StartsWith(text, Strings.CtxAlmostFull);
        // Zahlen mit derselben Formatierung wie in den Strings (invariant, nicht nach Landessprache)
        StringAssert.Contains(text, Strings.N0(9500));
        StringAssert.Contains(text, Strings.N0(10000));
        Assert.AreEqual("90 %", Strings.Pct100(0.9));
        Assert.AreEqual("100 %", Strings.Pct100(1));
    }
}