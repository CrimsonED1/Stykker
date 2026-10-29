using StykkerLlm.Core;
using StykkerLlm.Core.Eval;

namespace StykkerLlm.Tests;

// Fertige lange Läufe als Browser-Meldung (DoneEvents, TabStatus.Alert mit Absturz und Lauf)
[TestClass]
public class N107_DoneEventsTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 7, 0, 0, DateTimeKind.Local);

    private static EvalJob Job(EvalJobState state, DateTime? finished) =>
        new() { Model = new EvalModelDef { Name = "qwen" }, Suite = "basic", State = state, Finished = finished };

    [TestMethod]
    public void Bench_UsesChatSpeedAndRegression()
    {
        var done = new DoneEvents();
        var r = new BenchResult { ProfileName = "qwen3 8B", DurationSec = 312 };
        r.Steps.Add(new BenchStep { Kind = "context", Ok = true, GenTps = 80 });
        r.Steps.Add(new BenchStep { Kind = "chat", Ok = true, GenTps = 61.27 });
        done.Bench(r, new BenchRegression { DropPct = 14.2 }, T0);
        var e = done.Last!;
        Assert.AreEqual("bench", e.Kind);
        Assert.AreEqual("Stykker LLM: benchmark of qwen3 8B finished", e.Title);
        Assert.AreEqual("Took 5 min · 61.3 t/s · 14 % slower than the last run", e.Body);
    }

    [TestMethod]
    public void Bench_WithoutChat_TakesBestContextStep()
    {
        var done = new DoneEvents();
        var r = new BenchResult { Server = "gemma", DurationSec = 45 };
        r.Steps.Add(new BenchStep { Kind = "context", Ok = true, GenTps = 30 });
        r.Steps.Add(new BenchStep { Kind = "context", Ok = true, GenTps = 42 });
        r.Steps.Add(new BenchStep { Kind = "context", Ok = false, GenTps = 99 });
        done.Bench(r, null, T0);
        Assert.AreEqual("Took 45 s · 42.0 t/s", done.Last!.Body);
    }

    [TestMethod]
    public void Queue_ReportsOnceWhenItStops()
    {
        var done = new DoneEvents();
        var jobs = new List<EvalJob> { Job(EvalJobState.Done, T0.AddMinutes(-30)) };   // aus einem früheren Lauf
        done.Queue(false, jobs, T0);
        done.Queue(true, jobs, T0.AddSeconds(2));
        jobs.Add(Job(EvalJobState.Done, T0.AddMinutes(5)));
        jobs.Add(Job(EvalJobState.Done, T0.AddMinutes(9)));
        jobs.Add(Job(EvalJobState.Failed, T0.AddMinutes(12)));
        jobs.Add(Job(EvalJobState.Cancelled, T0.AddMinutes(13)));
        done.Queue(true, jobs, T0.AddMinutes(12));
        Assert.IsNull(done.Last, "solange sie läuft, keine Meldung");

        done.Queue(false, jobs, T0.AddMinutes(14));
        Assert.AreEqual("tests", done.Last!.Kind);
        Assert.AreEqual(Strings.DoneTestsTitle, done.Last.Title);
        Assert.AreEqual("2 jobs done, 1 failed", done.Last.Body);
        Assert.AreEqual(T0.AddMinutes(14), done.Last.When);

        done.Queue(false, jobs, T0.AddMinutes(20));
        Assert.AreEqual(T0.AddMinutes(14), done.Last.When, "nur einmal je Ende");
    }

    [TestMethod]
    public void Queue_OnlyCancelled_NoAlert()
    {
        var done = new DoneEvents();
        var jobs = new List<EvalJob>();
        done.Queue(true, jobs, T0);
        jobs.Add(Job(EvalJobState.Cancelled, T0.AddMinutes(1)));
        done.Queue(false, jobs, T0.AddMinutes(2));
        Assert.IsNull(done.Last);
    }

    [TestMethod]
    public void TestsBody_SingularAndScore()
    {
        Assert.AreEqual("1 job done", Strings.DoneTestsBody(1, 0, null));
        Assert.AreEqual("3 jobs done · average score 78", Strings.DoneTestsBody(3, 0, 77.6));
    }

    [TestMethod]
    public void Alert_NewerOfCrashAndDoneWins()
    {
        var lost = new ServerLost("qwen", "http://127.0.0.1:8081", null, null, Array.Empty<string>(), "boom", T0);
        var later = new DoneEvent("tests", "t", "b", T0.AddMinutes(1));
        var earlier = new DoneEvent("bench", "t", "b", T0.AddMinutes(-1));

        Assert.IsTrue(TabStatus.Alert(lost, later)!.Id.StartsWith("tests-", StringComparison.Ordinal));
        Assert.IsTrue(TabStatus.Alert(lost, earlier)!.Id.StartsWith("lost-", StringComparison.Ordinal));
        Assert.IsTrue(TabStatus.Alert(null, earlier)!.Id.StartsWith("bench-", StringComparison.Ordinal));
        Assert.IsNull(TabStatus.Alert(null, null));
    }
}
