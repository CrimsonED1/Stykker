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
    /// Regression from the earlier form of the bottom (expanded around the step start, see
    /// <see cref="LongStepsMatchTheExactBottom"/>): for a horizontal step the discriminant of the stationary point is
    /// zero in exact arithmetic, but it was computed as the difference of two products of size 4·d²·w2², so in
    /// float32 it came out slightly negative about half the time and the whole step was dropped for that cell. The
    /// longer the step (that is the larger w2 and d), the larger those products and the more certain the
    /// cancellation.
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
            Assert.Throws<GpuNativeException>(() =>
                cuda.Apply(Stock(4, 4), [BallStep.At((2, 2, 10), 1)], ZMapReadBack.Always));
            Assert.Throws<GpuNativeException>(() => cuda.RemovedVolumeMm3(Stock(4, 4)));
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

    /// <summary>
    /// The bilinear sampling of the CPU reference, against hand-computed values on a field that is not flat and not
    /// at the origin: the grid is 3 × 2 cells over 3 × 2 mm, so the cell centres sit at 0.5, 1.5, 2.5 and the
    /// interpolation weights are exact halves.
    /// </summary>
    [Fact]
    public void SamplingIsBilinearBetweenCellCentresAndReturnsAbsoluteMm()
    {
        // Heights are relative to the origin z = -4, so 0..5 in the array is -4..1 absolute.
        var map = new ZMap(0, 0, -4, 3, 2, 6, 3, 2, new CpuBackend(1));
        Array.Copy(new float[] { 0, 1, 2, 3, 4, 5 }, map.Heights, 6);

        SamplePoint[] points =
        [
            new(0.5, 0.5),   // a cell centre: the value is the cell value
            new(1.0, 0.5),   // half way between two columns, on a row centre: 0 + (1 - 0) / 2
            new(1.0, 1.0),   // half way in both directions: 0.5 + (3.5 - 0.5) / 2
            new(2.5, 1.5),   // the far cell centre: 5 relative
            new(-100, -100),  // clamped to the field: the low corner, not an extrapolation
            new(1000, 1000),  // clamped to the field: the far corner
        ];
        float[] expected = [-4f, -3.5f, -2f, 1f, -4f, 1f];

        var got = new float[points.Length];
        ZMapTiming timing = map.SampleHeights(points, got);

        for (int i = 0; i < points.Length; i++)
        {
            Assert.Equal(expected[i], got[i], 5);
            output.WriteLine($"({points[i].X}, {points[i].Y}) -> {got[i]:F4} mm, expected {expected[i]:F4}");
        }
        Assert.True(timing.KernelMs > 0);
    }

    /// <summary>
    /// The probe of the CPU reference against the field: the depth is the field height minus the bottom of the
    /// ball, clamped at zero, so a ball in air reads zero and a ball below the surface reads the material above it.
    /// </summary>
    [Fact]
    public void ProbingReadsHowDeepTheBallCuts()
    {
        // A flat 10 mm plate, so the field is 10 everywhere and the first four answers are whole millimetres.
        var map = new ZMap(0, 0, 0, 10, 10, 10, 32, 32, new CpuBackend(1));
        ToolPose[] poses =
        [
            new(5, 5, 11, 2),   // bottom at 9: cuts 1 mm
            new(5, 5, 10, 2),   // bottom at 8: cuts 2 mm
            new(5, 5, 12, 2),   // bottom at 10: just touching, nothing removed
            new(5, 5, 13, 2),   // bottom at 11: in air
        ];
        float[] expected = [1f, 2f, 0f, 0f];

        var got = new float[poses.Length];
        map.ProbeMaterial(poses, got);

        for (int i = 0; i < poses.Length; i++)
        {
            Assert.Equal(expected[i], got[i], 5);
            output.WriteLine($"pose {poses[i]} -> {got[i]:F4} mm, expected {expected[i]:F4}");
        }

        // After a dwell the field under the ball is no longer a round number -- the query reads the interpolated
        // field between the cell centres, not the analytic sphere -- so the answer is stated against the height the
        // same map reports at that point. That is the definition of the probe, and it is what makes the two agree.
        map.ApplySteps([BallStep.At((5, 5, 10), 4)]);
        var at = new[] { new SamplePoint(5, 5) };
        var height = new float[1];
        map.SampleHeights(at, height);
        Assert.True(height[0] < 10f, $"the dwell should have lowered the field, it is at {height[0]:F6} mm");
        output.WriteLine($"after the dwell the field at (5, 5) is {height[0]:F6} mm");

        map.ProbeMaterial([new ToolPose(5, 5, height[0] + 2, 2)], got);
        Assert.Equal(0f, got[0], 5);   // ball bottom exactly at the field
        map.ProbeMaterial([new ToolPose(5, 5, height[0] + 1, 2)], got);
        Assert.Equal(1f, got[0], 5);   // ball bottom 1 mm below the field
        map.ProbeMaterial([new ToolPose(5, 5, height[0] + 4, 2)], got);
        Assert.Equal(0f, got[0], 5);   // ball bottom above the field: in air
    }

    /// <summary>
    /// The read-back contract, on a backend that has a second copy of the field: with
    /// <see cref="ZMapReadBack.Never"/> the field stays on the backend, the host array is untouched, and everything
    /// that would read a stale surface throws instead of answering. The CUDA backend has exactly this shape, and
    /// this is asserted without a GPU.
    /// </summary>
    [Fact]
    public void ReadBackNeverLeavesAStaleSurfaceBehind()
    {
        var backend = new StubBackend();
        var map = Stock(8, 8, backend: backend);
        float[] before = (float[])map.Heights.Clone();

        map.ApplySteps([BallStep.At((5, 5, 10), 3)], ZMapReadBack.Never);

        Assert.Equal(ZMapReadBack.Never, backend.LastReadBack);
        Assert.False(map.IsHeightsCurrent);
        Assert.Equal(before, map.Heights);
        Assert.Throws<InvalidOperationException>(() => map.RemovedVolumeMm3);
        Assert.Throws<InvalidOperationException>(() => map.ToMesh());

        // The backend volume needs no read-back, so progress can be reported while the field stays put.
        double fromBackend = map.BackendRemovedVolumeMm3;
        Assert.True(fromBackend > 0);
        Assert.False(map.IsHeightsCurrent);

        map.ReadHeights();
        Assert.True(map.IsHeightsCurrent);
        Assert.Equal(ZMapReadBack.Always, backend.LastReadBack);
        Assert.Equal(map.RemovedVolumeMm3, fromBackend, 9);
        Assert.True(map.ToMesh().VertexCount > 0);

        // A CPU-like backend has no second copy, so the flag stays true even when the caller asks for no read-back.
        var cpu = Stock(8, 8, backend: new CpuBackend(1));
        cpu.ApplySteps([BallStep.At((5, 5, 10), 3)], ZMapReadBack.Never);
        Assert.True(cpu.IsHeightsCurrent);
        Assert.True(cpu.RemovedVolumeMm3 > 0);
    }

    /// <summary>A backend that cannot answer queries says so instead of pretending.</summary>
    [Fact]
    public void ABackendWithoutQueriesIsReported()
    {
        var map = Stock(4, 4, backend: new StubBackend());
        Assert.Throws<NotSupportedException>(() => map.SampleHeights([new SamplePoint(1, 1)], new float[1]));
        Assert.Throws<NotSupportedException>(() => map.ProbeMaterial([new ToolPose(1, 1, 1, 1)], new float[1]));

        // An empty query is answered without the backend at all, so a loop over no items never throws.
        Assert.Equal(ZMapTiming.Zero, map.SampleHeights([], new float[0]));
    }

    /// <summary>CUDA sampling and probing against the CPU reference. Same early return as the other GPU tests.</summary>
    [Fact]
    public void CudaQueriesAgreeWithTheCpuReference()
    {
        var cuda = new CudaBackend();
        if (!cuda.IsAvailable)
        {
            output.WriteLine($"not run: {cuda.UnavailableReason}");
            return;
        }

        BallStep[] steps =
        [
            new((1, 1, 10), (9, 1, 10), 2),
            new((7, 3, 10), (7, 8, 9), 2),
            BallStep.At((4, 5, 10), 1.5),
        ];

        var cpu = Stock(64, 64, backend: new CpuBackend(1));
        var gpu = Stock(64, 64, backend: cuda);
        cpu.ApplySteps(steps, ZMapReadBack.Never);
        gpu.ApplySteps(steps, ZMapReadBack.Never);

        // A mix of cell centres, cell edges and points outside the field, on a regular grid over the plate.
        SamplePoint[] points = new SamplePoint[41 * 41];
        int p = 0;
        for (int j = 0; j <= 40; j++)
            for (int i = 0; i <= 40; i++)
                points[p++] = new SamplePoint(-2 + 12.0 * i / 40, -2 + 12.0 * j / 40);

        ToolPose[] poses = new ToolPose[steps.Length * 3];
        p = 0;
        for (int s = 0; s < steps.Length; s++)
        {
            poses[p++] = ToolPose.From(steps[s]);
            poses[p++] = new ToolPose(steps[s].From.X, steps[s].From.Y, steps[s].From.Z + 3, steps[s].RadiusMm);
            poses[p++] = new ToolPose(steps[s].From.X, steps[s].From.Y, 20, steps[s].RadiusMm);
        }

        var cpuHeights = new float[points.Length];
        var gpuHeights = new float[points.Length];
        cpu.ReadHeights();
        gpu.ReadHeights();
        ZMapTiming cpuSample = cpu.SampleHeights(points, cpuHeights);
        ZMapTiming gpuSample = gpu.SampleHeights(points, gpuHeights);
        Assert.True(gpuSample.KernelMs > 0, "the CUDA backend should report a kernel time for the query");

        float worstHeight = 0;
        for (int i = 0; i < points.Length; i++)
        {
            float difference = Math.Abs(cpuHeights[i] - gpuHeights[i]);
            if (difference > worstHeight) worstHeight = difference;
        }

        var cpuDepths = new float[poses.Length];
        var gpuDepths = new float[poses.Length];
        ZMapTiming cpuProbe = cpu.ProbeMaterial(poses, cpuDepths);
        ZMapTiming gpuProbe = gpu.ProbeMaterial(poses, gpuDepths);
        Assert.True(gpuProbe.KernelMs > 0, "the CUDA backend should report a kernel time for the probe");

        float worstDepth = 0;
        for (int i = 0; i < poses.Length; i++)
        {
            float difference = Math.Abs(cpuDepths[i] - gpuDepths[i]);
            if (difference > worstDepth) worstDepth = difference;
        }

        output.WriteLine($"{points.Length} points: cpu {cpuSample.KernelMs:F3} ms, cuda kernel {gpuSample.KernelMs:F3} ms, " +
                         $"upload {gpuSample.UploadMs:F3} ms, download {gpuSample.DownloadMs:F3} ms, worst height {worstHeight:E2} mm");
        output.WriteLine($"{poses.Length} poses: cpu {cpuProbe.KernelMs:F3} ms, cuda kernel {gpuProbe.KernelMs:F3} ms, " +
                         $"upload {gpuProbe.UploadMs:F3} ms, download {gpuProbe.DownloadMs:F3} ms, worst depth {worstDepth:E2} mm");

        Assert.True(worstHeight < 1e-4f, $"the largest height difference was {worstHeight:E2} mm");
        Assert.True(worstDepth < 1e-4f, $"the largest depth difference was {worstDepth:E2} mm");
        Assert.Contains(0f, cpuDepths);   // the poses 10 mm above the stock must read as air on both backends
    }

    /// <summary>
    /// The device volume reduction against the host sum of the same field. The point is not only the value but the
    /// fact that it is available while the field stays on the device, which is what makes it usable for progress.
    /// </summary>
    [Fact]
    public void CudaDeviceVolumeAgreesWithTheHostSum()
    {
        var cuda = new CudaBackend();
        if (!cuda.IsAvailable)
        {
            output.WriteLine($"not run: {cuda.UnavailableReason}");
            return;
        }

        var map = Stock(128, 96, backend: cuda);
        map.ApplySteps(
        [
            new BallStep((1, 1, 10), (9, 1, 10), 2),
            new BallStep((2, 3, 10), (7, 8, 9), 2),
            BallStep.At((4, 5, 10), 1.5),
        ], ZMapReadBack.Never);

        double device = map.BackendRemovedVolumeMm3;
        Assert.False(map.IsHeightsCurrent);
        Assert.Throws<InvalidOperationException>(() => map.RemovedVolumeMm3);

        double downloadMs = map.ReadHeights();
        double host = map.RemovedVolumeMm3;

        output.WriteLine($"device {device:F9} mm³, host {host:F9} mm³, " +
                         $"difference {(device - host) / host:E2}, read-back {downloadMs:F3} ms");
        Assert.True(Math.Abs(device - host) < 1e-6 * host,
            $"the device reduced {device:R} mm³, the host sum is {host:R} mm³");
    }

    /// <summary>
    /// A point set answers exactly what the span it was made from answers, and releasing it is the caller's job.
    /// Nothing about the answer may depend on where the backend keeps the points.
    /// </summary>
    [Fact]
    public void APointSetAnswersWhatTheSpanAnswers()
    {
        var map = Stock(48, 32);
        map.ApplySteps(
        [
            new BallStep((1, 1, 10), (9, 1, 10), 2),
            BallStep.At((4, 5, 10), 1.5),
        ]);

        SamplePoint[] points = new SamplePoint[37 * 29];
        int p = 0;
        for (int j = 0; j <= 28; j++)
            for (int i = 0; i <= 36; i++)
                points[p++] = new SamplePoint(-1 + 12.0 * i / 36, -1 + 12.0 * j / 28);

        var fromSpan = new float[points.Length];
        var fromSet = new float[points.Length];
        map.SampleHeights(points, fromSpan);

        var set = map.UploadPoints(points);
        Assert.Equal(points.Length, set.Count);
        Assert.False(set.IsDisposed);
        map.SampleHeights(set, fromSet);

        // Bit for bit, not approximately: the same loop over the same numbers.
        Assert.Equal(fromSpan, fromSet);

        set.Dispose();
        Assert.True(set.IsDisposed);
        set.Dispose();    // releasing twice is not an error
        Assert.Throws<ObjectDisposedException>(() => map.SampleHeights(set, fromSet));
    }

    /// <summary>
    /// The query a viewer runs after every batch of steps: the same points, already on the device, so nothing is
    /// uploaded and the answer is the one the span query gives to the bit. The span query costs 1.7 ms of upload for
    /// a million points, this one costs nothing.
    /// </summary>
    [Fact]
    public void CudaResidentPointsUploadOnceAndAnswerLikeTheSpan()
    {
        var cuda = new CudaBackend();
        if (!cuda.IsAvailable)
        {
            output.WriteLine($"not run: {cuda.UnavailableReason}");
            return;
        }

        var gpu = Stock(64, 64, backend: cuda);
        gpu.ApplySteps(
        [
            new BallStep((1, 1, 10), (9, 1, 10), 2),
            new BallStep((7, 3, 10), (7, 8, 9), 2),
            BallStep.At((4, 5, 10), 1.5),
        ], ZMapReadBack.Never);

        SamplePoint[] points = new SamplePoint[41 * 41];
        int p = 0;
        for (int j = 0; j <= 40; j++)
            for (int i = 0; i <= 40; i++)
                points[p++] = new SamplePoint(-2 + 12.0 * i / 40, -2 + 12.0 * j / 40);

        var fromSpan = new float[points.Length];
        var fromSet = new float[points.Length];
        var again = new float[points.Length];

        ZMapTiming spanQuery = gpu.SampleHeights(points, fromSpan);
        using PointSet set = gpu.UploadPoints(points);
        ZMapTiming resident = gpu.SampleHeights(set, fromSet);
        ZMapTiming second = gpu.SampleHeights(set, again);

        Assert.Equal(fromSpan, fromSet);
        Assert.Equal(fromSpan, again);
        Assert.True(resident.UploadMs < 0.05,
            $"the resident query reported {resident.UploadMs:F4} ms of upload, the span query {spanQuery.UploadMs:F3} ms");

        output.WriteLine($"{points.Length} points: from a span kernel {spanQuery.KernelMs:F3} ms, " +
                         $"up {spanQuery.UploadMs:F3} ms, down {spanQuery.DownloadMs:F3} ms, wall {spanQuery.WallMs:F3} ms");
        output.WriteLine($"{points.Length} points: from a set  kernel {resident.KernelMs:F3} ms, " +
                         $"up {resident.UploadMs:F4} ms, down {resident.DownloadMs:F3} ms, wall {resident.WallMs:F3} ms " +
                         $"(second call {second.WallMs:F3} ms)");
    }

    /// <summary>
    /// A backend with a second copy of the field, standing in for the CUDA one where a GPU is not around: it records
    /// what it was asked and only fills <see cref="ZMap.Heights"/> when it was asked to.
    /// </summary>
    private sealed class StubBackend : IZMapBackend
    {
        public string Name => "stub";

        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public bool KeepsHeightsOnHost => false;

        public int VolumeCalls { get; private set; }

        public ZMapReadBack LastReadBack { get; private set; } = ZMapReadBack.Always;

        public ZMapTiming Apply(ZMap map, ReadOnlySpan<BallStep> steps, ZMapReadBack readBack)
        {
            LastReadBack = readBack;
            float[] packed = ToolProfile.Pack(steps, map.OriginMm);
            Field = (float[])map.Heights.Clone();
            float[] device = Field;
            for (int j = 0; j < map.CellsY; j++)
            {
                for (int i = 0; i < map.CellsX; i++)
                {
                    float x = map.CellCentreX(i), y = map.CellCentreY(j), lowest = device[j * map.CellsX + i];
                    for (int s = 0; s < steps.Length; s++)
                    {
                        float b = ToolProfile.Bottom(x, y, packed, s);
                        if (b < lowest) lowest = b > map.BottomRelative ? b : map.BottomRelative;
                    }
                    device[j * map.CellsX + i] = lowest;
                }
            }

            if (readBack == ZMapReadBack.Always) ReadHeights(map);
            return new ZMapTiming(0, 0, 0, 0, 0);
        }

        public double ReadHeights(ZMap map)
        {
            LastReadBack = ZMapReadBack.Always;
            Field.CopyTo(map.Heights, 0);
            return 0;
        }

        public double RemovedVolumeMm3(ZMap map)
        {
            VolumeCalls++;
            double top = map.TopRelative, sum = 0;
            foreach (float h in Field) sum += top - h;
            return sum * map.CellSizeXMm * map.CellSizeYMm;
        }

        /// <summary>The field as the backend holds it, which is not <see cref="ZMap.Heights"/> until a read-back.</summary>
        public float[] Field { get; private set; } = [];
    }

    /// <summary>
    /// Regression: the bottom of a long step. An earlier form expanded the reach of the ball around the start of the
    /// step, so on a long step the float terms were of order L² while the result is of order r²: a 100 mm step with
    /// r = 1 mm was off by 0.02 mm, a 50 mm ramp with r = 0.1 mm missed the cut by millimetres, and a long thin ramp
    /// could cut below the ball. Columns across the whole swept band are compared with a double-precision reference
    /// that finds the minimum by a different method (golden-section search on the convex bottom curve).
    /// </summary>
    [Theory]
    [InlineData(100, 1, 0)]
    [InlineData(1000, 1, 0)]
    [InlineData(50, 0.1, 0.3)]
    [InlineData(100, 0.1, 0.05)]
    [InlineData(200, 0.01, 0.3)]
    [InlineData(1000, 3, 0.05)]
    [InlineData(1000, 10, 0.3)]
    [InlineData(1000, 3, -0.3)]
    public void LongStepsMatchTheExactBottom(double length, double radius, double slope)
    {
        var (step, origin) = LongRamp(length, radius, slope);
        float[] packed = ToolProfile.Pack([step], origin);
        var (ux, uy) = (Math.Cos(0.37), Math.Sin(0.37));

        int compared = 0;
        double worst = 0;
        for (int k = 0; k <= 400; k++)
        {
            // Along the step, from before its start to past its end, and across it from well outside to well outside.
            double along = -1.5 * radius + (length + 3 * radius) * k / 400.0;
            for (int m = -24; m <= 24; m++)
            {
                double across = radius * m / 20.0;
                double x = step.From.X + along * ux - across * uy, y = step.From.Y + along * uy + across * ux;
                float fx = (float)(x - origin.X), fy = (float)(y - origin.Y);
                double cx = origin.X + fx, cy = origin.Y + fy;   // the column the float coordinates name
                double expected = ExactBottom(cx, cy, step) - origin.Z;
                float actual = ToolProfile.Bottom(fx, fy, packed, 0);

                // Within rounding of the rim the column may count as inside or outside; that is a lateral question
                // of a few float units, not an error of the formula. Everywhere else the bottom must match.
                double gap = radius - HorizontalDistance(cx, cy, step);
                double delta = 4 * 6e-8 * (length + Math.Abs(fx) + Math.Abs(fy) + 1);
                if (Math.Abs(gap) < 4 * delta) continue;
                if (double.IsPositiveInfinity(expected))
                {
                    Assert.True(float.IsPositiveInfinity(actual),
                        $"({cx}, {cy}) is {-gap:E2} mm outside the step but the bottom was {actual}");
                    continue;
                }
                Assert.False(float.IsPositiveInfinity(actual), $"({cx}, {cy}) is {gap:E2} mm inside the step");
                double tolerance = 2e-6 + delta * (1 + Math.Sqrt(radius / (2 * gap)));
                double error = Math.Abs(actual - expected);
                worst = Math.Max(worst, error);
                Assert.True(error <= tolerance,
                    $"({cx}, {cy}): bottom {actual}, expected {expected}, off by {error:E2} mm (tolerance {tolerance:E2})");
                compared++;
            }
        }
        output.WriteLine($"{compared} columns, worst {worst:E2} mm");
        Assert.True(compared > 5000, $"only {compared} columns were compared");
    }

    /// <summary>The CUDA kernel on the long steps of <see cref="LongStepsMatchTheExactBottom"/>, against the CPU.</summary>
    [Fact]
    public void CudaLongStepsAgreeWithTheCpuReference()
    {
        var cuda = new CudaBackend();
        if (!cuda.IsAvailable)
        {
            output.WriteLine($"not run: {cuda.UnavailableReason}");
            return;
        }

        foreach (var (length, radius, slope) in new[] { (1000.0, 1.0, 0.0), (50, 0.1, 0.3), (1000, 3, 0.05), (20, 0.01, 0.3) })
        {
            var (step, origin) = LongRamp(length, radius, slope);
            var end = (X: Math.Max(step.From.X, step.To.X) + 2 * radius, Y: Math.Max(step.From.Y, step.To.Y) + 2 * radius);
            int cellsX = 2000, cellsY = 1000;
            var cpu = new ZMap(origin.X, origin.Y, origin.Z, end.X, end.Y, step.From.Z + 2 * radius, cellsX, cellsY);
            var gpu = new ZMap(origin.X, origin.Y, origin.Z, end.X, end.Y, step.From.Z + 2 * radius, cellsX, cellsY, cuda);
            cpu.ApplySteps([step]);
            gpu.ApplySteps([step]);

            // fma contraction moves a column by a few float units, and near the rim the height is steep in the
            // column position, so the tolerance grows there as in LongStepsMatchTheExactBottom; columns within
            // rounding of the rim may differ by the whole depth and are skipped.
            int compared = 0;
            double worst = 0;
            for (int j = 0; j < cellsY; j++)
            {
                for (int i = 0; i < cellsX; i++)
                {
                    int k = j * cellsX + i;
                    double cx = origin.X + (i + 0.5) * cpu.CellSizeXMm, cy = origin.Y + (j + 0.5) * cpu.CellSizeYMm;
                    double gap = radius - HorizontalDistance(cx, cy, step);
                    double delta = 4 * 6e-8 * (length + Math.Abs(cx) + Math.Abs(cy) + 1);
                    if (gap < 4 * delta) continue;
                    double tolerance = 2 * (2e-6 + delta * (1 + Math.Sqrt(radius / (2 * gap))));
                    double d = Math.Abs(cpu.Heights[k] - gpu.Heights[k]);
                    worst = Math.Max(worst, d / tolerance);
                    Assert.True(d <= tolerance,
                        $"L {length}, r {radius}: cell ({i}, {j}) CPU {cpu.Heights[k]} CUDA {gpu.Heights[k]}, tolerance {tolerance:E2}");
                    compared++;
                }
            }
            output.WriteLine($"L {length}, r {radius}, slope {slope}: {compared} cells, worst {worst:F3} of the tolerance");
            Assert.True(compared > 0);
            // Rim columns that flip between the two count here, so the volumes agree to the rim, not to float units.
            Assert.Equal(cpu.RemovedVolumeMm3, gpu.BackendRemovedVolumeMm3, 1e-4 * Math.Max(1, cpu.RemovedVolumeMm3));
        }
    }

    [Fact]
    public void UntouchedStockRemovesNothingOnAnAwkwardBox()
    {
        // A top that float cannot represent, far from the origin: the host sum used to report the rounding of the
        // top times the box area as removed material, while the device reported zero.
        var map = new ZMap(3468.3, -12.7, 18.5, 3539.9, 9.4, 40.1, 37, 11);
        Assert.Equal(0, map.RemovedVolumeMm3);
        map.ApplySteps([BallStep.At((0, 0, 100), 1)]);
        Assert.Equal(0, map.RemovedVolumeMm3);
    }

    /// <summary>
    /// A straight step of <paramref name="length"/> mm in a direction that is not axis aligned, falling by
    /// <paramref name="slope"/>·length (rising when negative), and the grid origin below and before it.
    /// </summary>
    internal static (BallStep Step, (double X, double Y, double Z) Origin) LongRamp(double length, double radius, double slope)
    {
        var (ux, uy) = (Math.Cos(0.37), Math.Sin(0.37));
        double startZ = 10 + Math.Max(slope, 0) * length;
        var from = (X: 3.0 * radius, Y: 3.0 * radius, Z: startZ);
        var to = (X: from.X + ux * length, Y: from.Y + uy * length, Z: startZ - slope * length);
        return (new BallStep(from, to, radius), (0, 0, 0));
    }

    /// <summary>
    /// The lowest z of the swept ball at a column in double precision, or +inf when it does not reach: the valid
    /// interval of t from the distance to the step line, then a golden-section search for the minimum of the convex
    /// bottom curve on it.
    /// </summary>
    internal static double ExactBottom(double x, double y, BallStep s)
    {
        double px = x - s.From.X, py = y - s.From.Y;
        double wx = s.To.X - s.From.X, wy = s.To.Y - s.From.Y, wz = s.To.Z - s.From.Z, r2 = s.RadiusMm * s.RadiusMm;
        double w2 = wx * wx + wy * wy, d = px * wx + py * wy, p2 = px * px + py * py;
        double lo = 0, hi = 1;
        if (w2 == 0)
        {
            if (p2 > r2) return double.PositiveInfinity;
        }
        else
        {
            double disc = d * d - w2 * (p2 - r2);
            if (disc < 0) return double.PositiveInfinity;
            double sq = Math.Sqrt(disc);
            lo = Math.Max(0, (d - sq) / w2);
            hi = Math.Min(1, (d + sq) / w2);
            if (lo > hi) return double.PositiveInfinity;
        }

        double G(double t) => s.From.Z + wz * t - Math.Sqrt(Math.Max(r2 - p2 + 2 * d * t - w2 * t * t, 0));
        const double phi = 0.6180339887498949;
        double a = lo, b = hi, c = b - phi * (b - a), e = a + phi * (b - a), gc = G(c), ge = G(e);
        for (int k = 0; k < 200 && b - a > 1e-15; k++)
        {
            if (gc < ge) { b = e; e = c; ge = gc; c = b - phi * (b - a); gc = G(c); }
            else { a = c; c = e; gc = ge; e = a + phi * (b - a); ge = G(e); }
        }
        return Math.Min(Math.Min(G(lo), G(hi)), Math.Min(gc, ge));
    }

    /// <summary>Horizontal distance from a column to the segment of a step, in mm.</summary>
    internal static double HorizontalDistance(double x, double y, BallStep s)
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
