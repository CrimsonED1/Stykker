using Clipper2Lib;
using Stykker.NanoCut.Geometry2D;
using Xunit.Abstractions;
using NcFillRule = Stykker.NanoCut.Geometry2D.FillRule;

namespace Stykker.NanoCut.OracleTests;

/// <summary>
/// Phase 2 acceptance: on 10,000 random polygon pairs the NanoCut 2D kernel yields the same areas as
/// Clipper2 (which also works on Int64 grid coordinates). Both round crossing points to the grid, so the
/// areas may differ by the rounding of the crossing points only.
/// </summary>
public class Clipper2OracleTests(ITestOutputHelper output)
{
    private static readonly (BooleanOp Op, ClipType Clip)[] Ops =
    [
        (BooleanOp.Intersection, ClipType.Intersection),
        (BooleanOp.Union, ClipType.Union),
        (BooleanOp.Difference, ClipType.Difference),
        (BooleanOp.Xor, ClipType.Xor),
    ];

    private static readonly (NcFillRule Nc, Clipper2Lib.FillRule Clip)[] Rules =
    [
        (NcFillRule.EvenOdd, Clipper2Lib.FillRule.EvenOdd),
        (NcFillRule.NonZero, Clipper2Lib.FillRule.NonZero),
        (NcFillRule.Positive, Clipper2Lib.FillRule.Positive),
        (NcFillRule.Negative, Clipper2Lib.FillRule.Negative),
    ];

    [Fact]
    public void AreasMatchClipper2OnTenThousandRandomPolygons()
    {
        var rng = new Random(2026);
        double worstRelative = 0, worstNm2 = 0, worstRatio = 0;
        int nonEmpty = 0;
        for (int i = 0; i < 10_000; i++)
        {
            var subject = RandomContours(rng);
            var clip = RandomContours(rng);
            var (op, clipType) = Ops[i % Ops.Length];
            var (rule, clipRule) = Rules[(i / Ops.Length) % Rules.Length];

            var nc = Region2.FromContours(subject.Select(c => new Contour2(c)), rule)
                .Boolean(Region2.FromContours(clip.Select(c => new Contour2(c)), rule), op);
            var reference = Clipper.BooleanOp(clipType, ToPaths(subject), ToPaths(clip), clipRule);

            double ncTwiceArea = (double)nc.TwiceAreaNm2;
            double clipperTwiceArea = 2 * Clipper.Area(reference);
            double diff = Math.Abs(ncTwiceArea - clipperTwiceArea) / 2;

            // Each rounded crossing point moves the boundary by ≤ 0.71 nm (both libraries), so the area can
            // differ by at most ~1.42 nm × (length of the edges meeting there). Bound by perimeter × 1.5 nm.
            double perimeter = Perimeter(subject) + Perimeter(clip);
            double allowed = 1.5 * perimeter;
            Assert.True(diff <= allowed,
                $"case {i} ({op}, {rule}): NanoCut {ncTwiceArea / 2} nm², Clipper2 {clipperTwiceArea / 2} nm², allowed {allowed}");

            if (clipperTwiceArea > 0) nonEmpty++;
            // Relative agreement for results that are large compared to the grid (≥ 0.01 mm²).
            if (clipperTwiceArea / 2 >= 1e10)
                worstRelative = Math.Max(worstRelative, diff / (clipperTwiceArea / 2));
            worstNm2 = Math.Max(worstNm2, diff);
            worstRatio = Math.Max(worstRatio, diff / allowed);
        }
        output.WriteLine($"non-empty results: {nonEmpty}, worst |ΔA| = {worstNm2} nm², worst |ΔA|/allowed = {worstRatio:F3}, " +
                         $"worst relative (areas ≥ 0.01 mm²) = {worstRelative:E2}");
        Assert.True(nonEmpty > 5000);
    }

    [Fact]
    public void ExactOnAxisAlignedInput()
    {
        // Without crossings off the grid both kernels must agree exactly.
        var rng = new Random(5);
        for (int i = 0; i < 2_000; i++)
        {
            var subject = RandomRectangles(rng);
            var clip = RandomRectangles(rng);
            var (op, clipType) = Ops[i % Ops.Length];
            var nc = Region2.FromContours(subject.Select(c => new Contour2(c)), NcFillRule.NonZero)
                .Boolean(Region2.FromContours(clip.Select(c => new Contour2(c)), NcFillRule.NonZero), op);
            var reference = Clipper.BooleanOp(clipType, ToPaths(subject), ToPaths(clip), Clipper2Lib.FillRule.NonZero);
            Assert.Equal(2 * Clipper.Area(reference), (double)nc.TwiceAreaNm2);
        }
    }

    private static List<Vec2[]> RandomContours(Random rng)
    {
        var list = new List<Vec2[]>();
        int count = rng.Next(1, 3);
        for (int k = 0; k < count; k++)
        {
            int n = rng.Next(3, 16);
            long range = rng.Next(3) switch { 0 => 100, 1 => 1_000_000, _ => 1_000_000_000 };
            var pts = new Vec2[n];
            for (int i = 0; i < n; i++)
                pts[i] = new Vec2(rng.NextInt64(-range, range + 1), rng.NextInt64(-range, range + 1));
            list.Add(pts);
        }
        return list;
    }

    private static List<Vec2[]> RandomRectangles(Random rng)
    {
        var list = new List<Vec2[]>();
        for (int k = rng.Next(1, 4); k > 0; k--)
        {
            long x = rng.Next(-100, 100), y = rng.Next(-100, 100), w = rng.Next(1, 80), h = rng.Next(1, 80);
            var r = new Vec2[] { new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h) };
            if (rng.Next(2) == 0) Array.Reverse(r);
            list.Add(r);
        }
        return list;
    }

    private static Paths64 ToPaths(List<Vec2[]> contours)
    {
        var paths = new Paths64();
        foreach (var c in contours)
        {
            var p = new Path64(c.Length);
            foreach (var v in c) p.Add(new Point64(v.X, v.Y));
            paths.Add(p);
        }
        return paths;
    }

    private static double Perimeter(List<Vec2[]> contours)
    {
        double s = 0;
        foreach (var c in contours)
            for (int i = 0; i < c.Length; i++)
            {
                var d = c[(i + 1) % c.Length] - c[i];
                s += Math.Sqrt((double)d.X * d.X + (double)d.Y * d.Y);
            }
        return s;
    }
}
