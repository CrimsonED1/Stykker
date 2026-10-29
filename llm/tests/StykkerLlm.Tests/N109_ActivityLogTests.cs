using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Ereignisliste „Activity“ (ActivityLog): neueste zuerst, begrenzt, fertige Läufe aus DoneEvents, Zeit kurz
[TestClass]
public class N109_ActivityLogTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 7, 0, 0);

    [TestMethod]
    public void Recent_NewestFirst_AndLimited()
    {
        var log = new ActivityLog();
        for (int i = 0; i < ActivityLog.Max + 5; i++) log.Add(ActivityKind.Unload, "unload " + i, when: T0.AddMinutes(i));
        Assert.AreEqual(ActivityLog.Max, log.Count);
        var recent = log.Recent(3);
        CollectionAssert.AreEqual(new[] { "unload 104", "unload 103", "unload 102" }, recent.Select(e => e.Text).ToArray());
    }

    [TestMethod]
    public void Kinds_HaveIconAndAlarm()
    {
        Assert.IsTrue(new ActivityEvent(T0, ActivityKind.Crash, "x").Alarm);
        Assert.IsTrue(new ActivityEvent(T0, ActivityKind.RestartFailed, "x").Alarm);
        Assert.IsFalse(new ActivityEvent(T0, ActivityKind.Restart, "x").Alarm);
        Assert.AreEqual("e-crash", new ActivityEvent(T0, ActivityKind.Crash, "x").Icon);
        Assert.AreEqual("eject", new ActivityEvent(T0, ActivityKind.Unload, "x").Icon);
    }

    [TestMethod]
    public void DoneEvents_FlowIntoTheLog()
    {
        var log = new ActivityLog();
        var done = new DoneEvents();
        done.Added += log.Add;
        var r = new BenchResult { ProfileName = "qwen", DurationSec = 30 };
        done.Bench(r, null, T0);
        var e = log.Recent(1).Single();
        Assert.AreEqual(ActivityKind.Bench, e.Kind);
        Assert.AreEqual("benchmark of qwen finished", e.Text, "ohne „Stykker LLM:“ – die Marke steht schon über der Liste");
        Assert.AreEqual("Took 30 s", e.Detail);
        Assert.AreEqual(T0, e.When);
    }

    [TestMethod]
    public void Changed_FiresOnAdd()
    {
        var log = new ActivityLog();
        int n = 0;
        log.Changed += () => n++;
        log.Add(ActivityKind.Crash, "qwen stopped", "Out of memory");
        Assert.AreEqual(1, n);
    }

    [TestMethod]
    public void TimeText_TodayOnlyClock()
    {
        Assert.AreEqual("06:59", ActivityLog.TimeText(T0.AddMinutes(-1), T0));
        Assert.AreEqual("28.10. 23:10", ActivityLog.TimeText(new DateTime(2026, 10, 28, 23, 10, 0), T0));
    }
}
