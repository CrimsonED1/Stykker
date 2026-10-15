using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N84 – Benchmark-Seite: Vergleich zum letzten Lauf, Restzeit, Parallel-Hinweis je Slot-Zahl, Spaltennamen im Export.
// Nur Rechnungen und Texte, ohne Netz und ohne echten Server.
[TestClass]
public class N84_BenchPageTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 20, 0, 0);

    private static BenchResult Run(string profile, DateTime started, params BenchStep[] steps) => new()
    {
        ProfileKey = profile, ServerKey = "srv", Server = "srv", Model = "m", Started = started, Steps = steps.ToList(),
    };

    // Schritt mit gemessenen Zeiten: readTps = Prompt-Token/s, writeTps = Erzeugung/s (aus Token und Sekunden)
    private static BenchStep Measured(string kind, string name, double readTps, double writeTps, int target = 0, int promptTokens = 1000, int genTokens = 100, bool ok = true) => new()
    {
        Kind = kind, Name = name, TargetPromptTokens = target, Ok = ok,
        PromptTokens = promptTokens, PromptSec = promptTokens / readTps, PromptTps = readTps,
        GenTokens = genTokens, GenSec = genTokens / writeTps, GenTps = writeTps, TotalSec = 1,
    };

    // ── Vergleich zum letzten Lauf ──

    [TestMethod]
    public void Compare_BetterWorseSameUseTheFivePercentBand()
    {
        var before = Run("p", T0, Measured("chat", "Short chat", 100, 50));
        var better = BenchCompare.Against(before, Measured("chat", "Short chat", 120, 50));
        Assert.IsNotNull(better);
        Assert.AreEqual(20.0, better!.ReadPct!.Value, 1e-9);
        Assert.AreEqual(BenchTrend.Better, better.ReadTrend);
        Assert.AreEqual(BenchTrend.Same, better.WriteTrend);               // 0 %: grau

        var worse = BenchCompare.Against(before, Measured("chat", "Short chat", 90, 47.5));
        Assert.AreEqual(BenchTrend.Worse, worse!.ReadTrend);               // -10 %
        Assert.AreEqual(-5.0, worse.WritePct!.Value, 1e-9);
        Assert.AreEqual(BenchTrend.Worse, worse.WriteTrend);               // genau -5 % zählt als schlechter

        var same = BenchCompare.Against(before, Measured("chat", "Short chat", 97, 52));
        Assert.AreEqual(BenchTrend.Same, same!.ReadTrend);                 // -3 %
        Assert.AreEqual(BenchTrend.Same, same.WriteTrend);                 // +4 %
    }

    [TestMethod]
    public void Compare_WithoutAnEarlierRunOrAMeasuredStepThereIsNothingToShow()
    {
        var before = Run("p", T0, Measured("chat", "Short chat", 100, 50));
        Assert.IsNull(BenchCompare.Against(null, Measured("chat", "Short chat", 100, 50)));               // fehlender Vorlauf
        Assert.IsNull(BenchCompare.Against(before, Measured("context", "Context 8k", 100, 50)));         // anderer Test
        Assert.IsNull(BenchCompare.Against(before, Measured("chat", "Short chat", 100, 50, ok: false)));  // fehlgeschlagener Schritt
        var noTiming = Run("p", T0, new BenchStep { Kind = "chat", Name = "Short chat", Ok = true });   // ohne Zeiten: kein Prozentwert
        var change = BenchCompare.Against(noTiming, Measured("chat", "Short chat", 100, 50));
        Assert.IsNotNull(change);
        Assert.IsNull(change!.ReadPct);
        Assert.AreEqual(BenchTrend.None, change.ReadTrend);
    }

    [TestMethod]
    public void Trend_OfNothingIsNone()
    {
        Assert.AreEqual(BenchTrend.None, BenchCompare.Trend(null));
        Assert.AreEqual(BenchTrend.Better, BenchCompare.Trend(5));
        Assert.AreEqual(BenchTrend.Worse, BenchCompare.Trend(-5));
    }

    [TestMethod]
    public void PreviousSimilar_TakesTheEarlierRunNotTheNewest()
    {
        var lib = new Library(null);
        var a = Run("p", new DateTime(2026, 1, 1), Measured("chat", "Short chat", 100, 50));
        var b = Run("p", new DateTime(2026, 1, 2), Measured("chat", "Short chat", 110, 55));
        var c = Run("p", new DateTime(2026, 1, 3), Measured("chat", "Short chat", 120, 60));
        var other = Run("q", new DateTime(2026, 1, 2, 12, 0, 0), Measured("chat", "Short chat", 999, 999));
        foreach (var r in new[] { a, b, c, other }) lib.AddBenchmark(r);

        Assert.AreEqual(b.Id, BenchmarkRunner.PreviousSimilar(lib, c)!.Id);      // letzter früherer Lauf, nicht der neueste
        Assert.AreEqual(a.Id, BenchmarkRunner.PreviousSimilar(lib, b)!.Id);
        Assert.IsNull(BenchmarkRunner.PreviousSimilar(lib, a));                 // der erste Lauf hat keinen Vorlauf
    }

    // ── Restzeit ──

    private static BenchOptions Plan(params int[] sizes) => new()
    {
        Chat = true, Tool = false, Parallel = false, ContextSizes = sizes, ContextPerSlot = 65536, GenTokens = 128, Repeats = 1, Slots = 1,
    };

    [TestMethod]
    public void MinutesLeft_IsUnknownUntilTheFirstMeasuredStep()
    {
        var empty = Run("p", T0);
        Assert.IsNull(BenchmarkRunner.MinutesLeft(Plan(1024), empty));
        var failed = Run("p", T0, Measured("chat", "Short chat", 1000, 50, ok: false));
        Assert.IsNull(BenchmarkRunner.MinutesLeft(Plan(1024), failed));
    }

    [TestMethod]
    public void MinutesLeft_CountsThePromptAndAnswerTokensOfTheRemainingSteps()
    {
        // Chat gemessen (1000 Prompt-t/s, 50 Erzeugung-t/s). Offen: sechs Kontextstufen 1k..32k (64512 Prompt-Token, 6 x 128 Antwort-Token)
        // 64.512 s + 768 / 50 s = 79.9 s, aufgerundet auf 2 min
        var done = Run("p", T0, Measured("chat", "Short chat", 1000, 50, promptTokens: 12, genTokens: 100));
        var plan = Plan(BenchOptions.Ladder);
        Assert.AreEqual(2, BenchmarkRunner.MinutesLeft(plan, done));

        // Die gemessene Kontextstufe 1k fällt weg: 63488 Prompt-Token und 640 Antwort-Token -> 76.3 s, ebenfalls 2 min
        done.Steps.Add(Measured("context", "Context 1k", 1000, 50, target: 1024, promptTokens: 1024, genTokens: 128));
        Assert.AreEqual(2, BenchmarkRunner.MinutesLeft(plan, done));
    }

    [TestMethod]
    public void MinutesLeft_IsNullWhenNothingIsLeftOrAContextStepFailed()
    {
        var done = Run("p", T0, Measured("chat", "Short chat", 1000, 50, promptTokens: 12, genTokens: 100));
        Assert.IsNull(BenchmarkRunner.MinutesLeft(Plan(), done));                        // nur Chat geplant, schon fertig
        done.Steps.Add(Measured("context", "Context 8k", 1000, 50, target: 8192, promptTokens: 8192, genTokens: 128, ok: false));
        Assert.IsNull(BenchmarkRunner.MinutesLeft(Plan(1024, 2048, 4096, 8192, 16384), done));   // größere Stufen laufen nicht mehr
    }

    [TestMethod]
    public void MinutesLeft_CountsToolAndParallelRequests()
    {
        // Werkzeug (128 Antwort-Token) und vier parallele Anfragen (4 x 400 Prompt-Token, 4 x 128 Antwort-Token): 14.4 s, 1 min
        var done = Run("p", T0, Measured("chat", "Short chat", 1000, 50, promptTokens: 12, genTokens: 100));
        var plan = new BenchOptions { Chat = false, Tool = true, Parallel = true, ContextSizes = Array.Empty<int>(), GenTokens = 128, Slots = 4 };
        Assert.AreEqual(1, BenchmarkRunner.MinutesLeft(plan, done));
        done.Steps.Add(new BenchStep { Kind = "tool", Name = "Tool call", Ok = true });
        done.Steps.Add(new BenchStep { Kind = "parallel", Name = "4 parallel requests", Ok = true });
        Assert.IsNull(BenchmarkRunner.MinutesLeft(plan, done));
    }

    // ── Parallel-Hinweis je Slot-Zahl ──

    [TestMethod]
    public void ParallelHint_FollowsTheSlotCount()
    {
        Assert.AreEqual(BenchmarkRunner.ParallelHint.Unknown, BenchmarkRunner.ParallelHintFor(null));    // unbekannt: Hinweis wie bisher
        Assert.AreEqual(BenchmarkRunner.ParallelHint.OneSlot, BenchmarkRunner.ParallelHintFor(1));       // ein Slot: gesperrt
        Assert.AreEqual(BenchmarkRunner.ParallelHint.ManySlots, BenchmarkRunner.ParallelHintFor(4));     // mehrere: frei, ohne Hinweis
    }

    [TestMethod]
    public void SlotsFromArgs_ReadsNpAndParallel()
    {
        Assert.AreEqual(4, BenchmarkRunner.SlotsFromArgs(new[] { "-m", "a.gguf", "-np", "4" }));
        Assert.AreEqual(2, BenchmarkRunner.SlotsFromArgs(new[] { "-m", "a.gguf", "--parallel", "2" }));
        Assert.IsNull(BenchmarkRunner.SlotsFromArgs(new[] { "-m", "a.gguf" }));                        // nicht angegeben: unbekannt
    }

    // ── Texte ──

    [TestMethod]
    public void Strings_StepCountFollowsTheGrammar()
    {
        Assert.AreEqual("1 step done", Strings.BenchStepsDone(1));
        Assert.AreEqual("0 steps done", Strings.BenchStepsDone(0));
        Assert.AreEqual("2 steps done", Strings.BenchStepsDone(2));
        Assert.AreEqual("about 3 min left", Strings.BenchMinutesLeft(3));
        Assert.AreEqual("last run 20:42 · ok", Strings.BenchLastRun("20:42", Strings.BenchLastOk));
    }

    [TestMethod]
    public void Export_UsesTheSameReadAndWriteNamesAsThePage()
    {
        var result = Run("p", T0, Measured("chat", "Short chat", 800, 25));
        result.Server = "A";
        var md = BenchmarkExport.Markdown(new[] { result });
        StringAssert.Contains(md, "| Test | A read / write |");
        var csv = BenchmarkExport.Csv(new[] { result }).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        StringAssert.Contains(csv[0], ",ReadTokensPerSec,");
        StringAssert.Contains(csv[0], ",WriteTokensPerSec,");
        Assert.IsFalse(csv[0].Contains("PromptTokensPerSec"));
    }
}
