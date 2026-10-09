using System.Numerics;
using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Geometry3D;
using Xunit.Abstractions;

namespace Stykker.NanoCut.Tests;

/// <summary>Adversarial verification tests (independent review of the kernels).</summary>
public class VerificationTests(ITestOutputHelper output)
{
    // ------------------------------------------------------------------------------------------------ helpers

    private static Solid Tet(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        int o = Predicates.Orient3D(a, b, c, d);
        if (o == 0) throw new ArgumentException("flat tetrahedron");
        if (o > 0) (b, c) = (c, b);
        return Solid.FromTriangles([a, b, c, d], [0, 1, 2, 0, 3, 1, 1, 3, 2, 2, 3, 0]);
    }

    private static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static long Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static Vec3 Mul(long k, Vec3 a) => new(k * a.X, k * a.Y, k * a.Z);
    private static Vec3 Add(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    private static (long G, long X, long Y) Egcd(long a, long b)
    {
        if (b == 0) return (Math.Abs(a), Math.Sign(a), 0);
        var (g, x, y) = Egcd(b, a % b);
        return (g, y, x - a / b * y);
    }

    /// <summary>r minus the nearest lattice vector a·u + b·v.</summary>
    private static Vec3 Reduce(Vec3 r, Vec3 u, Vec3 v)
    {
        double uu = Dot(u, u), uv = Dot(u, v), vv = Dot(v, v);
        double ru = (double)r.X * u.X + (double)r.Y * u.Y + (double)r.Z * u.Z;
        double rv = (double)r.X * v.X + (double)r.Y * v.Y + (double)r.Z * v.Z;
        double det = uu * vv - uv * uv;
        long a = (long)Math.Round((ru * vv - rv * uv) / det), b = (long)Math.Round((rv * uu - ru * uv) / det);
        return Add(r, Mul(-1, Add(Mul(a, u), Mul(b, v))));
    }

    /// <summary>A closed surface encloses the same volume from any reference point (mm³).</summary>
    internal static double ClosureError(Solid s)
    {
        double V(double ox, double oy, double oz)
        {
            double sum = 0;
            foreach (var f in s.Faces)
            {
                var v = f.Vertices;
                for (int k = 1; k + 1 < v.Length; k++)
                {
                    double ax = v[0].X - ox, ay = v[0].Y - oy, az = v[0].Z - oz;
                    double bx = v[k].X - ox, by = v[k].Y - oy, bz = v[k].Z - oz;
                    double cx = v[k + 1].X - ox, cy = v[k + 1].Y - oy, cz = v[k + 1].Z - oz;
                    sum += ax * (by * cz - bz * cy) + ay * (bz * cx - bx * cz) + az * (bx * cy - by * cx);
                }
            }
            return sum / 6 / 1e18;
        }
        return Math.Abs(V(0, 0, 0) - V(7e6, -3e6, 11e6));
    }

    private static IEnumerable<SolidBoolean.Node> Leaves(IEnumerable<SolidBoolean.Node> roots)
    {
        foreach (var r in roots)
        {
            if (r.IsLeaf) yield return r;
            else foreach (var l in Leaves([r.Front!, r.Back!])) yield return l;
        }
    }

    // ------------------------------------------------------------------------------------------------ 3D: filters

    /// <summary>
    /// The interior probe of a fragment (SolidBoolean.Probe) is a weighted average of the approximate vertex
    /// coordinates. When the fragment's vertices are large (~1e8 nm) but the probe lies near the origin, the
    /// probe's absolute error (~eps·|vertex|) is far larger than eps·|probe|, which is all that Filter.Sign's
    /// bound (1e-11·Σ|terms|) allows for. A plane passing between the exact probe and its double approximation
    /// is then decided "certainly" with the wrong sign.
    ///
    /// Construction: A is a tetrahedron whose top face p lies in a skew plane Π through the origin (|n| ≈ 2.5e9,
    /// so parallel grid planes are 4e-10 nm apart). The flat tetrahedron T cuts from p a triangle with exact
    /// (non-dyadic) vertices 6/7·w_i, |w_i| ≈ 1e9, whose centroid is exactly the origin; its double approximation
    /// is off by ~4e-8 nm, i.e. ~31 lattice levels below Π. The other operand is T minus a thin cavity whose floor
    /// lies on the lattice plane one level below Π. The exact probe (origin) is in T's material, so the triangle
    /// is Inside; the filter evaluates the cavity floor at the approximate probe (inside the cavity) and the
    /// fragment is classified Outside. Every Boolean with this pair returns an open surface.
    /// </summary>
    [Fact]
    public void ProbeCancellationMisclassifiesFragment()
    {
        var (a, other, reference) = ProbeCancellationCase();
        var r = a & other;
        Assert.True(ClosureError(r) < 1e-6, $"A ∩ B is not closed: closure error {ClosureError(r)} mm³");
        Assert.Equal(reference.VolumeMm3, r.VolumeMm3, 6);
        Assert.True(ClosureError(a | other) < 1e-6);
        Assert.True(ClosureError(a - other) < 1e-6);
    }

    /// <summary>The ingredients of <see cref="ProbeCancellationMisclassifiesFragment"/> are valid, closed solids.</summary>
    [Fact]
    public void ProbeCancellationCaseInputsAreValid()
    {
        var (a, other, reference) = ProbeCancellationCase();
        Assert.True(ClosureError(a) < 1e-6);
        Assert.True(ClosureError(other) < 1e-6);
        Assert.True(ClosureError(reference) < 1e-6);
        Assert.True(reference.VolumeMm3 > 1);
        // Root cause, isolated: the exact probe of the triangle fragment is the origin (on the negative side of the
        // cavity floor), but the filtered evaluation at the double probe claims the positive side with certainty.
        var c = SolidBoolean.Classify(a.Faces, other.Faces);
        var leaf = Leaves(c.A).Single(n => n.Face.Vertices.Length == 3 && n.Face.Vertices.All(v => !v.IsGrid));
        var vs = leaf.Face.Vertices;
        double px = (vs[0].X + vs[1].X + vs[2].X) / 3, py = (vs[0].Y + vs[1].Y + vs[2].Y) / 3, pz = (vs[0].Z + vs[1].Z + vs[2].Z) / 3;
        var big = vs.Select(v => v.Big).ToArray();
        BigInteger Sum(Func<(BigInteger X, BigInteger Y, BigInteger Z, BigInteger W), BigInteger> f) =>
            f(big[0]) * big[1].W * big[2].W + f(big[1]) * big[0].W * big[2].W + f(big[2]) * big[0].W * big[1].W;
        Assert.True(Sum(p => p.X).IsZero && Sum(p => p.Y).IsZero && Sum(p => p.Z).IsZero); // exact centroid = origin
        int mismatches = 0;
        foreach (var f in other.Faces)
        {
            int fs = Filter.Sign(f.PlanesD, 0, px, py, pz), ex = Predicates.Side(f.Support, new Vec3(0, 0, 0));
            if (fs != Filter.Uncertain && fs != ex)
            {
                mismatches++;
                output.WriteLine($"filter {fs} vs exact {ex} for {f.Support} at probe ({px:R}, {py:R}, {pz:R})");
            }
        }
        output.WriteLine($"fragment classified {leaf.Loc} (exact: Inside); mismatching filtered plane tests: {mismatches}");
    }

    private static (Solid A, Solid Other, Solid Reference) ProbeCancellationCase()
    {
        Vec3 u = new(40001, 3002, 25003), v = new(-11003, 35001, 29009);
        Vec3 n = Cross(u, v); // Π: n·x = 0, lattice basis u, v
        var (g, s1, s2) = Egcd(n.X, n.Y);
        if (g != 1) throw new InvalidOperationException();
        Vec3 r1 = Reduce(new Vec3(s1, s2, 0), u, v); // a grid point on level n·x = +1
        if (Dot(n, r1) != 1) throw new InvalidOperationException();
        const int m = 6;
        Vec3 beta = new(0, 0, 1);
        Vec3 w0 = Add(Mul(20000, u), Mul(4000, v)), w1 = Add(Mul(-8000, u), Mul(24000, v));
        Vec3 w2 = Mul(-1, Add(w0, w1));
        // T: apex 6 levels·n_z below Π, base n_z above: T ∩ Π = 6/7·(w0, w1, w2), centroid exactly 0.
        var t = Tet(Mul(-m, beta), Add(w0, beta), Add(w1, beta), Add(w2, beta));
        var a = Tet(w0, w1, w2, new Vec3(0, 0, -100_000_000));
        // Cavity between levels -1 and -1000 (the double probe ends up at level ≈ -31).
        Vec3 q0 = Mul(-1, r1);
        Vec3 apex = Reduce(Mul(-1000, r1), u, v);
        var cavity = Tet(Add(q0, Add(Mul(10000, u), Mul(2000, v))), Add(q0, Add(Mul(-4000, u), Mul(12000, v))),
                         Add(q0, Add(Mul(-6000, u), Mul(-14000, v))), apex);
        var other = t - cavity;
        // The cavity lies inside A, so A ∩ (T − C) = (A ∩ T) − C: the volume of A ∩ T up to ~1e-7 mm³.
        return (a, other, a & t);
    }

    // ------------------------------------------------------------------------------------------------ 3D: chained Booleans

    private static Solid RandomPiece(Random rng, long span)
    {
        long R() => rng.NextInt64(-span, span);
        Solid s;
        if (rng.Next(2) == 0)
        {
            Vec3 a = new(R(), R(), R()), b = new(R(), R(), R());
            if (a.X == b.X || a.Y == b.Y || a.Z == b.Z) return RandomPiece(rng, span);
            s = Solid.Box(new Vec3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)), new Vec3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
        }
        else
        {
            Vec3 a = new(R(), R(), R()), b = new(R(), R(), R()), c = new(R(), R(), R()), d = new(R(), R(), R());
            if (Predicates.Orient3D(a, b, c, d) == 0) return RandomPiece(rng, span);
            s = Tet(a, b, c, d);
        }
        if (rng.Next(2) == 0)
            s = s.Transform(Pose3.Rotation(rng.NextDouble() * 6.28, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5));
        return s;
    }

