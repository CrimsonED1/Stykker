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

    private static BallStep[] MixedSteps()
    {
        var rng = new Random(42);
        double R(double a, double b) => a + rng.NextDouble() * (b - a);
        var steps = new List<BallStep>();
        var at = (X: 10.0, Y: 10.0, Z: 22.0);
        for (int i = 0; i < 300; i++)
        {
            var next = i % 7 == 0 ? (R(2, 18), R(2, 18), R(4, 16))      // a jump into the stock: buried moves
                     : (at.X + R(-3, 3), at.Y + R(-3, 3), at.Z + R(-2, 2));
            steps.Add(new BallStep(at, next, R(0.5, 2.5)));
            at = next;
        }
        return [.. steps];
    }

    /// <summary>
    /// CUDA against the CPU reference. Without the native library or a device (CI) the test says so and passes; the
    /// unavailable path is covered by GpuZMapTests.CudaBackendReportsItsStateInsteadOfCrashing.
    /// </summary>
    [Fact]
    public void CudaAgreesWithTheCpuReference()
    {
        var cuda = new CudaBackend();
        if (!cuda.IsAvailable)
        {
            output.WriteLine($"not run: {cuda.UnavailableReason}");
            return;
        }
        var steps = MixedSteps();
        var cpu = Stock(301, k: 6);
        var gpu = Stock(301, k: 6, backend: cuda);
        cpu.ApplySteps(steps);
        gpu.ApplySteps(steps);

        // fma contraction moves a column by a few float units; only columns at a rim can then differ in their interval
        // count. Everywhere else the intervals agree to float rounding.
        int differing = 0, compared = 0;
        double worst = 0;
        int k2 = cpu.MaxIntervals * 2;
        for (long c = 0; c < cpu.Counts.Length; c++)
        {
            if (cpu.Counts[c] != gpu.Counts[c]) { differing++; continue; }
            for (int q = 0; q < 2 * cpu.Counts[c]; q++)
                worst = Math.Max(worst, Math.Abs(cpu.Intervals[c * k2 + q] - gpu.Intervals[c * k2 + q]));
            compared++;
        }
        output.WriteLine($"{compared} columns compared, worst {worst:E2} mm, {differing} differ in count; " +
                         $"volume cpu {cpu.RemovedVolumeMm3:F6} cuda {gpu.BackendRemovedVolumeMm3:F6}; overflows {cpu.Overflows} / {gpu.Overflows}");
        Assert.True(worst < 1e-4, $"intervals differ by {worst:E2} mm");
        Assert.True(differing <= cpu.Counts.Length / 10_000, $"{differing} columns differ in their interval count");
        Assert.Equal(cpu.RemovedVolumeMm3, gpu.BackendRemovedVolumeMm3, 1e-4 * cpu.RemovedVolumeMm3);
        // The device multiplies by the float cell sizes (20/301 mm is not a float), the host by the double ones.
        Assert.Equal(gpu.RemovedVolumeMm3, gpu.BackendRemovedVolumeMm3, 1e-6 * gpu.RemovedVolumeMm3);
        Assert.Equal(cpu.Overflows, gpu.Overflows);
    }

    /// <summary>
    /// The binned launch against the unbinned one, bit for bit. The binning only decides which steps a block looks at,
    /// so it cannot change the result; this is what says so, and it is the check the speed-up rests on.
    /// </summary>
    [Fact]
    public void BinnedLaunchGivesTheSameBitsAsTheUnbinnedOne()
    {
        var binned = new CudaBackend { BinSteps = true };
        var plain = new CudaBackend { BinSteps = false };
        if (!binned.IsAvailable)
        {
            output.WriteLine($"not run: {binned.UnavailableReason}");
            return;
        }
        var steps = MixedSteps();
        long overflows = 0;
        foreach (int cells in new[] { 16, 32, 64, 301 })
        {
            var a = Stock(cells, k: 6, backend: binned);
            var b = Stock(cells, k: 6, backend: plain);
            a.ApplySteps(steps);
            b.ApplySteps(steps);
            int k2 = a.MaxIntervals * 2, bad = 0;
            double worst = 0;
            for (long c = 0; c < a.Counts.Length; c++)
            {
                if (a.Counts[c] != b.Counts[c]) { bad++; continue; }
                for (int q = 0; q < 2 * a.Counts[c]; q++)
                    worst = Math.Max(worst, Math.Abs(a.Intervals[c * k2 + q] - b.Intervals[c * k2 + q]));
            }
            output.WriteLine($"{cells,3} cells: {bad} columns differ in count, worst {worst:E2} mm, " +
                             $"volume {a.RemovedVolumeMm3:F4} against {b.RemovedVolumeMm3:F4}, " +
                             $"overflows {a.Overflows} / {b.Overflows}, bin {a.LastTiming.BinMs:F3} ms");

            Assert.Equal(0, bad);
            Assert.Equal(0, worst);
            Assert.Equal(b.Overflows, a.Overflows);
            overflows += a.Overflows;
        }

        // "overflows included" is worth nothing unless the capacity guard actually fires: 0 == 0 would
        // pass with the guard never reached, and the order the cuts arrive in would then never have been
        // observed to matter. If this fails, the scene does not overflow and the claim in
        // docs/long-programs.md is what needs correcting, not the test.
        Assert.True(overflows > 0, $"no column overflowed at any of the four sizes ({overflows} in total), " +
            "so this test does not exercise the capacity guard");
    }

    [Fact]
    public void CudaChunksGiveTheSameBitsAsOneBatch()
    {
        var cuda = new CudaBackend();
        if (!cuda.IsAvailable)
        {
            output.WriteLine($"not run: {cuda.UnavailableReason}");
            return;
        }
        var steps = MixedSteps();
        var one = Stock(257, backend: cuda);
        var chunks = Stock(257, backend: cuda);
        one.ApplySteps(steps);
        for (int i = 0; i < steps.Length; i += 37)
            chunks.ApplySteps(steps.AsSpan(i, Math.Min(37, steps.Length - i)), ZMapReadBack.Never);
        chunks.ReadBack();
        int k2 = one.MaxIntervals * 2, bad = 0;
        for (long c = 0; c < one.Counts.Length; c++)
        {
            bool same = one.Counts[c] == chunks.Counts[c];
            for (int q = 0; same && q < 2 * one.Counts[c]; q++) same = one.Intervals[c * k2 + q] == chunks.Intervals[c * k2 + q];
            if (!same && bad++ < 3)
                output.WriteLine($"column {c}: one {one.Counts[c]} [{string.Join(", ", one.Intervals.Skip((int)(c * k2)).Take(2 * one.Counts[c]))}] " +
                                 $"chunks {chunks.Counts[c]} [{string.Join(", ", chunks.Intervals.Skip((int)(c * k2)).Take(2 * chunks.Counts[c]))}]");
        }
        // Only the intervals in use are compared: the slots behind a column's last interval keep whatever an earlier
        // state left there, and that differs between one batch and several.
        Assert.Equal(0, bad);
        Assert.Equal(one.Overflows, chunks.Overflows);
    }
}

