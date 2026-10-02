using Stykker.NanoCut.Geometry2D;

namespace Stykker.NanoCut.Tests;

public class Boolean2Tests
{
    private static Region2 Square(long x, long y, long size) => Region2.Rectangle(new Vec2(x, y), new Vec2(x + size, y + size));

    private static Int128 Area2(Region2 r) => r.TwiceAreaNm2;

    [Fact]
    public void OverlappingSquaresAreExact()
    {
        var a = Square(0, 0, 10);
        var b = Square(5, 5, 10);
        Assert.Equal((Int128)2 * 25, Area2(a & b));
        Assert.Equal((Int128)2 * 175, Area2(a | b));
        Assert.Equal((Int128)2 * 75, Area2(a - b));
        Assert.Equal((Int128)2 * 150, Area2(a ^ b));
        var u = a | b;
        Assert.Single(u.Contours);
        Assert.Equal(8, u.Contours[0].Count);
        Assert.True(u.Contours[0].IsCounterClockwise);
    }

    [Fact]
    public void HoleIsClockwise()
    {
        var outer = Square(0, 0, 100);
        var inner = Square(25, 25, 50);
        var ring = outer - inner;
        Assert.Equal(2, ring.Contours.Count);
        Assert.Single(ring.Contours, c => c.IsCounterClockwise);
        Assert.Single(ring.Contours, c => !c.IsCounterClockwise);
        Assert.Equal((Int128)2 * (10000 - 2500), Area2(ring));
        Assert.Equal(1, ring.Locate(new Vec2(10, 10)));
        Assert.Equal(0, ring.Locate(new Vec2(50, 50)));
        Assert.Equal(-1, ring.Locate(new Vec2(25, 50)));
    }

    [Fact]
    public void IslandInsideHole()
    {
        var r = (Square(0, 0, 100) - Square(10, 10, 80)) | Square(40, 40, 20);
        Assert.Equal(3, r.Contours.Count);
        Assert.Equal((Int128)2 * (10000 - 6400 + 400), Area2(r));
        Assert.Equal(1, r.Locate(new Vec2(50, 50)));
        Assert.Equal(0, r.Locate(new Vec2(20, 20)));
    }

    [Fact]
    public void SharedEdgesAndTouchingCorners()
    {
        var a = Square(0, 0, 10);
        var b = Square(10, 0, 10);  // shares an edge
        var c = Square(10, 10, 10); // touches a corner of a
        var ab = a | b;
        Assert.Single(ab.Contours);
        Assert.Equal(4, ab.Contours[0].Count);
        Assert.Equal((Int128)2 * 200, Area2(ab));
        Assert.True((a & b).IsEmpty);
        var ac = a | c;
        Assert.Equal(2, ac.Contours.Count);
        Assert.Equal((Int128)2 * 200, Area2(ac));
    }

    [Fact]
    public void CollinearPartialOverlap()
    {
        var a = Region2.Rectangle(new Vec2(0, 0), new Vec2(10, 10));
        var b = Region2.Rectangle(new Vec2(3, 10), new Vec2(7, 20)); // sits on top edge
        var u = a | b;
        Assert.Single(u.Contours);
        Assert.Equal(8, u.Contours[0].Count);
        Assert.Equal((Int128)2 * 140, Area2(u));
    }

    [Fact]
    public void IdenticalRegions()
    {
        var a = Square(0, 0, 10);
        Assert.Equal(Area2(a), Area2(a & a));
        Assert.Equal(Area2(a), Area2(a | a));
        Assert.True((a - a).IsEmpty);
        Assert.True((a ^ a).IsEmpty);
    }

    [Fact]
    public void SelfIntersectingInputRespectsFillRule()
    {
        // Pentagram: the centre has winding 2.
        var pts = Enumerable.Range(0, 5).Select(i =>
        {
            double a = Math.PI / 2 + i * 4 * Math.PI / 5;
            return new Vec2((long)Math.Round(1e6 * Math.Cos(a)), (long)Math.Round(1e6 * Math.Sin(a)));
        }).ToArray();
        var nonZero = Region2.FromContours([new Contour2(pts)], FillRule.NonZero).Normalize();
        var evenOdd = Region2.FromContours([new Contour2(pts)], FillRule.EvenOdd).Normalize();
        Assert.Single(nonZero.Contours);
        Assert.Equal(10, nonZero.Contours[0].Count);
        Assert.Equal(5, evenOdd.Contours.Count(c => c.IsCounterClockwise));
        Assert.True(nonZero.AreaMm2 > evenOdd.AreaMm2);
        Assert.Equal(1, nonZero.Locate(new Vec2(0, 0)));
        Assert.Equal(0, evenOdd.Locate(new Vec2(0, 0)));
    }

    [Fact]
    public void BowtieWithPositiveAndNegativeRules()
    {
        var bowtie = new Contour2([new(0, 0), new(10, 10), new(10, 0), new(0, 10)]);
        var pos = Region2.FromContours([bowtie], FillRule.Positive).Normalize();
        var neg = Region2.FromContours([bowtie], FillRule.Negative).Normalize();
        var all = Region2.FromContours([bowtie], FillRule.NonZero).Normalize();
        Assert.Equal((Int128)2 * 25, Area2(pos));
        Assert.Equal((Int128)2 * 25, Area2(neg));
        Assert.Equal((Int128)2 * 50, Area2(all));
    }