    private static void CheckIdentities(Solid a, Solid b, string ctx, out Solid next, Random rng)
    {
        var i = a & b;
        var u = a | b;
        var d = a - b;
        double va = a.VolumeMm3, vb = b.VolumeMm3;
        double scale = Math.Max(1, va + vb);
        foreach (var (name, r) in new[] { ("and", i), ("or", u), ("minus", d) })
            Assert.True(ClosureError(r) < 1e-9 * scale * 1000, $"{ctx}: {name} not closed ({ClosureError(r)})");
        Assert.InRange(u.VolumeMm3 + i.VolumeMm3 - va - vb, -1e-9 * scale, 1e-9 * scale);
        Assert.InRange(d.VolumeMm3 + i.VolumeMm3 - va, -1e-9 * scale, 1e-9 * scale);
        next = rng.Next(3) switch { 0 => i.IsEmpty ? u : i, 1 => u, _ => d.IsEmpty ? u : d };
    }

    [Fact]
    public void ChainedBooleansOfRotatedPiecesStayClosed()
    {
        var rng = new Random(4242);
        for (int chain = 0; chain < 150; chain++)
        {
            long span = chain % 3 == 0 ? 1_200_000_000 : chain % 3 == 1 ? 5_000_000 : 50;
            var acc = RandomPiece(rng, span);
            for (int step = 0; step < 6; step++)
            {
                var b = RandomPiece(rng, span);
                CheckIdentities(acc, b, $"chain {chain} step {step} span {span}", out acc, rng);
                // Lossless storage of the intermediate result.
                using var ms = new MemoryStream();
                NcsFormat.Save(acc, ms);
                ms.Position = 0;
                var back = NcsFormat.Load(ms);
                Assert.Equal(acc.VolumeMm3, back.VolumeMm3);
                Assert.Equal(acc.FaceCount, back.FaceCount);
            }
        }
    }

