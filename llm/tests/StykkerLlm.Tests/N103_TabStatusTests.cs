using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Tab-Titel, Favicon-Stufe und Absturz-Meldung (TabStatus): was ein Browser-Tab im Hintergrund über den Zustand sagt
[TestClass]
public class N103_TabStatusTests
{
    private static ServerLost Lost(string? cause = null) =>
        new("qwen3-8b", "http://127.0.0.1:8081", 4711, null, Array.Empty<string>(), cause, new DateTime(2026, 10, 29, 6, 30, 0, DateTimeKind.Utc));

    [TestMethod]
    public void Title_Idle_IsPageAndBrand()
    {
        Assert.AreEqual("Monitor · Stykker LLM", TabStatus.Title("Monitor", new[] { 0.0, 0.0 }, null));
        Assert.AreEqual("Stykker LLM", TabStatus.Title("", Array.Empty<double>(), null));
    }

    [TestMethod]
    public void Title_Busy_SumsTokensPerSecondInFront()
    {
        Assert.AreEqual("▶ 94.5 t/s · Monitor · Stykker LLM", TabStatus.Title("Monitor", new[] { 60.0, 34.5, 0 }, null));
        Assert.AreEqual("▶ 142 t/s · Benchmarks · Stykker LLM", TabStatus.Title("Benchmarks", new[] { 100.0, 42.2 }, null));
    }

    [TestMethod]
    public void Title_Lost_WinsOverTokens()
    {
        Assert.AreEqual("⚠ qwen3-8b stopped · Monitor · Stykker LLM", TabStatus.Title("Monitor", new[] { 50.0 }, Lost()));
    }

    [TestMethod]
    public void Level_FollowsState()
    {
        Assert.AreEqual(TabLevel.Idle, TabStatus.Level(new[] { 0.0 }, null));
        Assert.AreEqual(TabLevel.Busy, TabStatus.Level(new[] { 0.0, 3.1 }, null));
        Assert.AreEqual(TabLevel.Alarm, TabStatus.Level(new[] { 3.1 }, Lost()));
        Assert.AreEqual("idle", TabStatus.LevelName(TabLevel.Idle));
        Assert.AreEqual("busy", TabStatus.LevelName(TabLevel.Busy));
        Assert.AreEqual("alarm", TabStatus.LevelName(TabLevel.Alarm));
    }

    [TestMethod]
    public void Alert_NoneWithoutLoss()
    {
        Assert.IsNull(TabStatus.Alert(null));
    }

    [TestMethod]
    public void Alert_HasStableIdCauseAndTime()
    {
        var a = TabStatus.Alert(Lost("Out of memory"))!;
        var b = TabStatus.Alert(Lost("Out of memory"))!;
        Assert.AreEqual(a.Id, b.Id, "dasselbe Ereignis meldet jeder Tab unter derselben Id");
        Assert.AreEqual("Stykker LLM: qwen3-8b stopped", a.Title);
        Assert.AreEqual("Out of memory", a.Body);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 29, 6, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), a.AtMs);
    }

    [TestMethod]
    public void Alert_WithoutCause_UsesTrayText()
    {
        var a = TabStatus.Alert(Lost())!;
        Assert.AreEqual(Strings.LostBalloon("qwen3-8b"), a.Body);
    }

    [TestMethod]
    public void Alert_NewLoss_GetsNewId()
    {
        var first = TabStatus.Alert(Lost())!;
        var later = TabStatus.Alert(Lost() with { When = new DateTime(2026, 10, 29, 6, 31, 0, DateTimeKind.Utc) })!;
        Assert.AreNotEqual(first.Id, later.Id);
    }
}
