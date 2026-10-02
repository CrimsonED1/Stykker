using System.Diagnostics;
using Stykker.NanoCut.Cutting;
using Stykker.NanoCut.Geometry3D;
using Xunit.Abstractions;

namespace Stykker.NanoCut.Tests;

public class SpinningToolTests(ITestOutputHelper output)
{
    // Spindle axis (tool z) along world y: the disc stands in the xz-plane and is fed along x.
    private static readonly Pose3 AxisAlongY = Pose3.Rotation(-Math.PI / 2, 1, 0, 0);

    private static Motion3 Feed(double x0, double x1, double zCenter) =>
        Motion3.Compose(Motion3.Linear(Vec3.Mm(x0, 0, zCenter), Vec3.Mm(x1, 0, zCenter)), Motion3.Fixed(AxisAlongY));

    // Block x ∈ [0, 20], y ∈ [-5, 5], z ∈ [-10, 0].
    private static Solid Block() => Solid.Box(Vec3.Mm(0, -5, -10), Vec3.Mm(20, 5, 0));
    private const double BlockVolume = 20 * 10 * 10;

    [Fact]
    public void TimeAngleAndFeedPerTooth()
    {
        var saw = SpinningTool.SawBlade(20, 1.6, 24, 2, rpm: 3000);
        Assert.Equal(2 * Math.PI, saw.AngleAt(0.02), 12);              // 3000 rpm = 50 rev/s
        Assert.Equal(100 * Math.PI, saw.AngularVelocityRadPerS, 9);
        Assert.Equal(0.05, saw.FeedPerToothMm(60), 12);                  // 60 mm/s ÷ (50/s · 24)
        Assert.Equal(1.2, saw.FeedPerRevolutionMm(60), 12);
        Assert.False(saw.IsSpinInvariant);
        Assert.True(SpinningTool.Disc(20, 2, 3000).IsSpinInvariant);
        Assert.Equal(25, saw.Shape.Parts.Count);

        // A quarter revolution turns (10, 0, 0) into (0, 10, 0) about the tool z-axis.
        Assert.Equal(Vec3.Mm(0, 10, 0), saw.SpinPose(0.005).Apply(Vec3.Mm(10, 0, 0)));

        Assert.Equal([1.5, 2.0], SpinningTool.Durations(Motion3.Polyline(Vec3.Mm(0, 0, 0), Vec3.Mm(3, 0, 0), Vec3.Mm(3, 4, 0)), 2));

        // Feed 10 mm in 1 s at 3000 rpm: 50 revolutions, split into pieces of ≤ 45° (400 pieces).
        var motion = saw.Motion(Motion3.Linear(Vec3.Mm(0, 0, 0), Vec3.Mm(10, 0, 0)), [1.0]);
        Assert.Equal(400, motion.Segments.Count);
        // Piece 2 starts at t = 2/400 s: 45° · 2 = 90° and 0.05 mm feed.
        Assert.Equal(Vec3.Mm(0.05, 10, 0), motion.Segments[2](0).Apply(Vec3.Mm(10, 0, 0)));
        // Every piece ends where the next starts; after 50 whole revolutions the point is back on the x-axis.
        for (int i = 0; i + 1 < motion.Segments.Count; i++)
            Assert.Equal(motion.Segments[i](1).Apply(Vec3.Mm(10, 0, 0)), motion.Segments[i + 1](0).Apply(Vec3.Mm(10, 0, 0)));
        Assert.Equal(Vec3.Mm(20, 0, 0), motion.Segments[^1](1).Apply(Vec3.Mm(10, 0, 0)));

        // The start time sets the spindle phase.
        var later = saw.Motion(Motion3.Linear(Vec3.Mm(0, 0, 0), Vec3.Mm(10, 0, 0)), [1.0], startSeconds: 0.005);
        Assert.Equal(Vec3.Mm(0, 10, 0), later.Segments[0](0).Apply(Vec3.Mm(10, 0, 0)));
    }

