using Stykker.NanoCut.Gpu;
using Xunit.Abstractions;

namespace Stykker.NanoCut.Tests;

/// <summary>
/// The dexel preview against analytic volumes and against the Z-map. The cases are the ones a height field cannot
/// represent: a ball buried in the stock, and the roof of material that stays above a tool whose crown is below the top.
/// </summary>
public class DexelMapTests(ITestOutputHelper output)
{
    private static DexelMap Stock(int cells, double size = 20, int k = 4, IDexelBackend? backend = null) =>
        new(0, 0, 0, size, size, size, cells, cells, k, backend);

    [Fact]
    public void UntouchedStockKeepsItsVolume()
    {
        var map = Stock(16);
        Assert.Equal(0, map.RemovedVolumeMm3);
        Assert.Equal(8000, map.RemainingVolumeMm3, 9);
        Assert.All(map.Counts, c => Assert.Equal(1, c));
    }

    [Fact]
    public void ABallBuriedInTheStockRemovesItsOwnVolume()
    {
        // The case a Z-map cannot do: it would cut every column down to the ball's bottom.
        var map = Stock(400);
        map.ApplySteps([BallStep.At((10, 10, 10), 3)]);
        double expected = 4.0 / 3 * Math.PI * 27;
        output.WriteLine($"removed {map.RemovedVolumeMm3:F6} mm³, sphere {expected:F6} mm³");
        Assert.True(Math.Abs(map.RemovedVolumeMm3 - expected) < 0.003 * expected);
        Assert.Equal(0, map.Overflows);
        Assert.All(map.TopHeights(), h => Assert.Equal(20f, h));   // the stock top is untouched
        Assert.Equal(2, map.Counts.Max());
    }

    [Fact]
    public void ABuriedCapsuleRemovesCylinderAndSphere()
    {
        var map = Stock(500);
        map.ApplySteps([new BallStep((5, 10, 10), (15, 7, 12), 2.5)]);
        double len = Math.Sqrt(100 + 9 + 4), r = 2.5;
        double expected = Math.PI * r * r * len + 4.0 / 3 * Math.PI * r * r * r;
        output.WriteLine($"removed {map.RemovedVolumeMm3:F6} mm³, capsule {expected:F6} mm³");
        Assert.True(Math.Abs(map.RemovedVolumeMm3 - expected) < 0.003 * expected);
    }

    [Fact]
    public void TheRoofAboveAShallowGrooveStays()
    {
        // Centre 1.5 mm below the top, r = 3: at lateral distance d the ball's crown is 18.5 + sqrt(9 - d²), below the top
        // 20 for d > sqrt(6.75) = 2.6 mm. There a column keeps a roof [crown, 20] over the cavity.
        var map = new DexelMap(0, 0, 0, 40, 20, 20, 400, 200);
        map.ApplySteps([new BallStep((5, 10, 18.5), (35, 10, 18.5), 3)]);
        int k2 = map.MaxIntervals * 2, roofs = 0;
        for (int j = 0; j < map.CellsY; j++)
        {
            int i = 200;
            double y = (j + 0.5) * map.CellSizeYMm, d = Math.Abs(y - 10);
            long c = (long)j * map.CellsX + i;
            int n = map.Counts[c];
            if (d >= 3) { Assert.Equal(1, n); continue; }
            double bottom = 18.5 - Math.Sqrt(9 - d * d), crown = 18.5 + Math.Sqrt(9 - d * d);
            if (crown < 20 - 1e-3)
            {
                Assert.Equal(2, n);
                Assert.Equal(bottom, map.Intervals[c * k2 + 1], 1e-5);
                Assert.Equal(crown, map.Intervals[c * k2 + 2], 1e-5);
                Assert.Equal(20f, map.Intervals[c * k2 + 3]);
                roofs++;
            }
        }
        Assert.True(roofs > 4, $"only {roofs} roof columns");
    }

