namespace Stykker.NanoCut.Tests;

public class CoreTests
{
    [Theory]
    [InlineData(50.0, 2222)] // plan: 2,222 segments at r = 50 mm, s = 50 nm
    [InlineData(3.0, 545)]   // plan: 545 segments at r = 3 mm
    public void SegmentCountMatchesPlan(double radiusMm, int expected)
    {
        int n = Discretization.SegmentCount(radiusMm * Units.NmPerMm, 50);
        Assert.Equal(expected, n);
        Assert.True(Discretization.Sagitta(radiusMm * Units.NmPerMm, 2 * Math.PI / n) <= 50);
        Assert.True(Discretization.Sagitta(radiusMm * Units.NmPerMm, 2 * Math.PI / (n - 1)) > 50);
    }

    [Fact]
    public void ToleranceBudgetSplitsAsPlanned()
    {
        var tol = Tolerance.Budget(totalUm: 0.1, chordNm: 50);
        Assert.Equal(100, tol.TotalNm, 9);
        Assert.Equal(50, tol.ChordNm);
        Assert.Equal(30, tol.SweepNm);
        Assert.Equal(19.5, tol.ReserveNm, 9);
        Assert.Throws<ArgumentException>(() => Tolerance.Budget(totalUm: 0.05, chordNm: 50));
    }

    [Fact]
    public void MillimetreConversionRoundsToNanometre()
    {
        Assert.Equal(new Vec2(1_000_000, -2_500_000), Vec2.Mm(1, -2.5));
        Assert.Equal(1, Units.MmToNm(0.0000006));
        Assert.Equal(-1, Units.MmToNm(-0.0000006));
        Assert.Equal(Units.MaxCoordinate, Units.MmToNm(2147.483648));
        Assert.Throws<ArgumentOutOfRangeException>(() => Units.MmToNm(2147.483649));
        Assert.Throws<ArgumentOutOfRangeException>(() => Units.MmToNm(double.NaN));
    }
}