    [Fact]
    public void SawBladeGeneratorHasConvexCoreAndTeeth()
    {
        var tol = Tolerance.Budget(totalUm: 10, chordNm: 2000, sweepNm: 5000);
        var blade = ToolShape.SawBlade(10, 1, 8, 1.5, tol);
        Assert.Equal(9, blade.Parts.Count);
        var b = blade.ToSolid().BoundsMm!.Value;
        Assert.InRange(b.MaxX, 9.9, 10);                                 // tooth 0 points along +x, tip corners on r = 10
        Assert.Equal(0.5, b.MaxZ, 9);
        double core = Math.PI * 8.5 * 8.5 * 1, ring = Math.PI * (100 - 8.5 * 8.5) * 1;
        Assert.InRange(blade.ToSolid().VolumeMm3, core * 0.99, core + ring * 0.6);
    }

    [Fact]
    public void SymmetricDiscIsSpinInvariant()
    {
        // A plain disc fed into a block while spinning an eighth of a revolution: sampling the spin (convex hulls of the
        // rotating polygon) gives the same cut as the revolved body moved by the feed alone.
        var tol = Tolerance.Budget(totalUm: 251, chordNm: 50000, sweepNm: 200000);
        var disc = SpinningTool.Disc(10, 2, rpm: 30, tol);
        var feed = Feed(-7.2, -3.2, 7);                                  // 4 mm at 16 mm/s = 0.25 s = 45°
        var block = Block();

        var sw = Stopwatch.StartNew();
        var invariant = Process3.CutSpinning([block], disc, feed, 16, tol, out var s1)[0];
        long tInvariant = sw.ElapsedMilliseconds;
        var direct = Process3.Cut(block, disc.Body!, feed, tol);
        Assert.Equal(direct.VolumeMm3, invariant.VolumeMm3, 9);
        Assert.Null(s1.Cutters);
        Assert.Equal(0.125, s1.Revolutions, 9);

        sw.Restart();
        var sampled = Process3.CutSpinning([block], SpinningTool.Asymmetric(disc.Body!, rpm: 30), feed, 16, tol, out var s2)[0];
        long tSampled = sw.ElapsedMilliseconds;
        output.WriteLine($"invariant {tInvariant} ms, sampled spin {tSampled} ms ({s2.Cutters!.Intervals} intervals)");
        Assert.True(s2.Cutters.Intervals > 10);

        double removed = BlockVolume - invariant.VolumeMm3, removedSampled = BlockVolume - sampled.VolumeMm3;
        output.WriteLine($"removed {removed:F6} mm³ (revolved body), {removedSampled:F6} mm³ (sampled spin)");
        Assert.InRange(removed, 5, 40);
        // Both differ only by the chord error of the polygon (the rotating polygon sweeps its circumcircle) and the
        // hull error of the sampled spin, each over the cut surface (< 40 mm²).
        Assert.InRange(removedSampled - removed, -1e-6, 40 * (tol.ChordNm + tol.SweepNm) * 1e-6);
    }

    [Fact]
    public void DiscSlotHasExpectedVolume()
    {
        // Through-slot 3 mm deep with a Ø20 × 2 mm wheel: removed volume = 20 × 3 × 2 mm³.
        var tol = Tolerance.Budget(totalUm: 2.1, chordNm: 1000, sweepNm: 500);
        var disc = SpinningTool.Disc(10, 2, rpm: 3000, tol);
        var sw = Stopwatch.StartNew();
        var rest = Process3.CutSpinning(Block(), disc, Feed(-11, 31, 7), feedMmPerS: 20, tol);
        output.WriteLine($"slot {sw.ElapsedMilliseconds} ms");
        double removed = BlockVolume - rest.VolumeMm3;
        Assert.InRange(removed, 120 - 2 * 20 * tol.ChordNm * 1e-6, 120 + 1e-6);
    }

