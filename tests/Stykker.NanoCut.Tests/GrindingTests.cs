using System.Diagnostics;
using Stykker.NanoCut.Cutting;
using Stykker.NanoCut.Geometry3D;
using Xunit.Abstractions;

namespace Stykker.NanoCut.Tests;

public class GrindingTests(ITestOutputHelper output)
{
    // Wheel axis (tool z) along world y, fed along +x over a block whose top is z = 0; the bond's lowest point is at
    // z = clearance, so a grain with protrusion p scratches p − clearance deep.
    private static readonly Pose3 AxisAlongY = Pose3.Rotation(-Math.PI / 2, 1, 0, 0);
    private static readonly Tolerance Tol = Tolerance.Budget(totalUm: 0.5, chordNm: 50, sweepNm: 200);

    private static Solid Block(double length = 2, double width = 1) => Solid.Box(Vec3.Mm(0, -width / 2, -1), Vec3.Mm(length, width / 2, 0));

    private static GrindingSimulation Simulate(GrindingWheel wheel, Solid block, double clearance, double x0, double x1, double feedMmPerS)
    {
        double zc = wheel.RadiusMm + clearance;
        var feed = Motion3.Compose(Motion3.Linear(Vec3.Mm(x0, 0, zc), Vec3.Mm(x1, 0, zc)), Motion3.Fixed(AxisAlongY));
        return new GrindingSimulation(block, wheel, feed, SpinningTool.Durations(feed, feedMmPerS), Tol);
    }

    [Fact]
    public void SingleGrainScratchHasProtrusionMinusClearanceDepth()
    {
        // R = 5 mm, one grain protruding 30 µm, bond 10 µm above the surface: scratch 20 µm deep.
        var wheel = GrindingWheel.FromGrains(5, 1, 3000, new GrindingWheel.GrainSpec(0, 0, 0.03, 0.1));
        var block = Block();
        var sw = Stopwatch.StartNew();
        var sim = Simulate(wheel, block, clearance: 0.01, x0: 0.5, x1: 1.5, feedMmPerS: 20);   // 0.4 mm per revolution
        sim.Run();
        var r = sim.Grains[0];
        // Lowest point of the scratches (the union of the cells has no cell walls left).
        double lowest = sim.Workpiece.Vertices.Where(v => v.Z > -0.5e6).Min(v => v.Z) / 1e6;
        output.WriteLine($"{sim.Passes.Count} passes, {r.ActivePasses} active, removed {r.RemovedMm3:E3} mm³, chip {r.MaxChipThicknessMm * 1000:F4} µm, " +
                         $"lowest {lowest * 1000:F4} µm, angles {r.EntryAngleDeg:F2}…{r.ExitAngleDeg:F2}°, {sim.Hulls} hulls, {sw.ElapsedMilliseconds} ms");
        Assert.True(r.Active);
        Assert.Equal(1, sim.ActiveGrains);
        Assert.InRange(lowest, -0.02 - 1e-6, -0.02 + Tol.SweepNm * 1e-6);
        Assert.InRange(r.MaxChipThicknessMm, 0.02 - 1e-4, 0.02 + 1e-6);
        // Contact arc of a 20 µm deep cut on R = 5 mm: about ±√(2 · 0.02 / 5) rad = ±5.1° around the lowest point.
        Assert.InRange(r.EntryAngleDeg, -6, 0);
        Assert.InRange(r.ExitAngleDeg, 0, 6);
        Assert.Equal(block.VolumeMm3 - sim.VolumeMm3, sim.RemovedMm3, 12);
        Assert.Equal(sim.RemovedMm3, r.RemovedMm3, 12);
        Assert.True(sim.Cells.Count > 1);
        Assert.Equal(sim.VolumeMm3, sim.Workpiece.VolumeMm3, 9);
    }