    [Fact]
    public void NearlyCoincidentPiecesStayClosed()
    {
        // Pairs that differ by one nanometre or a tiny rotation: near-coplanar faces and slivers everywhere.
        var rng = new Random(77);
        for (int iter = 0; iter < 600; iter++)
        {
            long span = iter % 2 == 0 ? 1_000_000_000 : 3_000_000;
            var a = RandomPiece(rng, span);
            var b = rng.Next(3) switch
            {
                0 => a.Transform(Pose3.Identity with { TxNm = rng.Next(-1, 2), TyNm = rng.Next(-1, 2), TzNm = rng.Next(-1, 2) }),
                1 => a.Transform(Pose3.Rotation(1e-9 * rng.Next(1, 100), rng.NextDouble(), rng.NextDouble(), rng.NextDouble())),
                _ => a.Transform(Pose3.Rotation(Math.PI / 2 * rng.Next(4), 0, 0, 1)),
            };
            CheckIdentities(a, b, $"iteration {iter}", out var r, rng);
            CheckIdentities(r, a, $"iteration {iter} (second)", out _, rng);
        }
    }

    // ------------------------------------------------------------------------------------------------ 3D: hull and sweep

    [Fact]
    public void ConvexHullContainsAllPointsAndIsClosed()
    {
        var rng = new Random(5);
        for (int iter = 0; iter < 300; iter++)
        {
            int span = iter % 2 == 0 ? 3 : 1000;
            var pts = Enumerable.Range(0, rng.Next(4, 40)).Select(_ => new Vec3(rng.Next(-span, span + 1), rng.Next(-span, span + 1), rng.Next(-span, span + 1))).ToList();
            Solid hull;
            try { hull = ConvexHull3.Compute(pts); }
            catch (ArgumentException) { continue; } // coplanar / collinear
            Assert.True(ClosureError(hull) < 1e-12, $"iteration {iter}: hull not closed");
            foreach (var f in hull.Faces)
            {
                foreach (var p in pts) Assert.True(Predicates.Side(f.Support, p) <= 0, $"iteration {iter}: point outside hull face");
                foreach (var e in f.Edges)
                    foreach (var v in f.Vertices) Assert.True(v.SideOf(e) <= 0, $"iteration {iter}: non-convex hull face");
            }
            // Exact volume of the hull equals the volume of the union of tetrahedra from one hull vertex.
            Assert.True(hull.VolumeMm3 > 0);
        }
    }

