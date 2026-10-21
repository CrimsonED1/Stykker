using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N97 – Überarbeitete Benchmark-Seite: Zeilen je Testart, passende Kontextgrößen, Vorauswahl, Diagrammpunkte, Kontext je Slot
// aus den Startargumenten. Nur Rechnungen, ohne Netz und ohne echten Server.
[TestClass]
public class N97_BenchViewTests
{
    private static BenchStep Ctx(int size, double read, double write, bool ok = true) => new()
    {
        Kind = "context", Name = $"Context {size / 1024}k", TargetPromptTokens = size, PromptTokens = size, PromptTps = read, GenTps = write, Ok = ok,
    };

    [TestMethod]
    public void Row_ToolCallShowsPassOrFailInsteadOfSpeeds()
    {
        var ok = BenchView.RowOf(new BenchStep { Kind = "tool", Ok = true, PromptTokens = 29, PromptTps = 373, GenTps = 33.9, Note = "ok: get_weather({\"city\":\"Paris\"})" });
        Assert.AreEqual("–", ok.Read);
        Assert.AreEqual("–", ok.Write);
        Assert.AreEqual(Strings.BenchToolOk, ok.Result);
        Assert.IsFalse(ok.Comparable, "ein Werkzeugaufruf wird nicht prozentual mit dem Vorlauf verglichen");

        var bad = BenchView.RowOf(new BenchStep { Kind = "tool", Ok = false, Note = "no tool call (finish: stop)" });
        Assert.AreEqual("no tool call (finish: stop)", bad.Result);
    }

    [TestMethod]
    public void Row_ParallelShowsTheAggregateAsWriteSpeed()
    {
        var r = BenchView.RowOf(new BenchStep { Kind = "parallel", Ok = true, Parallel = 4, AggregateTps = 210.5, GenTps = 55.2, PromptTps = 800 });
        Assert.AreEqual("Σ 210.5", r.Write);
        StringAssert.Contains(r.WriteTip, "55.2");
        StringAssert.Contains(r.WriteTip, "4 requests");
    }

    [TestMethod]
    public void Row_ShortPromptsDimTheReadSpeed()
    {
        Assert.IsTrue(BenchView.RowOf(new BenchStep { Kind = "chat", Ok = true, PromptTokens = 27, PromptTps = 373, GenTps = 33.9 }).ReadWeak);
        var ctx = BenchView.RowOf(Ctx(4096, 655, 59.6));
        Assert.IsFalse(ctx.ReadWeak);
        Assert.AreEqual("655", ctx.Read);
        Assert.AreEqual("59.6", ctx.Write);
        Assert.AreEqual(Strings.BenchStepFailed, BenchView.RowOf(new BenchStep { Kind = "chat", Ok = false }).Result);
    }

    [TestMethod]
    public void Sizes_FitTheSlotLikeTheRunAndThePresetStopsAt32k()
    {
        // dieselbe Regel wie der Lauf: Prompt + Antwort + 192 Reserve
        Assert.IsTrue(BenchView.Fits(4096, 8192, 128));
        Assert.IsFalse(BenchView.Fits(8192, 8192, 128));
        Assert.IsTrue(BenchView.Fits(262144, null, 128), "unbekannte Slot-Größe: nichts sperren");
        CollectionAssert.AreEqual(new[] { 1024, 2048, 4096 }, BenchView.DefaultSizes(8192, 128));
        CollectionAssert.AreEqual(new[] { 1024, 2048, 4096, 8192, 16384, 32768 }, BenchView.DefaultSizes(131072, 128));
        CollectionAssert.AreEqual(new[] { 1024, 4096 }, BenchView.DefaultSizes(null, 128));
        var opt = new BenchOptions { ContextSizes = BenchOptions.Ladder, ContextPerSlot = 8192, GenTokens = 128 };
        CollectionAssert.AreEqual(BenchView.DefaultSizes(8192, 128), opt.UsableSizes().ToList(), "Seite und Lauf rechnen gleich");
    }

    [TestMethod]
    public void Chart_LogXLinearYAndOneLinePerResult()
    {
        var a = new BenchResult { ProfileName = "A", Steps = { Ctx(1024, 800, 70), Ctx(4096, 600, 60), Ctx(16384, 400, 40) } };
        var b = new BenchResult { ProfileName = "B", Steps = { Ctx(1024, 300, 30), Ctx(4096, 0, 0, ok: false) } };
        var c = BenchView.ContextChart(new[] { a, b }, read: false);
        Assert.AreEqual(2, c.Lines.Count);
        Assert.AreEqual(3, c.Lines[0].Dots.Count);
        Assert.AreEqual(1, c.Lines[1].Dots.Count, "fehlgeschlagene Stufen fallen weg");
        // x logarithmisch: 4k liegt genau in der Mitte zwischen 1k und 16k
        double x1 = c.Lines[0].Dots[0].X, x4 = c.Lines[0].Dots[1].X, x16 = c.Lines[0].Dots[2].X;
        Assert.AreEqual((x1 + x16) / 2, x4, 0.11);
        Assert.AreEqual(BenchView.Left, x1, 0.01);
        Assert.AreEqual(BenchView.W - BenchView.Right, x16, 0.01);
        // y: höherer Wert liegt weiter oben, die Achse endet an einem runden Wert über dem Maximum (70 → 100)
        Assert.IsTrue(c.Lines[0].Dots[0].Y < c.Lines[0].Dots[2].Y);
        Assert.AreEqual("100", c.YTicks[^1].Label);
        CollectionAssert.AreEqual(new[] { "1k", "4k", "16k" }, c.XTicks.Select(t => t.Label).ToArray());
        Assert.IsTrue(BenchView.ContextChart(new[] { new BenchResult() }, true).Empty);
    }

    [TestMethod]
    public void NiceMax_RoundsUpWithHeadroom()
    {
        Assert.AreEqual(100, BenchView.NiceMax(70));
        Assert.AreEqual(1000, BenchView.NiceMax(769));
        Assert.AreEqual(250, BenchView.NiceMax(220));
        Assert.AreEqual(1, BenchView.NiceMax(0));
    }

    [TestMethod]
    public void PerSlot_SplitsTheContextOverTheSlots()
    {
        Assert.AreEqual(8192, BenchmarkService.PerSlot(new[] { "-m", "x.gguf", "-c", "16384", "-np", "2" }));
        Assert.AreEqual(32768, BenchmarkService.PerSlot(new[] { "--ctx-size", "32768" }));
        Assert.IsNull(BenchmarkService.PerSlot(new[] { "-m", "x.gguf" }));
    }
}
