using Stykker.Shared.Net;

namespace Stykker.Shared.Tests;

// Der Netzwerk-Durchsatz aus zwei Zählerständen (NetRateMeter, für HUD und LLM). Keine echten Adapter: nur Zahlen.
public class NetRatesTests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 12, 0, 0);

    private static NetTotals T(long rx, long tx, double link = 0) => new(rx, tx, link);

    [Fact]
    public void NeedsTwoReadingsAndDividesByTheTime()
    {
        var m = new NetRateMeter();
        Assert.Null(m.Next(T(1000, 500, 1e9 / 8), T0));
        var s = m.Next(T(3000, 1500, 1e9 / 8), T0.AddSeconds(2));
        Assert.NotNull(s);
        Assert.Equal(1000, s!.RxBytesPerSec, 1e-9);
        Assert.Equal(500, s.TxBytesPerSec, 1e-9);
        Assert.Equal(1e9 / 8, s.LinkBytesPerSec, 1e-9);
    }

    [Fact]
    public void StartsOverWhenACounterGoesBack()
    {
        var m = new NetRateMeter();
        m.Next(T(10_000, 10_000), T0);
        Assert.Null(m.Next(T(50, 50), T0.AddSeconds(1)));   // Adapter weg oder Zähler neu: keine negative Rate
        Assert.Equal(100, m.Next(T(150, 150), T0.AddSeconds(2))!.RxBytesPerSec, 1e-9);
    }

    [Fact]
    public void ATooShortGapKeepsTheBaseForTheNextReading()
    {
        var m = new NetRateMeter();
        m.Next(T(0, 0), T0);
        Assert.Null(m.Next(T(10, 10), T0.AddSeconds(0.01)));   // zu kurz: keine Rate, Basis bleibt bei T0
        Assert.Equal(100, m.Next(T(100, 100), T0.AddSeconds(1))!.RxBytesPerSec, 1e-9);
    }

    [Fact]
    public void ResetMakesTheNextReadingABaseAgain()
    {
        var m = new NetRateMeter();
        m.Next(T(0, 0), T0);
        m.Reset();
        Assert.Null(m.Next(T(500, 500), T0.AddSeconds(1)));
        Assert.NotNull(m.Next(T(600, 600), T0.AddSeconds(2)));
    }

    [Fact]
    public void TheCountersOfThisMachineAreReadableAndNotNegative()
    {
        var totals = NetCounters.ReadTotals();
        if (totals is { } t)
        {
            Assert.True(t.RxBytes >= 0 && t.TxBytes >= 0 && t.LinkBytesPerSec >= 0);
        }
    }
}
