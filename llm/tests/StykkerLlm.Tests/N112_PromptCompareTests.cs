using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Zwei Modelle mit derselben Frage (PromptCompare): wer fängt schneller an, wer schreibt schneller, Gleichstand unter 5 %
[TestClass]
public class N112_PromptCompareTests
{
    private static PromptResult R(double ttftMs, double tps, string? error = null) =>
        new("answer", "", 10, 100, ttftMs, 2000, tps, "stop", error);

    [TestMethod]
    public void Better_LowerOrHigherWins_TieUnderFivePercent()
    {
        Assert.AreEqual(0, PromptCompare.Better(90, 140, lowerWins: true));
        Assert.AreEqual(1, PromptCompare.Better(90, 140, lowerWins: false));
        Assert.AreEqual(-1, PromptCompare.Better(100, 103, lowerWins: true), "3 % ist Messrauschen");
        Assert.AreEqual(-1, PromptCompare.Better(0, 50, lowerWins: true), "unbekannt");
    }

    [TestMethod]
    public void Of_NamesTheFasterOne()
    {
        var c = PromptCompare.Of(R(90, 70), R(140, 33.4), "qwen3-8b", "gemma-3-12b")!;
        Assert.AreEqual(0, c.FasterStart);
        Assert.AreEqual(0, c.FasterOutput);
        Assert.AreEqual("faster start: qwen3-8b (90 vs 140 ms) · faster output: qwen3-8b (70.0 vs 33.4 t/s)", c.Text);
    }

    [TestMethod]
    public void Of_MixedAndTie()
    {
        var c = PromptCompare.Of(R(300, 70), R(100, 71), "a", "b")!;
        Assert.AreEqual(1, c.FasterStart);
        Assert.AreEqual(-1, c.FasterOutput);
        StringAssert.Contains(c.Text, "output about equal (70.0 / 71.0 t/s)");
    }

    [TestMethod]
    public void Of_NeedsTwoAnswers()
    {
        Assert.IsNull(PromptCompare.Of(R(90, 70), null, "a", "b"));
        Assert.IsNull(PromptCompare.Of(R(90, 70), R(0, 0, "down"), "a", "b"));
    }

    [TestMethod]
    public void Markdown_HasBothConversationsWithNames()
    {
        var md = PromptCompare.Markdown("qwen", new[] { new ChatTurn("user", "Hi"), new ChatTurn("assistant", "Hello") },
            "gemma", new[] { new ChatTurn("user", "Hi"), new ChatTurn("assistant", "Hey") }).Replace("\r\n", "\n");
        StringAssert.StartsWith(md, "## qwen\n\n**You:**\nHi");
        StringAssert.Contains(md, "## gemma\n\n**You:**\nHi\n\n**Model:**\nHey");
    }

    [TestMethod]
    public async Task TwoSessions_InParallel_AgainstTheSimulator()
    {
        using var sim = new SimHost(SimServerSpec.Defaults(), autoStep: false);
        var llamas = sim.World.Servers.Where(s => s.Spec.Kind == BackendKind.LlamaCpp).Take(2).ToList();
        Assert.AreEqual(2, llamas.Count);
        var a = new PromptSession();
        var b = new PromptSession();
        var ra = a.SendAsync(sim.Http, "http://127.0.0.1:" + llamas[0].Port, "hello");
        var rb = b.SendAsync(sim.Http, "http://127.0.0.1:" + llamas[1].Port, "hello");
        await Task.WhenAll(ra, rb);
        Assert.IsTrue(ra.Result.Ok && rb.Result.Ok, ra.Result.Error + rb.Result.Error);
        Assert.IsNotNull(PromptCompare.Of(ra.Result, rb.Result, "a", "b"));
    }
}