    [Fact]
    public void WithOneIntervalPerColumnItIsTheZMap()
    {
        // K = 1 has no room for a roof, so every split cuts through to the top: exactly the height-field model.
        var steps = new[] { new BallStep((2, 3, 18), (17, 14, 16.5), 2.2), new BallStep((17, 14, 16.5), (4, 15, 19), 1.4), BallStep.At((10, 10, 12), 3) };
        var dexel = Stock(97, k: 1);
        var zmap = new ZMap(0, 0, 0, 20, 20, 20, 97, 97);
        dexel.ApplySteps(steps);
        zmap.ApplySteps(steps);
        Assert.True(dexel.Overflows > 0);
        Assert.Equal(zmap.Heights, dexel.TopHeights());
        Assert.Equal(zmap.RemovedVolumeMm3, dexel.RemovedVolumeMm3, 6);
    }

    [Fact]
    public void ThreadCountDoesNotMatter()
    {
        var steps = new[] { new BallStep((1, 1, 19), (19, 18, 15), 2), BallStep.At((10, 10, 8), 4), new BallStep((19, 2, 12), (1, 17, 12), 1.5) };
        var a = Stock(123, backend: new CpuBackend(1));
        var b = Stock(123, backend: new CpuBackend(16));
        a.ApplySteps(steps);
        b.ApplySteps(steps);
        Assert.Equal(a.Counts, b.Counts);
        Assert.Equal(a.Intervals, b.Intervals);
        Assert.Equal(a.Overflows, b.Overflows);
    }

    /// <summary>The top of the swept ball on long ramps, against the double reference (mirror image of the bottom).</summary>
    [Theory]
    [InlineData(100, 1, 0)]
    [InlineData(50, 0.1, 0.3)]
    [InlineData(1000, 3, 0.05)]
    [InlineData(1000, 3, -0.3)]
    public void SpanMatchesTheExactInterval(double length, double radius, double slope)
    {
        var (step, origin) = GpuZMapTests.LongRamp(length, radius, slope);
        float[] packed = ToolProfile.Pack([step], origin);
        var mirrored = new BallStep((step.From.X, step.From.Y, -step.From.Z), (step.To.X, step.To.Y, -step.To.Z), step.RadiusMm);
        var (ux, uy) = (Math.Cos(0.37), Math.Sin(0.37));
        int compared = 0;
        for (int k = 0; k <= 200; k++)
        {
            double along = -1.5 * radius + (length + 3 * radius) * k / 200.0;
            for (int m = -24; m <= 24; m++)
            {
                double across = radius * m / 20.0;
                double x = step.From.X + along * ux - across * uy, y = step.From.Y + along * uy + across * ux;
                float fx = (float)(x - origin.X), fy = (float)(y - origin.Y);
                double cx = origin.X + fx, cy = origin.Y + fy;
                double gap = radius - GpuZMapTests.HorizontalDistance(cx, cy, step);
                double delta = 4 * 6e-8 * (length + Math.Abs(fx) + Math.Abs(fy) + 1);
                if (Math.Abs(gap) < 4 * delta) continue;
                double low = GpuZMapTests.ExactBottom(cx, cy, step), high = -GpuZMapTests.ExactBottom(cx, cy, mirrored);
                bool hit = ToolProfile.Span(fx, fy, packed, 0, out float lo, out float hi);
                Assert.Equal(!double.IsPositiveInfinity(low), hit);
                if (!hit) continue;
                double tolerance = 2e-6 + delta * (1 + Math.Sqrt(radius / (2 * gap)));
                Assert.True(Math.Abs(lo - low) <= tolerance, $"low {lo} vs {low} at ({cx}, {cy})");
                Assert.True(Math.Abs(hi - high) <= tolerance, $"high {hi} vs {high} at ({cx}, {cy})");
                compared++;
            }
        }
        Assert.True(compared > 2000, $"only {compared} columns");
    }
}
