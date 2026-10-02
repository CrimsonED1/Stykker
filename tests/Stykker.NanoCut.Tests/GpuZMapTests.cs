using Stykker.NanoCut.Geometry3D;
using Stykker.NanoCut.Gpu;
using Xunit.Abstractions;

namespace Stykker.NanoCut.Tests;

/// <summary>
/// The Z-map preview against analytic volumes. Every case is chosen so that the tool reaches the column from above
/// (its top is above the stock top wherever it cuts), because that is the situation a Z-map models; a tool that is
/// buried in the stock is a known limitation and is tested as such.
/// </summary>
public class GpuZMapTests(ITestOutputHelper output)
{
    /// <summary>Stock 0..sizeX × 0..sizeY × 0..sizeZ, so relative and absolute mm are the same.</summary>
    private static ZMap Stock(int cellsX, int cellsY, double sizeX = 10, double sizeY = 10, double sizeZ = 10,
        IZMapBackend? backend = null) =>
        new(0, 0, 0, sizeX, sizeY, sizeZ, cellsX, cellsY, backend);

    private static readonly (double X, double Y, double Z) Zero = (0, 0, 0);

    [Fact]
    public void UntouchedStockKeepsItsVolume()
    {
        var map = Stock(16, 12);
        Assert.Equal(1000, map.BoxVolumeMm3, 9);
        Assert.Equal(0, map.RemovedVolumeMm3, 9);
        Assert.Equal(1000, map.RemainingVolumeMm3, 9);

        map.ApplySteps(Array.Empty<BallStep>());
        Assert.Equal(0, map.AppliedSteps);
        Assert.All(map.Heights, h => Assert.Equal(10f, h));
    }

    [Fact]
    public void ACutThroughTheWholeStockStopsAtTheBottom()
    {
        // A ball of r = 100 centred in the middle of a 5 mm plate spans the whole plate, so all of it goes.
        var map = Stock(8, 8, sizeZ: 5);
        map.ApplySteps([BallStep.At((5, 5, 2.5), 100)]);

        Assert.All(map.Heights, h => Assert.Equal(0f, h));
        Assert.Equal(500, map.RemovedVolumeMm3, 9);
        Assert.Equal(0, map.RemainingVolumeMm3, 9);
    }

    [Fact]
    public void AToolBuriedInTheStockIsTheKnownZMapLimit()
    {
        // A ball of r = 100 centred 100 mm below the plate only touches z = 0, so physically nothing is removed.
        // A Z-map tracks the top surface only and cannot see that the tool never reached the column from above: it
        // lowers every column the tool touches to the bottom. That is the standard convention, and the reason the
        // preview is not used for toolpaths that dive under the surface.
        var map = Stock(8, 8, sizeZ: 5);
        map.ApplySteps([BallStep.At((5, 5, -100), 100)]);

        Assert.All(map.Heights, h => Assert.Equal(0f, h));
        Assert.Equal(500, map.RemovedVolumeMm3, 9);
    }

