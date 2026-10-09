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

public class PoseTests
{
    [Fact]
    public void QuaternionRoundTripAndInterpolation()
    {
        var a = Pose3.Rotation(0.4, 1, 2, 3);
        var q = a.Quaternion();
        var b = Pose3.FromQuaternion(q.X, q.Y, q.Z, q.W, 0, 0, 0);
        Assert.Equal(a.R01, b.R01, 12);
        Assert.Equal(a.R12, b.R12, 12);
        Assert.Equal(a.R20, b.R20, 12);

        // Halfway between identity and a 90° turn about z is a 45° turn; translation halfway.
        var end = Pose3.Rotation(Math.PI / 2, 0, 0, 1) with { TxNm = 10e6 };
        var mid = Pose3.Interpolate(Pose3.Identity, end, 0.5);
        var expected = Pose3.Rotation(Math.PI / 4, 0, 0, 1);
        Assert.Equal(expected.R00, mid.R00, 12);
        Assert.Equal(expected.R10, mid.R10, 12);
        Assert.Equal(5e6, mid.TxNm, 6);
        var (x, y, _) = mid.Apply(1e6, 0, 0);
        Assert.Equal(5e6 + 1e6 * Math.Cos(Math.PI / 4), x, 3);
        Assert.Equal(1e6 * Math.Sin(Math.PI / 4), y, 3);
    }
}
