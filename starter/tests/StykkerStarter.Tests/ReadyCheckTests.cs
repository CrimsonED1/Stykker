using StykkerStarter.Core.Ready;

namespace StykkerStarter.Tests;

// „Läuft heißt bereit“: welche Eingaben gelten, und wie der Zustand sich mit den Prüfungen ändert. Die Prüfung ist ein Fake.
public class ReadyCheckTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    // Ein Fake: liefert der Reihe nach die Ergebnisse; danach immer das letzte
    private sealed class FakeProbe(params bool[] results) : IReadyProbe
    {
        private int _next;
        public int Calls { get; private set; }

        public bool IsReady(ReadyTarget target, TimeSpan timeout)
        {
            Calls++;
            Assert.Equal(ReadyTracker.ProbeTimeout, timeout);
            var value = results[Math.Min(_next, results.Length - 1)];
            _next++;
            return value;
        }
    }

    private sealed class ThrowingProbe : IReadyProbe
    {
        public bool IsReady(ReadyTarget target, TimeSpan timeout) => throw new IOException("the probe broke");
    }

    [Theory]
    [InlineData("8080", true)]
    [InlineData(" 5001 ", true)]
    [InlineData("0", false)]
    [InlineData("70000", false)]
    [InlineData("12a", false)]
    [InlineData("http://localhost:8080/health", true)]
    [InlineData("https://127.0.0.1:5001", true)]
    [InlineData("http://[::1]:8080/", true)]
    [InlineData("HTTP://LOCALHOST:8080", true)]
    [InlineData("http://example.com:8080", false)]
    [InlineData("http://192.168.1.10:8080", false)]
    [InlineData("ftp://localhost:21", false)]
    [InlineData("localhost:8080", false)]
    public void Only_a_port_or_a_local_http_address_is_accepted(string text, bool accepted)
    {
        Assert.Equal(accepted, ReadyRule.Parse(text) != null);
        Assert.Equal(accepted, ReadyRule.Problem(text) == null);
    }

    [Fact]
    public void Empty_text_means_no_check_and_is_not_a_problem()
    {
        Assert.Null(ReadyRule.Parse(""));
        Assert.Null(ReadyRule.Parse("   "));
        Assert.Null(ReadyRule.Problem(null));
    }

    [Fact]
    public void A_port_is_checked_on_this_machine()
    {
        var target = ReadyRule.Parse("8080")!;
        Assert.False(target.IsUrl);
        Assert.Equal(8080, target.Port);
        Assert.Equal("127.0.0.1:8080", target.Describe);
    }

    [Fact]
    public void A_program_waits_until_the_check_passes_and_then_is_ready()
    {
        var tracker = new ReadyTracker();
        var target = ReadyRule.Parse("8080")!;
        tracker.Started("app", T0);
        Assert.Equal(ReadyState.Waiting, tracker.StateOf("app"));

        var probe = new FakeProbe(false, false, true);
        Assert.Equal(ReadyState.Waiting, tracker.Check("app", target, probe, T0.AddSeconds(1)));
        Assert.Equal(ReadyState.Waiting, tracker.Check("app", target, probe, T0.AddSeconds(2)));
        Assert.Equal(ReadyState.Ready, tracker.Check("app", target, probe, T0.AddSeconds(3)));
        Assert.Equal(3, probe.Calls);
    }

    [Fact]
    public void A_ready_program_that_fails_a_check_is_waiting_again_and_gets_a_new_minute()
    {
        var tracker = new ReadyTracker();
        tracker.Started("app", T0);
        tracker.Record("app", true, T0);

        Assert.Equal(ReadyState.Waiting, tracker.Record("app", false, T0.AddMinutes(5)));
        // 59 s nach dem Fehlschlag noch wartend, nicht Zeitüberschreitung
        Assert.Equal(ReadyState.Waiting, tracker.Record("app", false, T0.AddMinutes(5).AddSeconds(59)));
        Assert.Equal(ReadyState.Timeout, tracker.Record("app", false, T0.AddMinutes(6)));
    }

    [Fact]
    public void Sixty_seconds_without_success_is_a_timeout_and_a_later_success_makes_it_ready()
    {
        var tracker = new ReadyTracker();
        tracker.Started("app", T0);

        Assert.Equal(ReadyState.Waiting, tracker.Record("app", false, T0.AddSeconds(59)));
        Assert.Equal(ReadyState.Timeout, tracker.Record("app", false, T0.AddSeconds(60)));
        Assert.Equal(ReadyState.Timeout, tracker.Record("app", false, T0.AddSeconds(120)));
        Assert.Equal(ReadyState.Ready, tracker.Record("app", true, T0.AddSeconds(130)));
    }

    [Fact]
    public void A_probe_that_throws_counts_as_not_ready()
    {
        var tracker = new ReadyTracker();
        tracker.Started("app", T0);

        Assert.Equal(ReadyState.Waiting, tracker.Check("app", ReadyRule.Parse("8080")!, new ThrowingProbe(), T0.AddSeconds(1)));
    }

    [Fact]
    public void A_stopped_program_has_no_state()
    {
        var tracker = new ReadyTracker();
        tracker.Started("app", T0);
        tracker.Stopped("app");

        Assert.Equal(ReadyState.None, tracker.StateOf("app"));
    }
}