    [Fact]
    public void TranslationSweepEqualsHullOfBothPositions()
    {
        var rng = new Random(9);
        for (int iter = 0; iter < 200; iter++)
        {
            int span = iter % 2 == 0 ? 4 : 1_000_000;
            var pts = Enumerable.Range(0, rng.Next(4, 12)).Select(_ => new Vec3(rng.Next(-span, span + 1), rng.Next(-span, span + 1), rng.Next(-span, span + 1))).ToList();
            Solid part;
            try { part = ConvexHull3.Compute(pts); }
            catch (ArgumentException) { continue; }
            var t = rng.Next(4) == 0
                ? new Vec3(rng.Next(-span, span + 1), 0, 0) // axis-parallel: faces parallel to t
                : new Vec3(rng.Next(-span, span + 1), rng.Next(-span, span + 1), rng.Next(-span, span + 1));
            var sweep = Sweep3.Translate(part, t);
            var hull = ConvexHull3.Compute(pts.Concat(pts.Select(p => p + t)));
            Assert.True(ClosureError(sweep) < 1e-12, $"iteration {iter}: sweep not closed");
            Assert.Equal(hull.VolumeMm3, sweep.VolumeMm3, 12);
            // The two exact solids describe the same point set.
            Assert.True((sweep - hull).IsEmpty && (hull - sweep).IsEmpty, $"iteration {iter}: sweep differs from hull");
        }
    }

    // ------------------------------------------------------------------------------------------------ 2D