    [Fact]
    public void GrainsBelowTheEngagementLevelStayInactive()
    {
        var wheel = GrindingWheel.FromGrains(5, 1, 3000,
            new GrindingWheel.GrainSpec(0, -0.2, 0.025, 0.1),               // reaches 15 µm deep
            new GrindingWheel.GrainSpec(Math.PI / 2, 0.2, 0.008, 0.1),      // 2 µm short of the surface
            new GrindingWheel.GrainSpec(Math.PI, 0, 0.0, 0.1));             // flush with the bond
        var sim = Simulate(wheel, Block(), clearance: 0.01, x0: 0.5, x1: 1.5, feedMmPerS: 20);
        sim.Run();
        Assert.True(sim.Grains[0].Active);
        Assert.False(sim.Grains[1].Active);
        Assert.False(sim.Grains[2].Active);
        Assert.Equal(0, sim.Grains[1].RemovedMm3);
        Assert.Equal(3, sim.Grains[0].ActivePasses);                        // 2.5 revolutions over the block
        Assert.Equal(1.0 / 3, sim.ActiveRatio, 12);
    }

    [Fact]
    public void RandomWheelIsReproducibleFromItsSeed()
    {
        GrindingWheel Make(int seed) => GrindingWheel.Random(5, 1, 40, 0.1, 0.02, 0.01, 3000, seed);
        var a = Make(42);
        var b = Make(42);
        var c = Make(43);
        Assert.Equal(40, a.Grains.Count);
        for (int i = 0; i < a.Grains.Count; i++)
        {
            Assert.Equal(a.Grains[i].AngleRad, b.Grains[i].AngleRad);
            Assert.Equal(a.Grains[i].ProtrusionMm, b.Grains[i].ProtrusionMm);
            Assert.Equal(a.Grains[i].Tip, b.Grains[i].Tip);
            Assert.InRange(a.Grains[i].ProtrusionMm, 0, 0.1);
            Assert.InRange(a.Grains[i].AxialMm, -0.45, 0.45);
            // The tip lies at bond radius + protrusion.
            var t = a.Grains[i].Tip;
            Assert.Equal(5 + a.Grains[i].ProtrusionMm, Math.Sqrt((double)t.X * t.X + (double)t.Y * t.Y) / 1e6, 5);
        }
        Assert.NotEqual(a.Grains.Select(g => g.ProtrusionMm), c.Grains.Select(g => g.ProtrusionMm));
        double mean = Make(7).Grains.Concat(Make(8).Grains).Average(g => g.ProtrusionMm);
        Assert.InRange(mean, 0.015, 0.025);

        // The same wheel grinds the same result.
        var block = Block(1, 0.6);
        var s1 = Simulate(a, block, 0.005, 0.3, 0.5, 40);
        var s2 = Simulate(b, block, 0.005, 0.3, 0.5, 40);
        s1.Run();
        s2.Run();
        output.WriteLine($"{s1.ActiveGrains}/{a.Grains.Count} active, removed {s1.RemovedMm3:E4} mm³, {s1.Passes.Count} passes");
        Assert.Equal(s1.VolumeMm3, s2.VolumeMm3);
        Assert.Equal(s1.Grains.Select(g => g.RemovedMm3), s2.Grains.Select(g => g.RemovedMm3));
        Assert.True(s1.ActiveGrains > 0 && s1.ActiveGrains < a.Grains.Count);
    }

    [Fact]
    public void SurfaceProfileAndRoughness()
    {
        var box = Solid.Box(Vec3.Mm(0, 0, -1), Vec3.Mm(2, 2, 0.5));
        var flat = new SurfaceProfile(box);
        Assert.Equal(0.5, flat.TopZ(1, 1), 9);
        Assert.True(double.IsNaN(flat.TopZ(3, 1)));
        Assert.Equal((0.0, 0.0), SurfaceProfile.Roughness(flat.Line(0.1, 1, 1.9, 1, 50)));

        // A V-groove 0.2 mm deep and 0.2 mm wide in the top (flanks at 45°).
        var groove = box - ConvexHull3.Compute([Vec3.Mm(-1, 0.8, 0.7), Vec3.Mm(-1, 1.2, 0.7), Vec3.Mm(-1, 1, 0.3),
                                                Vec3.Mm(3, 0.8, 0.7), Vec3.Mm(3, 1.2, 0.7), Vec3.Mm(3, 1, 0.3)]);
        var p = new SurfaceProfile(groove);
        Assert.Equal(0.3, p.TopZ(1, 1), 6);
        Assert.Equal(0.4, p.TopZ(1, 0.95), 6);
        var (ra, rz) = SurfaceProfile.Roughness(p.Line(1, 0, 1, 2, 2001));
        Assert.Equal(0.2, rz, 6);
        Assert.InRange(ra, 0.005, 0.05);
    }
}