    [Fact]
    public void DwellHeightsAreTheAnalyticBallBottom()
    {
        const int n = 32;
        double cell = 10.0 / n;
        var map = Stock(n, n);
        map.ApplySteps([BallStep.At((5, 5, 10), 3)]);

        int checkedCells = 0;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double x = (i + 0.5) * cell, y = (j + 0.5) * cell;
                double d = Math.Sqrt((x - 5) * (x - 5) + (y - 5) * (y - 5));
                float h = map.Heights[j * n + i];
                if (d > 3 + 1e-3)
                {
                    Assert.Equal(10f, h);
                }
                else if (d < 3 - 1e-3)
                {
                    // 10 nm is about ten float32 units at this magnitude.
                    double expected = 10 - Math.Sqrt(9 - d * d);
                    Assert.True(Math.Abs(expected - h) < 1e-5, $"cell ({i}, {j}): {h:R} mm instead of {expected:R} mm");
                    checkedCells++;
                }
            }
        }
        Assert.True(checkedCells > 200, $"only {checkedCells} cells were inside the tool");
    }

    [Fact]
    public void DwellVolumeConvergesToTheAnalyticHemisphere()
    {
        // A ball of r = 3 centred in the stock top leaves a hemisphere: (2/3)·pi·r³ = 18·pi.
        double expected = 18 * Math.PI;
        double coarse = Error(32), fine = Error(256);

        Assert.True(fine < coarse, $"256 cells ({fine:E2}) should beat 32 cells ({coarse:E2})");
        Assert.True(fine < 0.002, $"256 cells should be within 0.2 % of 18 pi, was {fine:E2}");

        double Error(int cells)
        {
            var map = Stock(cells, cells);
            map.ApplySteps([BallStep.At((5, 5, 10), 3)]);
            return Math.Abs(map.RemovedVolumeMm3 - expected) / expected;
        }
    }

    [Fact]
    public void HorizontalStepHeightsAreTheAnalyticCapsuleBottom()
    {
        const int n = 40;
        double cell = 10.0 / n;
        var map = Stock(n, n);
        map.ApplySteps([new BallStep((2, 5, 10), (8, 5, 10), 3)]);

        int checkedCells = 0;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double x = (i + 0.5) * cell, y = (j + 0.5) * cell;
                double dx = Math.Clamp(x, 2, 8) - x, dy = 5 - y;   // horizontal offset to the closest segment point
                double d = Math.Sqrt(dx * dx + dy * dy);
                float h = map.Heights[j * n + i];
                if (d > 3 + 1e-3)
                {
                    Assert.Equal(10f, h);
                }
                else if (d < 3 - 1e-3)
                {
                    double expected = 10 - Math.Sqrt(9 - d * d);
                    Assert.True(Math.Abs(expected - h) < 1e-5, $"cell ({i}, {j}): {h:R} mm instead of {expected:R} mm");
                    checkedCells++;
                }
            }
        }
        Assert.True(checkedCells > 200, $"only {checkedCells} cells were inside the tool");
    }

    [Fact]
    public void RampedStepMatchesDenseSamplingOfTheSegment()
    {
        // A step that rises while it moves: the lowest point of the swept ball is then not above the closest point
        // of the segment, so the closed form minimum has to be right.
        var step = new BallStep((1, 2, 9), (6, -1, 12), 3);
        float[] packed = ToolProfile.Pack([step], Zero);

        int compared = 0;
        for (int i = 0; i < 16; i++)
        {
            for (int j = 0; j < 16; j++)
            {
                float x = -1.5f + i * 0.55f, y = -1.5f + j * 0.55f;
                if (Math.Abs(HorizontalDistance(x, y, step) - step.RadiusMm) < 0.05) continue;   // steep boundary

                double sampled = SampleSegment(x, y, step, 400_000);
                float actual = ToolProfile.Bottom(x, y, packed, 0);
                if (double.IsPositiveInfinity(sampled))
                {
                    Assert.True(float.IsPositiveInfinity(actual),
                        $"at ({x}, {y}) the segment is out of reach but the bottom was {actual}");
                    continue;
                }
                Assert.False(float.IsPositiveInfinity(actual), $"at ({x}, {y}) the segment is in reach");
                Assert.Equal(sampled, actual, 3);
                compared++;
            }
        }
        Assert.True(compared > 100, $"only {compared} columns were compared");
    }

    /// <summary>
    /// Regression: for a horizontal step the discriminant of the stationary point is zero in exact arithmetic, but
    /// it is computed as the difference of two products of size 4·d²·w2², so in float32 it comes out slightly
    /// negative about half the time and the whole step used to be dropped for that cell. The longer the step (that
    /// is the larger w2 and d), the larger those products and the more certain the cancellation.
    /// </summary>
    [Fact]
    public void LongHorizontalStepIsNotDroppedByFloatCancellation()
    {
        // A 60 mm long capsule of r = 3 lying exactly in the top face of an 80 x 60 x 20 block: half of it is in the
        // stock, so the removed volume is (pi r² L + 4/3 pi r³) / 2.
        var map = new ZMap(0, 0, 0, 80, 60, 20, 320, 240);
        map.ApplySteps([new BallStep((10, 30, 20), (70, 30, 20), 3)]);

        double expected = (Math.PI * 9 * 60 + 4.0 / 3 * Math.PI * 27) / 2;
        Assert.True(Math.Abs(map.RemovedVolumeMm3 - expected) < 0.02 * expected,
            $"removed {map.RemovedVolumeMm3:F6} mm³, expected about {expected:F6} mm³");

        // The deepest cells sit on the axis y = 30, which the grid straddles by 0.125 mm.
        double expectedDepth = 20 - Math.Sqrt(9 - 0.125 * 0.125);
        Assert.True(Math.Abs(map.Heights.Min() - expectedDepth) < 1e-3,
            $"the deepest cell is at {map.Heights.Min():F6} mm, expected {expectedDepth:F6} mm");
    }

    [Fact]
    public void HeightsNeverRiseAndThreadCountDoesNotMatter()
    {
        BallStep[] steps =
        [
            new((1, 1, 10), (9, 1, 10), 2),   // long horizontal step: the float cancellation case
            new((2, 3, 10), (7, 3, 10), 2),
            new((7, 3, 10), (7, 8, 9), 2),    // ramp, so the lowest point is not above the closest one
            new((7, 8, 9), (2, 8, 9), 2),
            BallStep.At((4, 5, 10), 1.5),
        ];

        var one = Stock(48, 48, backend: new CpuBackend(1));
        var all = Stock(48, 48, backend: new CpuBackend());
        one.ApplySteps(steps);
        for (int i = 0; i < steps.Length; i++)
        {
            float[] before = (float[])all.Heights.Clone();
            all.ApplySteps(steps.AsSpan(i, 1));
            for (int k = 0; k < before.Length; k++)
                Assert.True(all.Heights[k] <= before[k], $"cell {k} rose from {before[k]} to {all.Heights[k]}");
        }

        Assert.Equal(one.Heights, all.Heights);
        Assert.Equal(one.RemovedVolumeMm3, all.RemovedVolumeMm3, 9);
        Assert.Equal(steps.Length, all.AppliedSteps);
    }

    /// <summary>
    /// The preview against the exact kernel, which is the check that matters: both should give the analytic 63 pi.
    /// The exact capsule is tessellated to a 0.5 µm chord error, about a thousandth of the removed volume, so the
    /// comparison stays meaningful without the seconds a 50 nm tessellation costs.
    /// </summary>
    [Fact]
    public void PreviewAgreesWithTheExactKernelOnAGroove()
    {
        // A horizontal capsule in the top face of a 20 x 20 x 10 block, r = 3, 10 mm long: half of it lies in the
        // stock, so the removed volume is (pi r² L + 4/3 pi r³) / 2 = 63 pi.
        double expected = 63 * Math.PI;
        var stock = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(20, 20, 10));
        var tool = Solid.Capsule(Vec3.Mm(5, 10, 10), Vec3.Mm(15, 10, 10), 3, Tolerance.Budget(totalUm: 1.0, chordNm: 500));
        double exactRemoved = 4000 - (stock - tool).VolumeMm3;

        var map = new ZMap(0, 0, 0, 20, 20, 10, 512, 512);
        map.ApplySteps([new BallStep((5, 10, 10), (15, 10, 10), 3)]);

        Assert.True(Math.Abs(exactRemoved - expected) < 2e-3 * expected,
            $"the exact kernel removed {exactRemoved:F6} mm³, expected {expected:F6}");
        Assert.True(Math.Abs(map.RemovedVolumeMm3 - expected) < 5e-3 * expected,
            $"the preview removed {map.RemovedVolumeMm3:F6} mm³, expected {expected:F6}");
        Assert.True(Math.Abs(map.RemovedVolumeMm3 - exactRemoved) < 5e-3 * exactRemoved,
            $"preview {map.RemovedVolumeMm3:F6} mm³ against exact {exactRemoved:F6} mm³");
        output.WriteLine($"63 pi = {expected:F6} mm³, exact kernel {exactRemoved:F6} mm³ " +
                         $"({(exactRemoved - expected) / expected:P4}), preview 512x512 {map.RemovedVolumeMm3:F6} mm³ " +
                         $"({(map.RemovedVolumeMm3 - expected) / expected:P4})");
    }

    [Fact]
    public void MeshMatchesTheHeightField()
    {
        const int nx = 17, ny = 13;
        var map = Stock(nx, ny);
        map.ApplySteps([new BallStep((2, 2, 10), (8, 7, 9), 3)]);
        var mesh = map.ToMesh();

        Assert.Equal(nx * ny, mesh.VertexCount);
        Assert.Equal((nx - 1) * (ny - 1) * 2, mesh.TriangleCount);
        Assert.Equal(Zero, mesh.OriginMm);
        for (int v = 0; v < mesh.VertexCount; v++)
            Assert.Equal(map.Heights[v], mesh.Positions[3 * v + 2]);

        for (int v = 0; v < mesh.VertexCount; v++)
        {
            double len = Math.Sqrt(mesh.Normals[3 * v] * mesh.Normals[3 * v]
                + mesh.Normals[3 * v + 1] * mesh.Normals[3 * v + 1]
                + mesh.Normals[3 * v + 2] * mesh.Normals[3 * v + 2]);
            Assert.Equal(1, len, 5);
            Assert.True(mesh.Normals[3 * v + 2] > 0, "the preview surface faces up");
        }
        Assert.All(mesh.Indices, i => Assert.True(i < (uint)mesh.VertexCount));
    }

    [Fact]
    public void ExpandedSceneLoaderRebuildsTheBallSteps()
    {
        string path = WriteExpanded(
            min: [0, 0, 0], max: [10_000_000, 8_000_000, 5_000_000],
            centres: [(1_000_000, 2_000_000, 3_000_000), (4_000_000, 2_000_000, 3_000_000), (4_000_000, 6_000_000, 3_500_000)],
            radiusNm: 1_500_000, segments: 8);
        try
        {
            var scene = ExpandedScene.Load(path);
            Assert.Equal(Zero, scene.BoxMinMm);
            Assert.Equal((10d, 8d, 5d), scene.BoxMaxMm);
            Assert.Equal(400, scene.BoxVolumeMm3, 9);
            Assert.Equal(1.5, scene.RadiusMm, 9);
            Assert.Equal(2, scene.Steps.Count);
            Assert.Equal(2 * (2 + 3 * 8), scene.PointsPerStep);
            Assert.Equal((1d, 2d, 3d), scene.Steps[0].From);
            Assert.Equal((4d, 2d, 3d), scene.Steps[0].To);
            Assert.Equal((4d, 6d, 3.5), scene.Steps[1].To);
            Assert.Equal(1.5, scene.Steps[1].RadiusMm, 9);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExpandedSceneLoaderRejectsPointsThatAreNotBalls()
    {
        // The poles of the first ball are not on a vertical line, so the points cannot come from bench/run.py.
        string path = Path.Combine(Path.GetTempPath(), $"nanocut-not-a-ball-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {"box": {"min": [0, 0, 0], "max": [1000000, 1000000, 1000000]},
             "steps": [[[0, 0, 100], [1, 0, -100], [0, 0, 0], [0, 0, 0]]],
             "save": [-1]}
            """);
        try
        {
            Assert.Throws<FormatException>(() => ExpandedScene.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CudaBackendReportsItsStateInsteadOfCrashing()
    {
        var cuda = new CudaBackend();
        Assert.Equal(CudaRuntime.IsAvailable, cuda.IsAvailable);
        if (cuda.IsAvailable)
        {
            Assert.Null(cuda.UnavailableReason);
            Assert.NotEmpty(CudaRuntime.Devices);
            Assert.True(CudaRuntime.Devices[0].MemoryBytes > 0);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(cuda.UnavailableReason));
            Assert.Throws<GpuNativeException>(() => cuda.Apply(Stock(4, 4), [BallStep.At((2, 2, 10), 1)]));
        }
    }

    /// <summary>
    /// CUDA against the CPU reference. xunit 2.9 has no dynamic skip, so on a machine without the native library or
    /// without a device (that is in CI) the test reports why it did nothing and passes; the unavailable path itself
    /// is asserted in <see cref="CudaBackendReportsItsStateInsteadOfCrashing"/>.
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

        BallStep[] steps =
        [
            new((1, 1, 10), (9, 1, 10), 2),   // long horizontal step: the float cancellation case
            new((2, 3, 10), (7, 3, 10), 2),
            new((7, 3, 10), (7, 8, 9), 2),    // ramp, so the lowest point is not above the closest one
            new((7, 8, 9), (2, 8, 9), 2),
            BallStep.At((4, 5, 10), 1.5),
        ];

        var cpu = Stock(64, 64, backend: new CpuBackend(1));
        var gpu = Stock(64, 64, backend: cuda);

        // One step at a time, so a divergence says which step caused it.
        float worst = 0;
        for (int s = 0; s < steps.Length; s++)
        {
            cpu.ApplySteps(steps.AsSpan(s, 1));
            gpu.ApplySteps(steps.AsSpan(s, 1));
            for (int k = 0; k < cpu.Heights.Length; k++)
            {
                float difference = Math.Abs(cpu.Heights[k] - gpu.Heights[k]);
                if (difference <= worst) continue;
                worst = difference;
                output.WriteLine($"step {s} cell {k % 64},{k / 64} " +
                                 $"({(k % 64 + 0.5) * 10.0 / 64:F4}, {(k / 64 + 0.5) * 10.0 / 64:F4}) mm: " +
                                 $"cpu {cpu.Heights[k]:R}, cuda {gpu.Heights[k]:R}, difference {difference:E2} mm");
            }
        }

        output.WriteLine($"cpu removed {cpu.RemovedVolumeMm3:F9} mm³, cuda removed {gpu.RemovedVolumeMm3:F9} mm³");
        output.WriteLine($"cuda timing {gpu.TotalTiming}");
        Assert.True(worst < 1e-4f, $"the largest height difference was {worst:E2} mm");

        // Both sum the same heights in double, so the volumes differ only through the float32 heights themselves,
        // where the CUDA compiler contracts a*b+c into fma.
        Assert.True(Math.Abs(cpu.RemovedVolumeMm3 - gpu.RemovedVolumeMm3) < 1e-6 * cpu.RemovedVolumeMm3,
            $"cpu removed {cpu.RemovedVolumeMm3:R} mm³, cuda {gpu.RemovedVolumeMm3:R} mm³");
        Assert.True(gpu.TotalTiming.KernelMs > 0, "the CUDA backend should report a kernel time");
        Assert.True(gpu.LastTiming.WallMs >= gpu.LastTiming.KernelMs);
        output.WriteLine($"{CudaRuntime.Devices[cuda.DeviceIndex].Name}: kernel {gpu.TotalTiming.KernelMs:F3} ms, " +
                         $"upload {gpu.TotalTiming.UploadMs:F3} ms, download {gpu.TotalTiming.DownloadMs:F3} ms, " +
                         $"first call {gpu.TotalTiming.FirstCallMs:F1} ms for {steps.Length} steps");
    }

    /// <summary>Horizontal distance from a column to the segment of a step, in mm.</summary>
    private static double HorizontalDistance(double x, double y, BallStep s)
    {
        double wx = s.To.X - s.From.X, wy = s.To.Y - s.From.Y;
        double w2 = wx * wx + wy * wy;
        double px = x - s.From.X, py = y - s.From.Y;
        double t = w2 > 0 ? Math.Clamp((px * wx + py * wy) / w2, 0, 1) : 0;
        double ex = px - t * wx, ey = py - t * wy;
        return Math.Sqrt(ex * ex + ey * ey);
    }

    /// <summary>The lowest ball bottom over a dense sampling of the segment, or +inf when it never reaches.</summary>
    private static double SampleSegment(double x, double y, BallStep s, int samples)
    {
        double best = double.PositiveInfinity, r2 = s.RadiusMm * s.RadiusMm;
        for (int k = 0; k <= samples; k++)
        {
            double t = (double)k / samples;
            double cx = s.From.X + (s.To.X - s.From.X) * t;
            double cy = s.From.Y + (s.To.Y - s.From.Y) * t;
            double cz = s.From.Z + (s.To.Z - s.From.Z) * t;
            double d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
            if (d2 > r2) continue;
            double g = cz - Math.Sqrt(r2 - d2);
            if (g < best) best = g;
        }
        return best;
    }

    /// <summary>
    /// Writes an expanded scene the way <c>bench/run.py</c> does: per step the points of the ball at the start
    /// position and then at the end position, each ball starting with its two poles.
    /// </summary>
    private static string WriteExpanded(long[] min, long[] max, (long X, long Y, long Z)[] centres,
        long radiusNm, int segments)
    {
        int n = Math.Max(4, segments / 2 * 2);
        var steps = new List<string>();
        for (int s = 0; s + 1 < centres.Length; s++)
            steps.Add($"[{Ball(centres[s], radiusNm, n)},{Ball(centres[s + 1], radiusNm, n)}]");

        string path = Path.Combine(Path.GetTempPath(), $"nanocut-expanded-{Guid.NewGuid():N}.json");
        File.WriteAllText(path,
            $"{{\"box\": {{\"min\": [{min[0]},{min[1]},{min[2]}], \"max\": [{max[0]},{max[1]},{max[2]}]}}, " +
            $"\"steps\": [{string.Join(",", steps)}], \"save\": [-1]}}");
        return path;

        static string Ball((long X, long Y, long Z) c, long r, int n)
        {
            var pts = new List<string> { $"[{c.X},{c.Y},{c.Z + r}]", $"[{c.X},{c.Y},{c.Z - r}]" };
            for (int i = 1; i < n / 2; i++)
            {
                double th = Math.PI * i / (n / 2);
                for (int j = 0; j < n; j++)
                {
                    double ph = 2 * Math.PI * j / n;
                    pts.Add($"[{c.X + Round(r * Math.Sin(th) * Math.Cos(ph))}," +
                            $"{c.Y + Round(r * Math.Sin(th) * Math.Sin(ph))},{c.Z + Round(r * Math.Cos(th))}]");
                }
            }
            return string.Join(",", pts);
        }

        static long Round(double v) => (long)Math.Floor(v + 0.5);
    }
}
