using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Ansicht der Liste „Recent requests“ (RequestRows.View): Modellfilter, nur Probleme, kurz oder lang
[TestClass]
public class N108_RequestViewTests
{
    private static RequestRow Row(int i, string model = "qwen", string result = "ok") =>
        new("srv", "k", null, model, new DateTime(2026, 10, 29, 7, 0, 0).AddSeconds(-i), 2, 500, 50, 100, "", "cli", null, null, result);

    private static List<RequestRow> Rows(int n, Func<int, string>? result = null, Func<int, string>? model = null) =>
        Enumerable.Range(0, n).Select(i => Row(i, model?.Invoke(i) ?? "qwen", result?.Invoke(i) ?? "ok")).ToList();

    [TestMethod]
    public void Short_ShowsEight_AndSaysThereIsMore()
    {
        var v = RequestRows.View(Rows(20), null, problemsOnly: false, longList: false);
        Assert.AreEqual(RequestRows.TakeShort, v.Shown.Count);
        Assert.IsTrue(v.More);
        Assert.AreEqual(0, v.Problems);
    }

    [TestMethod]
    public void Long_ShowsUpToTwentyFour()
    {
        Assert.AreEqual(20, RequestRows.View(Rows(20), null, false, longList: true).Shown.Count);
        Assert.AreEqual(RequestRows.TakeLong, RequestRows.View(Rows(30), null, false, true).Shown.Count);
        Assert.IsFalse(RequestRows.View(Rows(5), null, false, false).More);
    }

    [TestMethod]
    public void ProblemsOnly_FindsOlderFailures()
    {
        // Probleme ganz hinten in der langen Liste: die kurze Liste sieht sie nicht, der Filter schon
        var rows = Rows(20, result: i => i switch { 15 => "err", 17 => "abort", 19 => "full", _ => "ok" });
        var all = RequestRows.View(rows, null, false, false);
        Assert.AreEqual(3, all.Problems);
        Assert.IsFalse(all.Shown.Any(RequestRows.IsProblem));

        var only = RequestRows.View(rows, null, problemsOnly: true, longList: false);
        CollectionAssert.AreEqual(new[] { "err", "abort", "full" }, only.Shown.Select(r => r.Result).ToArray());
        Assert.IsFalse(only.More);
    }

    [TestMethod]
    public void ModelFilter_CountsOnlyThatModel()
    {
        var rows = Rows(10, result: i => i % 2 == 0 ? "err" : "ok", model: i => i < 4 ? "gemma" : "qwen");
        var v = RequestRows.View(rows, "gemma", problemsOnly: false, longList: false);
        Assert.AreEqual(4, v.Shown.Count);
        Assert.AreEqual(2, v.Problems);
    }

    [TestMethod]
    public void ProblemsLabel()
    {
        Assert.AreEqual("problems", Strings.ReqProblems(0));
        Assert.AreEqual("problems · 3", Strings.ReqProblems(3));
    }
}
