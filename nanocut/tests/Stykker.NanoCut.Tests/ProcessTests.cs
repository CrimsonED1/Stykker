using Stykker.NanoCut.Cutting;
using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Tests;

public class GeneratorTests
{
    [Fact]
    public void ConvexPartsCoverRegionsWithHoles()
    {
        var plate = Region2.Rectangle(Vec2.Mm(0, 0), Vec2.Mm(50, 30))
                    - Region2.Circle(Vec2.Mm(10, 10), 4) - Region2.Circle(Vec2.Mm(30, 15), 6)
                    - Region2.Rectangle(Vec2.Mm(40, 5), Vec2.Mm(45, 25));
        var parts = plate.ConvexParts(); // exact area coverage is checked inside
        Assert.All(parts, p => Assert.True(ConvexHull.IsConvex(p)));
        Assert.Equal(plate.TwiceAreaNm2, Region2.UnionAll(parts.Select(p => Region2.Polygon(p))).TwiceAreaNm2);
    }

    [Fact]
    public void InvoluteGearProfile()
    {
        var gear = GearProfile.Involute(2, 20);
        Assert.Single(gear.Contours);
        double ra = 22, rf = 17.5;
        Assert.InRange(gear.AreaMm2, Math.PI * rf * rf, Math.PI * ra * ra);
        Assert.Equal(21, gear.ConvexParts().Count); // 20 convex teeth and the core
    }

    [Fact]
    public void ExtrudeRevolveAndTransform()
    {
        var gear = GearProfile.Involute(1, 24);
        var prism = Solid.Extrude(gear, 0, 5);
        Assert.Equal(gear.AreaMm2 * 5, prism.VolumeMm3, 6);
        Assert.Equal(prism.VolumeMm3, prism.Transform(Pose3.Rotation(0.7, 1, 2, 3)).VolumeMm3, 3);

        var tol = Tolerance.Default;
        var ring = Solid.Revolve(Region2.Rectangle(Vec2.Mm(2, 0), Vec2.Mm(5, 4)), tol);
        double exact = Math.PI * (25 - 4) * 4;
        Assert.InRange(ring.VolumeMm3, exact - 2 * Math.PI * (5 + 2) * 4 * tol.ChordNm * 1e-6, exact);
    }

    [Fact]
    public void ConvexHullAndTranslationSweep()
    {
        var cube = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(1, 1, 1));
        Assert.Equal(7, Sweep3.Translate(cube, Vec3.Mm(1, 2, 3)).VolumeMm3, 9);
        var hull = ConvexHull3.Compute([Vec3.Mm(0, 0, 0), Vec3.Mm(2, 0, 0), Vec3.Mm(0, 2, 0), Vec3.Mm(0, 0, 2), Vec3.Mm(0.3, 0.3, 0.3)]);
        Assert.Equal(8.0 / 6, hull.VolumeMm3, 9);
        var s = Solid.Sphere(default, 1, Tolerance.Budget(totalUm: 2.1, chordNm: 1000));
        Assert.Equal(Solid.Capsule(default, Vec3.Mm(5, 0, 0), 1, Tolerance.Budget(totalUm: 2.1, chordNm: 1000)).VolumeMm3,
            Sweep3.Translate(s, Vec3.Mm(5, 0, 0)).VolumeMm3, 9);
    }
}

public class ProcessTests
{
    [Fact]
    public void PlanarTranslationEqualsMinkowskiSum()
    {
        var tool = Region2.Polygon(Vec2.Mm(-1, -1), Vec2.Mm(1, -1), Vec2.Mm(1, 1), Vec2.Mm(-1, 1));
        var swept = Process2.Sweep(tool, Motion2.Polyline(Vec2.Mm(0, 0), Vec2.Mm(10, 0), Vec2.Mm(10, 5)));
        Assert.Equal(2 * 12 + 2 * 5 + 0, swept.AreaMm2, 9);
    }

    [Fact]
    public void PlanarRotationSweepsAnnulusWithoutOvercut()
    {
        // A 1 × 1 mm square rotating once about a centre 5 mm away sweeps an annulus.
        var tol = Tolerance.Budget(totalUm: 0.5, chordNm: 50, sweepNm: 300);
        var square = Region2.Rectangle(Vec2.Mm(5, -0.5), Vec2.Mm(6, 0.5));
        var swept = Process2.Sweep(square, Motion2.Rotate(default, 0, 2 * Math.PI), tol);
        double rIn = 5, rOut = Math.Sqrt(36 + 0.25);
        double exact = Math.PI * (rOut * rOut - rIn * rIn);
        double perimeter = 2 * Math.PI * (rIn + rOut);
        Assert.InRange(swept.AreaMm2, exact - perimeter * tol.SweepNm * 1e-6, exact + perimeter * tol.SweepNm * 1e-6);
    }