    [Fact]
    public void ToothedDiscApproachesPlainDiscAtSlowFeed()
    {
        // A saw blade fed 3 mm into the block and stopped there. At the end position the teeth have not yet cut all
        // the material a plain disc of the tip radius removes (a sliver of up to one feed per tooth along the engaged
        // arc), so the difference shrinks with the feed per tooth. The feed lies in the blade plane: planar process.
        var tol = Tolerance.Budget(totalUm: 4.1, chordNm: 2000, sweepNm: 2000);
        const double r = 10, depth = 2, rpm = 3000;
        double x0 = -Math.Sqrt(2 * r * depth - depth * depth) - 0.2;
        var feed = Feed(x0, x0 + 3, r - depth);
        var plain = Process3.CutSpinning(Block(), SpinningTool.Disc(r, 1, rpm, tol), feed, 10, tol);
        double vPlain = BlockVolume - plain.VolumeMm3;
        var saw = SpinningTool.SawBlade(r, 1, 6, 1.5, rpm, tol);

        var deficit = new List<double>();
        foreach (double fz in new[] { 1, 0.2, 0.05 })
        {
            double feedMmPerS = fz * rpm / 60 * saw.Teeth;
            Assert.Equal(fz, saw.FeedPerToothMm(feedMmPerS), 12);
            var sw = Stopwatch.StartNew();
            var rest = Process3.CutSpinning([Block()], saw, feed, feedMmPerS, tol, out var stats)[0];
            double v = BlockVolume - rest.VolumeMm3;
            Assert.NotNull(stats.PlanarCutters);
            output.WriteLine($"f_z {fz} mm: removed {v:F6} mm³ (plain disc {vPlain:F6}), {stats.Revolutions:F1} rev, " +
                             $"{stats.PlanarCutters!.Intervals} steps, {sw.ElapsedMilliseconds} ms");
            deficit.Add(vPlain - v);
        }
        // Engaged arc at the front ≈ 6.3 mm, 1 mm wide: the sliver is below f_z · 6.3 mm³ (plus chord/sweep errors).
        double errors = 30 * (tol.ChordNm + tol.SweepNm) * 1e-6;
        Assert.InRange(deficit[2], -errors, 0.05 * 6.3 + errors);
        Assert.True(deficit[0] > deficit[1] && deficit[1] > deficit[2], "a finer feed per tooth must come closer to the plain disc");
    }

    [Fact]
    public void PlanarAndSpatialSpinningAgree()
    {
        // The planar edge sweep (prismatic teeth fed in their plane) and the general spatial process (convex hulls of
        // the rotating teeth) give the same cut within their error bounds.
        var tol = Tolerance.Budget(totalUm: 100.1, chordNm: 20000, sweepNm: 80000);
        const double r = 10, depth = 2;
        double x0 = -Math.Sqrt(2 * r * depth - depth * depth) - 0.2;
        var feed = Feed(x0, x0 + 3, r - depth);
        var saw = SpinningTool.SawBlade(r, 1, 6, 1.5, rpm: 3000, tol);
        var sw = Stopwatch.StartNew();
        var planar = Process3.CutSpinning([Block()], saw, feed, 300, tol, out var sp)[0];
        long msPlanar = sw.ElapsedMilliseconds;
        sw.Restart();
        var spatial = Process3.CutSpinning([Block()], saw, feed, 300, tol, out var ss, planar: false)[0];
        long msSpatial = sw.ElapsedMilliseconds;
        Assert.NotNull(sp.PlanarCutters);
        Assert.Null(sp.Cutters);
        Assert.NotNull(ss.Cutters);
        output.WriteLine($"planar {BlockVolume - planar.VolumeMm3:F6} mm³ in {msPlanar} ms, spatial {BlockVolume - spatial.VolumeMm3:F6} mm³ " +
                         $"in {msSpatial} ms ({ss.Cutters!.Hulls} hulls)");
        // Cut surface < 20 mm²; the planar sweep undercuts by ≤ SweepNm, the hulls deviate by ≤ SweepNm either way.
        Assert.Equal(planar.VolumeMm3, spatial.VolumeMm3, 20 * 2 * tol.SweepNm * 1e-6);
    }
}