    [Fact]
    public void CrossingPointIsRoundedToGrid()
    {
        // Diagonals of a 3 × 2 rectangle cross at (1.5, 1): rounded to (2, 1) (halves up).
        Assert.Equal(new Vec2(2, 1), BooleanKernel.RoundedIntersection(new(0, 0), new(3, 2), new(0, 2), new(3, 0)));
        Assert.Equal(-1, BooleanKernel.RoundDiv(-3, 2));
        Assert.Equal(-2, BooleanKernel.RoundDiv(-5, 2));
        Assert.Equal(2, BooleanKernel.RoundDiv(3, 2));
        Assert.Equal(1, BooleanKernel.RoundDiv(4, 3));
        Assert.Equal(-1, BooleanKernel.RoundDiv(-4, 3));
    }

    [Fact]
    public void HotPixelTestIsExact()
    {
        // Pixel around (0, 0) is [-0.5, 0.5)².
        Assert.True(BooleanKernel.SegmentHitsPixel(new(-5, 0), new(5, 0), new(0, 0)));
        Assert.True(BooleanKernel.SegmentHitsPixel(new(-1, -1), new(1, 1), new(0, 0)));
        Assert.False(BooleanKernel.SegmentHitsPixel(new(-5, 1), new(5, 1), new(0, 0)));
        // The open upper boundary y = 0.5 belongs to the pixel above: a horizontal segment on it hits (0, 1) only.
        Assert.False(BooleanKernel.SegmentHitsPixel(new(-4, 0), new(4, 0), new(0, 1)));
        Assert.False(BooleanKernel.SegmentHitsPixel(new(-3, 1), new(3, 1), new(0, 0)));
        // A segment crossing the corner region diagonally.
        Assert.True(BooleanKernel.SegmentHitsPixel(new(-1, 0), new(1, 1), new(0, 0)));
        Assert.True(BooleanKernel.SegmentHitsPixel(new(-1, 0), new(1, 1), new(0, 1)));
        Assert.False(BooleanKernel.SegmentHitsPixel(new(2, 2), new(9, 3), new(0, 0)));
    }

    [Fact]
    public void ManyThinOverlappingSliversStayRobust()
    {
        // Fan of long thin triangles rotating in tiny steps: dense near-degenerate crossings.
        var pieces = new List<Region2>();
        for (int i = 0; i < 400; i++)
        {
            double a = i * 1e-4;
            Vec2 P(double x, double y) => new((long)Math.Round(x * Math.Cos(a) - y * Math.Sin(a)), (long)Math.Round(x * Math.Sin(a) + y * Math.Cos(a)));
            pieces.Add(Region2.Polygon(P(0, 0), P(10_000_000, 0), P(10_000_000, 3)));
        }
        var u = Region2.UnionAll(pieces);
        AssertValid(u);
        Assert.True(u.AreaMm2 > 0);
    }

    [Fact]
    public void RandomPolygonsSatisfyAreaIdentities()
    {
        var rng = new Random(42);
        for (int iter = 0; iter < 400; iter++)
        {
            var a = RandomRegion(rng);
            var b = RandomRegion(rng);
            Int128 i = Area2(a & b), u = Area2(a | b), d = Area2(a - b), x = Area2(a ^ b);
            Int128 aa = Area2(a), bb = Area2(b);
            // Rounding a crossing point moves area by at most ~0.71 nm × adjacent edge length.
            double tol = 2 * (a.PerimeterMm + b.PerimeterMm) * Units.NmPerMm * 2;
            Assert.InRange((double)(u + i - aa - bb), -tol, tol);
            Assert.InRange((double)(d + i - aa), -tol, tol);
            Assert.InRange((double)(x - (u - i)), -tol, tol);
            Assert.True(i >= 0 && u >= 0 && d >= 0 && x >= 0);
            foreach (var r in new[] { a & b, a | b, a - b, a ^ b })
                AssertValid(r);
        }
    }

    internal static Region2 RandomRegion(Random rng)
    {
        int contours = rng.Next(1, 3);
        var list = new List<Contour2>();
        for (int k = 0; k < contours; k++)
        {
            int n = rng.Next(3, 12);
            long range = rng.Next(2) == 0 ? 1_000_000 : 50;
            var pts = new Vec2[n];
            for (int i = 0; i < n; i++) pts[i] = new Vec2(rng.NextInt64(-range, range), rng.NextInt64(-range, range));
            list.Add(new Contour2(pts));
        }
        return Region2.FromContours(list, rng.Next(2) == 0 ? FillRule.NonZero : FillRule.EvenOdd).Normalize();
    }

    /// <summary>Normalised output: no proper crossings between any two edges, no collinear vertices.</summary>
    internal static void AssertValid(Region2 r)
    {
        var edges = new List<(Vec2 A, Vec2 B)>();
        foreach (var c in r.Contours)
        {
            Assert.True(c.Count >= 3);
            for (int i = 0; i < c.Count; i++)
            {
                Assert.NotEqual(0, Predicates.Orient2D(c[(i + c.Count - 1) % c.Count], c[i], c[(i + 1) % c.Count]));
                edges.Add((c[i], c[(i + 1) % c.Count]));
            }
        }
        for (int i = 0; i < edges.Count; i++)
            for (int j = i + 1; j < edges.Count; j++)
            {
                var (p, q) = (edges[i], edges[j]);
                int d1 = Predicates.Orient2D(q.A, q.B, p.A), d2 = Predicates.Orient2D(q.A, q.B, p.B);
                int d3 = Predicates.Orient2D(p.A, p.B, q.A), d4 = Predicates.Orient2D(p.A, p.B, q.B);
                Assert.False(d1 * d2 < 0 && d3 * d4 < 0, $"edges {p} and {q} cross");
            }
    }
}