    [Fact]
    public void RackGeneratedGearHasInvoluteFlanks()
    {
        double m = 1;
        int z = 20;
        var tol = Tolerance.Budget(totalUm: 0.5, chordNm: 50, sweepNm: 300);
        double rp = m * z / 2 * 1e6, p = Math.PI * m * 1e6;
        var blank = Region2.Circle(default, m * z / 2 + m, tol);
        var rack = GearProfile.Rack(m, 7, 20, bodyMm: 0.3);
        var motion = Motion2.Sequence(Enumerable.Range(0, z).Select(k => Motion2.Custom(t =>
        {
            double dphi = 2 * Math.PI / z * t, phi = 2 * Math.PI * k / z + dphi;
            return Pose2.Rotation(-phi).Compose(new Pose2(0, p / 2 - rp * dphi, rp));
        })).ToArray());
        var gear = Process2.Cut(blank, rack, motion, tol);

        double alpha = 20 * Math.PI / 180, rb = rp * Math.Cos(alpha), ra = rp + m * 1e6;
        double psiB = Math.PI / (2 * z) + GearProfile.Inv(alpha), worst = 0;
        int count = 0;
        foreach (var c in gear.Contours)
            foreach (var v in c.Points)
            {
                double r = Math.Sqrt((double)v.X * v.X + (double)v.Y * v.Y);
                if (r < rb + 0.3 * m * 1e6 || r > ra - 0.1 * m * 1e6) continue;
                double ar = Math.Acos(rb / r), ideal = psiB - GearProfile.Inv(ar);
                double rel = Math.Abs(Math.IEEERemainder(Math.Atan2(v.Y, v.X), 2 * Math.PI / z));
                worst = Math.Max(worst, r * Math.Abs(rel - ideal) * Math.Sin(ar));
                count++;
            }
        Assert.True(count > 500);
        Assert.True(worst <= tol.SweepNm, $"flank deviation {worst} nm");
    }

    [Fact]
    public void TurningAGroove()
    {
        var tol = Tolerance.Default;
        var bar = Lathe.BarProfile(10, 0, 20);
        var insert = Region2.Rectangle(Vec2.Mm(0, -1), Vec2.Mm(4, 1)); // grooving insert, 2 mm wide
        var result = Lathe.Turn(bar, insert, Motion2.Polyline(Vec2.Mm(12, 10), Vec2.Mm(6, 10), Vec2.Mm(12, 10)), tol);
        // Profile: 10 × 20 mm minus the 4 × 2 mm groove.
        Assert.Equal(200 - 8, result.Profile.AreaMm2, 9);
        double exact = Math.PI * 100 * 20 - Math.PI * (100 - 36) * 2;
        Assert.InRange(result.Part.VolumeMm3, exact - 2 * Math.PI * 10 * 25 * tol.ChordNm * 1e-6, exact);
    }

    [Fact]
    public void SpatialTranslationMatchesCapsuleCut()
    {
        var tol = Tolerance.Budget(totalUm: 2.1, chordNm: 1000);
        var stock = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(20, 20, 10));
        var viaProcess = Process3.Cut(stock, ToolShape.Ball(3, tol), Motion3.Linear(Vec3.Mm(-5, 10, 12), Vec3.Mm(25, 10, 12)), tol);
        var viaCapsule = stock - Solid.Capsule(Vec3.Mm(-5, 10, 12), Vec3.Mm(25, 10, 12), 3, tol);
        Assert.Equal(viaCapsule.VolumeMm3, viaProcess.VolumeMm3, 6);
    }

    [Fact]
    public void SpatialRotationAgreesWithPlanarProcess()
    {
        // A bar rotating 3° about the z-axis cuts into a block: compare with the planar process.
        var tol = Tolerance.Budget(totalUm: 5, chordNm: 50, sweepNm: 4000);
        var block = Solid.Box(Vec3.Mm(3, -3, 0), Vec3.Mm(8, 3, 1));
        var bar = Solid.Box(Vec3.Mm(2, -0.5, -1), Vec3.Mm(6, 0.5, 2));
        var motion = Motion3.Rotate(default, 0, 0, 1, 0, 3 * Math.PI / 180);
        var cut3 = Process3.Cut([block], ToolShape.FromConvexParts(bar), motion, tol, out var stats)[0];

        var block2 = Region2.Rectangle(Vec2.Mm(3, -3), Vec2.Mm(8, 3));
        var bar2 = Region2.Rectangle(Vec2.Mm(2, -0.5), Vec2.Mm(6, 0.5));
        var cut2 = Process2.Cut(block2, bar2, Motion2.Rotate(default, 0, 3 * Math.PI / 180), tol);
        // The 3D hull method may overcut by at most the sweep budget along the swept boundary (~10 mm).
        Assert.InRange(cut3.VolumeMm3 - cut2.AreaMm2 * 1, -10 * tol.SweepNm * 1e-6, 1e-6);
        Assert.True(stats.Intervals > 1);
    }
}