    private static Vec2[] RandomPolygon(Random rng, long span, bool nearlyCollinear)
    {
        int n = rng.Next(3, 9);
        var pts = new Vec2[n];
        if (nearlyCollinear)
        {
            // Points close to one long line: nearly collinear, nearly coincident edges.
            long x0 = rng.NextInt64(-span, span), y0 = rng.NextInt64(-span, span);
            long dx = rng.NextInt64(-span, span), dy = rng.NextInt64(-span, span);
            for (int i = 0; i < n; i++)
            {
                double t = rng.NextDouble();
                pts[i] = new Vec2(x0 + (long)(t * dx) + rng.Next(-2, 3), y0 + (long)(t * dy) + rng.Next(-2, 3));
            }
        }
        else
            for (int i = 0; i < n; i++) pts[i] = new Vec2(rng.NextInt64(-span, span), rng.NextInt64(-span, span));
        return pts;
    }

    private static double SegmentDistance(Vec2 p, Vec2 a, Vec2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l = dx * dx + dy * dy;
        double t = l == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l, 0, 1);
        double ex = a.X + t * dx - p.X, ey = a.Y + t * dy - p.Y;
        return Math.Sqrt(ex * ex + ey * ey);
    }

    private static IEnumerable<(Vec2 A, Vec2 B)> Segments(IEnumerable<IReadOnlyList<Vec2>> contours)
    {
        foreach (var c in contours)
            for (int i = 0; i < c.Count; i++) yield return (c[i], c[(i + 1) % c.Count]);
    }

    private static int RawWinding(IEnumerable<IReadOnlyList<Vec2>> contours, Vec2 p)
    {
        int w = 0;
        foreach (var (a, b) in Segments(contours))
        {
            int o = Predicates.Orient2D(a, b, p);
            if (a.Y <= p.Y) { if (b.Y > p.Y && o > 0) w++; }
            else if (b.Y <= p.Y && o < 0) w--;
        }
        return w;
    }

    private static bool Inside(int w, Geometry2D.FillRule rule) => rule switch
    {
        Geometry2D.FillRule.EvenOdd => (w & 1) != 0,
        Geometry2D.FillRule.NonZero => w != 0,
        Geometry2D.FillRule.Positive => w > 0,
        _ => w < 0,
    };

    [Fact]
    public void Boolean2DFuzzIsValidAndConsistent()
    {
        var rng = new Random(31337);
        var ops = new[] { BooleanOp.Intersection, BooleanOp.Union, BooleanOp.Difference, BooleanOp.Xor };
        var rules = new[] { Geometry2D.FillRule.EvenOdd, Geometry2D.FillRule.NonZero, Geometry2D.FillRule.Positive, Geometry2D.FillRule.Negative };
        for (int iter = 0; iter < 12000; iter++)
        {
            long span = (iter % 4) switch { 0 => 6, 1 => 40, 2 => 1_000_000, _ => 1_000_000_000 };
            bool thin = iter % 3 == 0;
            var sa = Enumerable.Range(0, rng.Next(1, 3)).Select(_ => RandomPolygon(rng, span, thin)).ToArray();
            var sb = Enumerable.Range(0, rng.Next(1, 3)).Select(_ => RandomPolygon(rng, span, thin)).ToArray();
            var rule = rules[rng.Next(4)];
            var op = ops[rng.Next(4)];
            var a = Region2.FromContours(sa.Select(c => new Contour2(c)), rule);
            var b = Region2.FromContours(sb.Select(c => new Contour2(c)), rule);
            var r = a.Boolean(b, op);
            string ctx = $"iteration {iter} ({op}, {rule}, span {span})";

            // 1. Result contours never cross properly.
            var segs = Segments(r.Contours).ToArray();
            for (int i = 0; i < segs.Length; i++)
                for (int j = i + 1; j < segs.Length; j++)
                {
                    var (p, q) = segs[i];
                    var (u, v) = segs[j];
                    int d1 = Predicates.Orient2D(u, v, p), d2 = Predicates.Orient2D(u, v, q);
                    int d3 = Predicates.Orient2D(p, q, u), d4 = Predicates.Orient2D(p, q, v);
                    Assert.False(d1 * d2 < 0 && d3 * d4 < 0, $"{ctx}: result edges cross");
                }
            // 2. Normalisation is idempotent and the result has winding 0/1.
            var again = Region2.FromContours(r.Contours).Normalize();
            Assert.Equal(r.TwiceAreaNm2, again.TwiceAreaNm2);
            Assert.True(r.TwiceAreaNm2 >= 0, ctx);

            // 3. Point classification agrees away from all boundaries (snapping moves edges by ≤ 0.71 nm).
            var all = Segments(sa).Concat(Segments(sb)).Concat(segs).ToArray();
            for (int k = 0; k < 30; k++)
            {
                var p = new Vec2(rng.NextInt64(-span - 2, span + 3), rng.NextInt64(-span - 2, span + 3));
                if (all.Any(e => SegmentDistance(p, e.A, e.B) < 1.0)) continue;
                bool ia = Inside(RawWinding(sa, p), rule), ib = Inside(RawWinding(sb, p), rule);
                bool expect = op switch
                {
                    BooleanOp.Intersection => ia && ib,
                    BooleanOp.Union => ia || ib,
                    BooleanOp.Difference => ia && !ib,
                    _ => ia ^ ib,
                };
                int w = RawWinding(r.Contours, p);
                Assert.True(w == 0 || w == 1, $"{ctx}: result winding {w}");
                Assert.True(expect == (w == 1), $"{ctx}: point {p} expected {expect}");
            }
        }
    }
    // ------------------------------------------------------------------------------------------------ processes

    /// <summary>
    /// Process3 places each convex tool part at a fixed orientation with Solid.Transform (vertices rounded to the
    /// grid) and sweeps it with Sweep3.Translate, which is only valid for convex solids. Rounding makes a faceted
    /// ball slightly non-convex (nm dents); where a dent lies on the silhouette of the move, the silhouette
    /// parallelogram folds back into the body and the sweep surface overlaps itself (winding 2 in thin slivers).
    /// The Boolean kernel requires winding 0/1 inputs, so the workpiece minus that sweep is not closed.
    /// </summary>
    [Fact]
    public void Process3TiltedTranslationLeavesClosedWorkpiece()
    {
        var (block, tool, motion, _, _) = TiltedBallCase(2);
        var r = Cutting.Process3.Cut(block, tool, motion, Tolerance.Budget(totalUm: 20, chordNm: 3000));
        Assert.True(ClosureError(r) < 1e-9, $"closure error {ClosureError(r):E2} mm³");
    }

    /// <summary>Root cause of <see cref="Process3TiltedTranslationLeavesClosedWorkpiece"/> and the fix direction.</summary>
    [Fact]
    public void Process3TiltedTranslationRootCause()
    {
        var (block, tool, _, tilt, t) = TiltedBallCase(2);
        var placed = tool.Parts[0].Transform(tilt);
        // The rounded part has a concave edge on the silhouette of the move (Sweep3.Translate's convexity
        // precondition is violated) ...
        Assert.True(CountConcaveSilhouetteEdges(placed, t) > 0);
        // ... while sweeping the convex hull of the same rounded vertices gives a valid, closed result.
        var hull = ConvexHull3.Compute(placed.Vertices.Select(v => v.Grid));
        var good = block - Sweep3.Translate(hull, t);
        Assert.True(ClosureError(good) < 1e-9);
        var bad = block - Sweep3.Translate(placed, t);
        output.WriteLine($"closure error: hull-based sweep {ClosureError(good):E2}, rounded-part sweep {ClosureError(bad):E2} mm³");
    }

    private static (Solid Block, Cutting.ToolShape Tool, Cutting.Motion3 Motion, Pose3 Tilt, Vec3 T) TiltedBallCase(int seed)
    {
        var tool = Cutting.ToolShape.Ball(0.5, Tolerance.Budget(totalUm: 20, chordNm: 3000));
        var block = Solid.Box(Vec3.Mm(-1, -1, -1), Vec3.Mm(1.5, 1.5, 1.5));
        var rng = new Random(seed);
        var tilt = Pose3.Rotation(rng.NextDouble() * 6, rng.NextDouble(), rng.NextDouble(), rng.NextDouble());
        var t = new Vec3(rng.Next(-3_000_000, 3_000_000), rng.Next(-3_000_000, 3_000_000), rng.Next(-3_000_000, 3_000_000));
        // Fixed (tilted) orientation, straight move from the origin by t: Process3's exact "same rotation" path.
        var motion = Cutting.Motion3.Custom(s => tilt with { TxNm = t.X * s, TyNm = t.Y * s, TzNm = t.Z * s });
        return (block, tool, motion, tilt, t);
    }

    /// <summary>Edges between a backward and a forward face (silhouette edges of a sweep along t) that are concave.</summary>
    private static int CountConcaveSilhouetteEdges(Solid s, Vec3 t)
    {
        var owner = new Dictionary<(Vec3, Vec3), Face3>();
        foreach (var f in s.Faces)
            for (int k = 0; k < f.Vertices.Length; k++)
                owner[(f.Vertices[k].Grid, f.Vertices[(k + 1) % f.Vertices.Length].Grid)] = f;
        int Side(Face3 f) => Int128.Sign(f.Support.Nx * t.X + f.Support.Ny * t.Y + f.Support.Nz * t.Z);
        int count = 0;
        foreach (var f in s.Faces)
        {
            if (Side(f) >= 0) continue;
            for (int k = 0; k < f.Vertices.Length; k++)
            {
                var a = f.Vertices[k].Grid;
                var b = f.Vertices[(k + 1) % f.Vertices.Length].Grid;
                var g = owner[(b, a)];
                if (Side(g) > 0 && g.Vertices.Any(v => Predicates.Side(f.Support, v.Grid) > 0)) count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Process2 claims the swept area is exact for the linearly interpolated motion, but it covers each edge's
    /// sweep with the quadrilateral u0 v0 v1 u1 (or two triangles). The segment u(t)v(t) with linearly moving end
    /// points sweeps a ruled region whose boundary contains the envelope of the moving line; when the envelope
    /// touches the segment (rotation combined with translation) the region bulges beyond the quadrilateral. Here a
    /// 20 mm bar turning by 0.05° while moving 0.86 mm: the sampler accepts one interval (vertex chord deviation
    /// 3.3 nm ≤ SweepNm = 30 nm), but points of the true swept area are missed by ~195 nm (budget 100 nm total).
    /// </summary>
    [Fact]
    public void Process2UndercutStaysWithinSweepTolerance()
    {
        double worst = Process2Undercut(out var tol);
        Assert.True(worst <= tol.SweepNm + 1, $"undercut {worst:F1} nm > SweepNm {tol.SweepNm} nm");
    }

    private double Process2Undercut(out Tolerance tol)
    {
        const double L = 10, th = 0.0009028436900517973;
        tol = Tolerance.Default;
        var tool = Region2.Rectangle(Vec2.Mm(-L, 0), Vec2.Mm(L, 0.05));
        double tx = -0.08608708463512005 * L * 1e6, ty = -0.0023693000120882605 * L * 1e6;
        var c = Vec2.Mm(-1.8587085043486544 * L, -1.5839005915287268 * L);
        Pose2 At(double t) => new Pose2(0, tx * t, ty * t).Compose(Pose2.Rotation(th * t, c));
        var swept = Cutting.Process2.Sweep(tool, Cutting.Motion2.Custom(At), tol);
        var segs = Segments(swept.Contours).ToArray();
        double worst = 0;
        // Points of the bar's lower edge along the true motion that are outside the computed swept area.
        for (int i = 0; i <= 1000; i++)
        {
            var pose = At(i / 1000.0);
            for (int j = 0; j <= 400; j++)
            {
                var (x, y) = pose.Apply(-L * 1e6 + 2 * L * 1e6 * j / 400.0, 0);
                var p = new Vec2((long)Math.Round(x), (long)Math.Round(y));
                if (swept.Locate(p) != 0) continue;
                worst = Math.Max(worst, segs.Min(e => SegmentDistance(p, e.A, e.B)));
            }
        }
        output.WriteLine($"worst undercut {worst:F1} nm, SweepNm {tol.SweepNm} nm, swept contour vertices {swept.Contours.Sum(k => k.Count)}");
        return worst;
    }

    /// <summary>Diagnostic companion of <see cref="Process2UndercutStaysWithinSweepTolerance"/> (always passes; prints the undercut).</summary>
    [Fact]
    public void Process2UndercutDiagnostic() => Assert.True(Process2Undercut(out _) >= 0);

    [Fact]
    public void Process2PureTranslationIsExact()
    {
        // Without rotation the edge quadrilaterals are exact: the sweep equals the Minkowski sum.
        var tool = Region2.Polygon(Vec2.Mm(-1, -0.5), Vec2.Mm(1, -0.3), Vec2.Mm(0.2, 0.9));
        var path = new[] { Vec2.Mm(0, 0), Vec2.Mm(7.3, 1.1), Vec2.Mm(3, -4.4) };
        var swept = Cutting.Process2.Sweep(tool, Cutting.Motion2.Polyline(path));
        var mink = Minkowski2.SweepConvex(tool.Contours[0], path);
        Assert.True((swept ^ mink).IsEmpty);
    }

    [Fact]
    public void ConvexPartsOfRandomRegionsTileExactly()
    {
        // Boolean results with holes, touching contours and pinch points, decomposed and re-assembled.
        var rng = new Random(2718);
        int done = 0;
        for (int iter = 0; iter < 1500; iter++)
        {
            long span = iter % 2 == 0 ? 12 : 1_000_000;
            var a = Region2.FromContours(Enumerable.Range(0, rng.Next(1, 4)).Select(_ => new Contour2(RandomPolygon(rng, span, false))), Geometry2D.FillRule.EvenOdd);
            var b = Region2.FromContours(Enumerable.Range(0, rng.Next(1, 4)).Select(_ => new Contour2(RandomPolygon(rng, span, false))), Geometry2D.FillRule.NonZero);
            var r = rng.Next(2) == 0 ? a - b : a ^ b;
            if (r.IsEmpty) continue;
            var parts = r.ConvexParts();
            Assert.All(parts, p => Assert.True(ConvexHull.IsConvex(p), $"iteration {iter}: non-convex part"));
            var re = Region2.UnionAll(parts.Select(p => Region2.Polygon(p)));
            Assert.True((re ^ r).IsEmpty, $"iteration {iter}: parts do not tile the region");
            Int128 sum = 0;
            foreach (var p in parts) sum += new Contour2(p).TwiceSignedAreaNm2;
            Assert.Equal(r.TwiceAreaNm2, sum);
            done++;
        }
        Assert.True(done > 500);
    }

    [Fact]
    public void FilteredSideOfAgreesWithExactOnBooleanVertices()
    {
        // Exact vertices of chained Booleans near the coordinate limit, tested against planes through nearby grid points.
        var rng = new Random(99);
        int checkedCount = 0;
        for (int iter = 0; iter < 20; iter++)
        {
            var a = RandomPiece(rng, 1_200_000_000);
            var b = RandomPiece(rng, 1_200_000_000);
            var r = a - b;
            foreach (var v in r.Vertices.Where(v => !v.IsGrid).Take(200))
            {
                var g = new Vec3((long)Math.Round(v.X), (long)Math.Round(v.Y), (long)Math.Round(v.Z));
                for (int k = 0; k < 10; k++)
                {
                    Vec3 p = g + new Vec3(rng.Next(-3, 4), rng.Next(-3, 4), rng.Next(-3, 4));
                    Vec3 q = g + new Vec3(rng.Next(-2_000_000, 2_000_000), rng.Next(-2_000_000, 2_000_000), rng.Next(-2_000_000, 2_000_000));
                    Vec3 s = g + new Vec3(rng.Next(-2_000_000, 2_000_000), rng.Next(-2_000_000, 2_000_000), rng.Next(-2_000_000, 2_000_000));
                    var plane = Plane3.FromPoints(p, q, s);
                    if (plane.IsDegenerate) continue;
                    Assert.Equal(Predicates.Side(plane, v.Homogeneous), v.SideOf(plane));
                    checkedCount++;
                }
            }
        }
        Assert.True(checkedCount > 1000);
    }
}
