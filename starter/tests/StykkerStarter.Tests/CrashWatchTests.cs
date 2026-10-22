using StykkerStarter.Core.Crash;

namespace StykkerStarter.Tests;

// Die Entscheidung über Abstürze und Neustarte. Die Zeit ist vorgegeben, nichts wartet wirklich.
public class CrashWatchTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    private static CrashDecision Observe(CrashWatch w, DateTime at, bool running, bool restartOnCrash = true) =>
        w.Observe("app", running, startWithStarter: true, restartOnCrash, at);

    [Fact]
    public void A_program_that_ends_without_a_reason_is_restarted_after_five_seconds()
    {
        var w = new CrashWatch();
        w.Started("app", manual: false);
        Assert.Equal(CrashAction.None, Observe(w, T0, running: true).Action);

        var pending = Observe(w, T0.AddSeconds(1), running: false);
        Assert.Equal(CrashAction.RestartPending, pending.Action);
        Assert.Equal(TimeSpan.FromSeconds(5), pending.Wait);
        Assert.Equal("restarting", w.StateOf("app"));

        Assert.Equal(CrashAction.None, Observe(w, T0.AddSeconds(3), running: false).Action);
        Assert.Equal(CrashAction.Restart, Observe(w, T0.AddSeconds(6), running: false).Action);

        Assert.Equal(CrashAction.None, Observe(w, T0.AddSeconds(7), running: true).Action);
        Assert.Null(w.StateOf("app"));
    }

    [Fact]
    public void Without_restart_on_crash_the_state_says_crashed_and_nothing_restarts()
    {
        var w = new CrashWatch();
        w.Started("app", manual: false);

        Assert.Equal(CrashAction.Crashed, Observe(w, T0, running: false, restartOnCrash: false).Action);
        Assert.Equal("crashed", w.StateOf("app"));
        Assert.Equal(CrashAction.None, Observe(w, T0.AddMinutes(1), running: false, restartOnCrash: false).Action);
        Assert.Equal("crashed", w.StateOf("app"));
    }

    [Fact]
    public void A_stop_the_starter_or_the_user_asked_for_is_not_a_crash()
    {
        var w = new CrashWatch();
        w.Started("app", manual: false);
        w.Intend("app");

        Assert.Equal(CrashAction.None, Observe(w, T0, running: false).Action);
        Assert.Null(w.StateOf("app"));
    }

    [Fact]
    public void A_program_that_already_runs_and_starts_with_the_starter_counts_as_expected()
    {
        var w = new CrashWatch();
        Assert.Equal(CrashAction.None, Observe(w, T0, running: true).Action);   // schon beim Start da

        Assert.Equal(CrashAction.RestartPending, Observe(w, T0.AddSeconds(1), running: false).Action);
    }

    [Fact]
    public void A_program_that_was_started_by_hand_and_does_not_start_with_the_starter_is_not_watched()
    {
        var w = new CrashWatch();
        w.Observe("app", running: true, startWithStarter: false, restartOnCrash: true, now: T0);

        Assert.Equal(CrashAction.None, w.Observe("app", running: false, startWithStarter: false, restartOnCrash: true, now: T0.AddSeconds(1)).Action);
        Assert.Null(w.StateOf("app"));
    }

    [Fact]
    public void Pauses_grow_five_fifteen_and_sixty_seconds_and_a_failed_restart_counts()
    {
        var w = new CrashWatch();
        w.Started("app", manual: false);
        var now = T0;

        Assert.Equal(TimeSpan.FromSeconds(5), Observe(w, now, running: false).Wait);   // erster Absturz
        now = now.AddSeconds(5);
        Assert.Equal(CrashAction.Restart, Observe(w, now, running: false).Action);     // Neustart 1, startet nicht

        var second = Observe(w, now, running: false);                                 // der Start ist gescheitert
        Assert.Equal(CrashAction.RestartPending, second.Action);
        Assert.Equal(TimeSpan.FromSeconds(15), second.Wait);
        now = now.AddSeconds(15);
        Assert.Equal(CrashAction.Restart, Observe(w, now, running: false).Action);     // Neustart 2

        Assert.Equal(TimeSpan.FromSeconds(60), Observe(w, now, running: false).Wait);
        now = now.AddSeconds(60);
        Assert.Equal(CrashAction.Restart, Observe(w, now, running: false).Action);     // Neustart 3
        Assert.Equal(3, w.RestartsIn("app", now));
    }

    [Fact]
    public void A_sixth_restart_within_ten_minutes_gives_up()
    {
        var w = new CrashWatch();
        w.Started("app", manual: false);
        var now = T0;
        for (int i = 0; i < CrashWatch.MaxRestarts; i++)
        {
            Observe(w, now, running: false);                               // Absturz, Neustart vorgemerkt
            now = now.AddSeconds(60);
            Assert.Equal(CrashAction.Restart, Observe(w, now, running: false).Action);
            now = now.AddSeconds(1);
        }
        Assert.Equal(CrashWatch.MaxRestarts, w.RestartsIn("app", now));

        var last = Observe(w, now, running: false);
        Assert.Equal(CrashAction.GaveUp, last.Action);
        Assert.Equal("gaveUp", w.StateOf("app"));
        Assert.Equal(CrashAction.None, Observe(w, now.AddMinutes(1), running: false).Action);   // kein weiterer Versuch
    }

    [Fact]
    public void Restarts_older_than_ten_minutes_no_longer_count()
    {
        var w = new CrashWatch();
        w.Started("app", manual: false);
        var now = T0;
        for (int i = 0; i < CrashWatch.MaxRestarts; i++)
        {
            Observe(w, now, running: false);
            now = now.AddSeconds(60);
            Observe(w, now, running: false);
            now = now.AddSeconds(1);
        }

        var later = T0.AddMinutes(16);   // alle fünf Neustarte liegen bis fünf Minuten nach T0: nun älter als zehn Minuten
        Assert.Equal(0, w.RestartsIn("app", later));
        var decision = Observe(w, later, running: false);
        Assert.Equal(CrashAction.RestartPending, decision.Action);
        Assert.Equal(TimeSpan.FromSeconds(5), decision.Wait);
    }

    [Fact]
    public void A_manual_start_clears_the_counter_and_the_give_up()
    {
        var w = new CrashWatch();
        w.Started("app", manual: false);
        var now = T0;
        for (int i = 0; i <= CrashWatch.MaxRestarts; i++)
        {
            Observe(w, now, running: false);
            now = now.AddSeconds(60);
            Observe(w, now, running: false);
            now = now.AddSeconds(1);
        }
        Assert.Equal("gaveUp", w.StateOf("app"));

        w.Started("app", manual: true);
        Assert.Null(w.StateOf("app"));
        Assert.Equal(0, w.RestartsIn("app", now));
    }

    [Fact]
    public void An_automatic_start_by_the_starter_keeps_the_counter()
    {
        var w = new CrashWatch();
        w.Started("app", manual: false);
        Observe(w, T0, running: false);                                  // Neustart vorgemerkt
        Assert.Equal(CrashAction.Restart, Observe(w, T0.AddSeconds(5), running: false).Action);

        w.Started("app", manual: false);                                 // der Starter startet es, kein Handauftrag

        Assert.Equal(1, w.RestartsIn("app", T0.AddSeconds(5)));
        Assert.Null(w.StateOf("app"));
        Assert.Equal(CrashAction.None, Observe(w, T0.AddSeconds(6), running: true).Action);
    }
}
