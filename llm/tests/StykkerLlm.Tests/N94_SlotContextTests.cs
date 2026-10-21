using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N94 – Kontext-Füllstand je Slot (Serverkarte): Anteil, Stufen ok/warn/full (ab 85 % und ab 100 %), kein Balken
// ohne Werte, Text mit k-Rundung.
[TestClass]
public class N94_SlotContextTests
{
    [TestMethod]
    public void No_bar_without_context_max()
    {
        var f = SlotContextFill.Of(500, 0);
        Assert.AreEqual(CtxFillLevel.None, f.Level);
        Assert.IsFalse(f.Shown);
        Assert.AreEqual(0, f.Percent);
        Assert.IsFalse(SlotContextFill.Of(0, -1).Shown, "auch ein negativer Wert gibt keinen Balken");
    }

    [TestMethod]
    public void Below_85_percent_is_ok()
    {
        var f = SlotContextFill.Of(33600, 40000);   // genau 84 %
        Assert.AreEqual(CtxFillLevel.Ok, f.Level);
        Assert.AreEqual(84, f.Percent);
        Assert.AreEqual("ok", f.Css);
        Assert.AreEqual(CtxFillLevel.Ok, SlotContextFill.Of(0, 40000).Level);
    }

    [TestMethod]
    public void From_85_percent_is_warn_and_below_100_stays_warn()
    {
        Assert.AreEqual(CtxFillLevel.Warn, SlotContextFill.Of(34000, 40000).Level, "genau 85 % ist schon warn");
        Assert.AreEqual(CtxFillLevel.Warn, SlotContextFill.Of(39999, 40000).Level, "knapp unter voll bleibt warn");
        Assert.AreEqual(99, SlotContextFill.Of(39999, 40000).Percent, "Anteil wird abgerundet, nie 100 vor voll");
        Assert.AreEqual("warn", SlotContextFill.Of(34000, 40000).Css);
    }

    [TestMethod]
    public void From_100_percent_is_full_and_clamped()
    {
        var exact = SlotContextFill.Of(40000, 40000);
        Assert.AreEqual(CtxFillLevel.Full, exact.Level);
        Assert.AreEqual(100, exact.Percent);
        Assert.AreEqual("full", exact.Css);

        var over = SlotContextFill.Of(41000, 40000);
        Assert.AreEqual(CtxFillLevel.Full, over.Level);
        Assert.AreEqual(100, over.Percent, "ein Anteil über 100 % wird auf 100 begrenzt");
    }

    [TestMethod]
    public void Tooltip_text_rounds_to_k()
    {
        Assert.AreEqual("18.2k / 40k context used", SlotContextFill.Of(18200, 40000).Text);
        Assert.AreEqual("18.2k / 40k context used", SlotContextFill.Of(18234, 40000).Text);
        Assert.AreEqual("512 / 1k context used", SlotContextFill.Of(512, 1000).Text);
        Assert.AreEqual("0 / 8.2k context used", SlotContextFill.Of(0, 8192).Text);
        Assert.AreEqual("999 / 999 context used", SlotContextFill.Of(999, 999).Text, "unter 1000 ohne k");
    }

    [TestMethod]
    public void K_rounding()
    {
        Assert.AreEqual("0", SlotContextFill.K(0));
        Assert.AreEqual("999", SlotContextFill.K(999));
        Assert.AreEqual("1k", SlotContextFill.K(1000));
        Assert.AreEqual("18.3k", SlotContextFill.K(18250), "halbe Stelle wird aufgerundet");
        Assert.AreEqual("41k", SlotContextFill.K(40960), "eine Nachkommastelle, die 0 ist, entfällt");
    }
}
