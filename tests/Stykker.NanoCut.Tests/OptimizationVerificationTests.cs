using System.Numerics;
using Stykker.NanoCut.Geometry3D;
using Xunit.Abstractions;

namespace Stykker.NanoCut.Tests;

/// <summary>
/// Adversarial verification of the first optimisation round (filtered hull side test, coplanar grouping in the hull,
/// binary GCD in Plane3.Canonical, Int256 fast plane intersection, Separated shortcut in SolidBoolean, BVH arrays).
/// Every check compares against an independent BigInteger / brute-force / analytic reference.
/// </summary>
public class OptimizationVerificationTests(ITestOutputHelper output)
{
    private const long M = Units.MaxCoordinate;
    private static readonly BigInteger Two255 = BigInteger.One << 255;
    private static readonly BigInteger Two256 = BigInteger.One << 256;

    // ------------------------------------------------------------------------------------------------ helpers

    private static BigInteger Big(Int256 v) => v.ToInt384();

    private static Int256 FromBig(BigInteger v)
    {
        if (v < 0) v += Two256;
        var mask = (BigInteger)ulong.MaxValue;
        return new Int256((ulong)(v & mask), (ulong)((v >> 64) & mask), (ulong)((v >> 128) & mask), (ulong)((v >> 192) & mask));
    }

    /// <summary>Random value with |v| &lt; 2^bits, log-uniform bit length, with boundary values mixed in.</summary>
    private static BigInteger RandBits(Random rng, int bits)
    {
        BigInteger max = (BigInteger.One << bits) - 1;
        int sign = rng.Next(2) == 0 ? 1 : -1;
        switch (rng.Next(10))
        {
            case 0: return sign * max;
            case 1: return sign * (BigInteger.One << (bits - 1));
            case 2: return rng.Next(3) - 1;
            case 3: return sign * (max - rng.Next(1000));
        }
        int len = rng.Next(1, bits + 1);
        var bytes = new byte[len / 8 + 2];
        rng.NextBytes(bytes);
        bytes[^1] = 0;
        var v = new BigInteger(bytes) & ((BigInteger.One << len) - 1);
        return sign * v;
    }

    private static Int128 R128(Random rng, int bits) => (Int128)RandBits(rng, bits);

    /// <summary>Reference intersection by Cramer's rule in BigInteger, normalised to W &gt; 0.</summary>
    private static (BigInteger X, BigInteger Y, BigInteger Z, BigInteger W)? RefIntersect(Plane3 p, Plane3 q, Plane3 r)
    {
        BigInteger[,] a =
        {
            { p.Nx, p.Ny, p.Nz, -(BigInteger)p.D },
            { q.Nx, q.Ny, q.Nz, -(BigInteger)q.D },
            { r.Nx, r.Ny, r.Nz, -(BigInteger)r.D },
        };
        BigInteger Det(int c0, int c1, int c2) =>
            a[0, c0] * (a[1, c1] * a[2, c2] - a[1, c2] * a[2, c1])
            - a[0, c1] * (a[1, c0] * a[2, c2] - a[1, c2] * a[2, c0])
            + a[0, c2] * (a[1, c0] * a[2, c1] - a[1, c1] * a[2, c0]);
        var w = Det(0, 1, 2);
        if (w.IsZero) return null;
        var x = Det(3, 1, 2);
        var y = Det(0, 3, 2);
        var z = Det(0, 1, 3);
        return w.Sign < 0 ? (-x, -y, -z, -w) : (x, y, z, w);
    }

    private static void AssertSamePoint((BigInteger X, BigInteger Y, BigInteger Z, BigInteger W)? expected, HomogeneousPoint3? actual)
    {
        Assert.Equal(expected.HasValue, actual.HasValue);
        if (expected is not { } e) return;
        var h = actual!.Value;
        Assert.Equal(e.X, (BigInteger)h.X);
        Assert.Equal(e.Y, (BigInteger)h.Y);
        Assert.Equal(e.Z, (BigInteger)h.Z);
        Assert.Equal(e.W, (BigInteger)h.W);
    }

    /// <summary>Six times the exact volume relative to origin o, as a reduced fraction (independent of fragmentation).</summary>
    internal static (BigInteger Num, BigInteger Den) Volume6(Solid s, long ox = 0, long oy = 0, long oz = 0)
    {
        BigInteger num = 0, den = 1;
        foreach (var f in s.Faces)
        {
            var v = f.Vertices;
            var a = Shift(v[0].Big);
            for (int i = 1; i + 1 < v.Length; i++)
            {
                var b = Shift(v[i].Big);
                var c = Shift(v[i + 1].Big);
                BigInteger tn = a.X * (b.Y * c.Z - b.Z * c.Y) - a.Y * (b.X * c.Z - b.Z * c.X) + a.Z * (b.X * c.Y - b.Y * c.X);
                if (tn.IsZero) continue;
                BigInteger td = a.W * b.W * c.W;
                num = num * td + tn * den;
                den *= td;
                var g = BigInteger.GreatestCommonDivisor(num, den);
                if (!g.IsOne && !g.IsZero) { num /= g; den /= g; }
            }
        }
        if (num.IsZero) den = 1;
        return (num, den);

        (BigInteger X, BigInteger Y, BigInteger Z, BigInteger W) Shift((BigInteger X, BigInteger Y, BigInteger Z, BigInteger W) p) =>
            (p.X - ox * p.W, p.Y - oy * p.W, p.Z - oz * p.W, p.W);
    }

    private static (BigInteger Num, BigInteger Den) Add((BigInteger Num, BigInteger Den) a, (BigInteger Num, BigInteger Den) b, int sign = 1)
    {
        var n = a.Num * b.Den + sign * b.Num * a.Den;
        var d = a.Den * b.Den;
        var g = BigInteger.GreatestCommonDivisor(n, d);
        if (n.IsZero) return (0, 1);
        return (n / g, d / g);
    }

    /// <summary>Exact closure: the enclosed volume does not depend on the reference point.</summary>
    private static void AssertClosed(Solid s, string what)
    {
        var v0 = Volume6(s);
        var v1 = Volume6(s, 7_000_001, -3_000_017, 11_000_003);
        Assert.True(v0 == v1, $"{what}: surface not closed (volume depends on the reference point)");
    }

    private static Solid Tet(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        int o = Predicates.Orient3D(a, b, c, d);
        if (o == 0) throw new ArgumentException("flat tetrahedron");
        if (o > 0) (b, c) = (c, b);
        return Solid.FromTriangles([a, b, c, d], [0, 1, 2, 0, 3, 1, 1, 3, 2, 2, 3, 0]);
    }

    // ------------------------------------------------------------------------------------------------ Int256

    [Fact]
    public void Int256OperationsMatchBigIntegerAndThrowExactlyOnOverflow()
    {
        var rng = new Random(256);
        for (int i = 0; i < 40_000; i++)
        {
            // Product of two Int128 (never overflows unless an operand is Int128.MinValue).
            Int128 a = R128(rng, 127), b = R128(rng, 127);
            if (i % 997 == 0) a = Int128.MinValue;
            if (a == Int128.MinValue || b == Int128.MinValue)
                Assert.Throws<OverflowException>(() => Int256.Product(a, b));
            else
                Assert.Equal((BigInteger)a * b, Big(Int256.Product(a, b)));

            // Int256 · Int128.
            var xb = RandBits(rng, 255);
            if (i % 1009 == 0) xb = -Two255;
            var x = FromBig(xb);
            Assert.Equal(xb, Big(x));
            Int128 m = R128(rng, rng.Next(2) == 0 ? 127 : 40);
            var prod = xb * (BigInteger)m;
            bool prodFits = BigInteger.Abs(prod) < Two255 && xb != -Two255 && m != Int128.MinValue;
            if (prodFits) Assert.Equal(prod, Big(x * m));
            else Assert.Throws<OverflowException>(() => x * m);

            // Addition and subtraction.
            var yb = RandBits(rng, 255);
            var y = FromBig(yb);
            var sum = xb + yb;
            if (sum >= -Two255 && sum < Two255) Assert.Equal(sum, Big(x + y));
            else Assert.Throws<OverflowException>(() => x + y);
            var diff = xb - yb;
            if (diff >= -Two255 && diff < Two255)
            {
                Assert.Equal(diff, Big(x - y));
            }
            else Assert.Throws<OverflowException>(() => x - y);

            // Negation.
            if (xb == -Two255) Assert.Throws<OverflowException>(() => x.Negate());
            else Assert.Equal(-xb, Big(x.Negate()));
            Assert.Equal(xb.Sign < 0, x.IsNegative);
            Assert.Equal(xb.IsZero, x.IsZero);
        }
        // Carry chains through all limbs.
        foreach (var v in new[] { BigInteger.One, (BigInteger.One << 64), (BigInteger.One << 128), (BigInteger.One << 192), Two255 - 1 })
        {
            Assert.Equal(-v, Big(FromBig(v).Negate()));
            Assert.Equal(v, Big(FromBig(-v).Negate()));
            Assert.Equal(v - 1 + 1, Big(FromBig(v - 1) + FromBig(1)));
        }
        Assert.Equal(BigInteger.Zero, Big(FromBig(0).Negate()));
    }

    [Fact]
    public void Int256SubtractionOfMinimumThrowsEvenWhenTheResultFits()
    {
        // Documented quirk, not a correctness bug: a - b negates b first, so b = -2^255 throws although
        // (-1) - (-2^255) = 2^255 - 1 is representable. Unreachable in IntersectFast (|values| < 2^234).
        Assert.Throws<OverflowException>(() => FromBig(-1) - FromBig(-Two255));
    }

    // ------------------------------------------------------------------------------------------------ IntersectFast

    [Fact]
    public void FastIntersectionEqualsBigIntegerCramerOnAdversarialBudgetPlanes()
    {
        var rng = new Random(66);
        int compared = 0, nulls = 0;
        Plane3 RandPlane() => new(R128(rng, 66), R128(rng, 66), R128(rng, 66), R128(rng, 98));
        for (int i = 0; i < 30_000; i++)
        {
            Plane3 p = RandPlane(), q = RandPlane(), r = RandPlane();
            switch (i % 5)
            {
                case 1: // r's normal in the span of p and q (W = 0) where the budget allows it
                    r = new Plane3(p.Nx / 2 - q.Nx / 2, p.Ny / 2 - q.Ny / 2, p.Nz / 2 - q.Nz / 2, r.D);
                    p = new Plane3(p.Nx / 2 * 2, p.Ny / 2 * 2, p.Nz / 2 * 2, p.D);
                    q = new Plane3(q.Nx / 2 * 2, q.Ny / 2 * 2, q.Nz / 2 * 2, q.D);
                    break;
                case 2: // nearly dependent: tiny W, huge numerators
                    r = new Plane3(p.Nx / 2 - q.Nx / 2 + rng.Next(-1, 2), p.Ny / 2 - q.Ny / 2, p.Nz / 2 - q.Nz / 2 + 1, r.D);
                    break;
                case 3: // all extremes with random signs
                    Int128 N = ((Int128)1 << 66) - 1, D = ((Int128)1 << 98) - 1;
                    Int128 S() => rng.Next(2) == 0 ? 1 : -1;
                    p = new Plane3(S() * N, S() * N, S() * (N - 1), S() * D);
                    q = new Plane3(S() * N, S() * (N - 2), S() * N, S() * D);
                    r = new Plane3(S() * (N - 3), S() * N, S() * N, S() * D);
                    break;
            }
            var expected = RefIntersect(p, q, r);
            var fast = Plane3.IntersectFast(p, q, r);
            AssertSamePoint(expected, fast);
            AssertSamePoint(expected, Plane3.Intersect(p, q, r));
            if (expected is null) nulls++; else compared++;
        }
        output.WriteLine($"compared {compared}, dependent {nulls}");
        Assert.True(compared > 20_000 && nulls > 1000);
    }

    [Fact]
    public void IntersectDispatchIsExactAtAndBeyondTheBudgetBoundary()
    {
        var rng = new Random(98);
        Int128[] edgesN = [((Int128)1 << 66) - 1, (Int128)1 << 66, -((Int128)1 << 66), -((Int128)1 << 66) + 1, ((Int128)1 << 70) + 12345];
        Int128[] edgesD = [((Int128)1 << 98) - 1, (Int128)1 << 98, -((Int128)1 << 98), ((Int128)1 << 102) - 7];
        for (int i = 0; i < 5000; i++)
        {
            Int128 N() => rng.Next(3) == 0 ? edgesN[rng.Next(edgesN.Length)] : R128(rng, 66);
            Int128 D() => rng.Next(3) == 0 ? edgesD[rng.Next(edgesD.Length)] : R128(rng, 98);
            Plane3 p = new(N(), N(), N(), D()), q = new(N(), N(), N(), D()), r = new(N(), N(), N(), D());
            AssertSamePoint(RefIntersect(p, q, r), Plane3.Intersect(p, q, r));
        }
    }

    [Fact]
    public void FastIntersectionOfGridPlanesNearTheCoordinateLimitEqualsReference()
    {
        // Planes through grid points with coordinates near ±2^31 (the realistic extreme of the budget).
        var rng = new Random(31);
        Vec3 P() => new(Ex(), Ex(), Ex());
        long Ex() => rng.Next(4) switch { 0 => M - rng.Next(3), 1 => -M + rng.Next(3), _ => rng.NextInt64(-M, M + 1) };
        for (int i = 0; i < 10_000; i++)
        {
            var p = Plane3.FromPoints(P(), P(), P());
            var q = Plane3.FromPoints(P(), P(), P());
            var r = Plane3.FromPoints(P(), P(), P());
            if (i % 2 == 0) { p = p.Canonical(); r = r.Flipped(); }
            AssertSamePoint(RefIntersect(p, q, r), Plane3.Intersect(p, q, r));
        }
    }

    // ------------------------------------------------------------------------------------------------ Canonical

    private static Plane3 RefCanonical(Plane3 p)
    {
        var g = BigInteger.GreatestCommonDivisor(BigInteger.GreatestCommonDivisor(p.Nx, p.Ny),
                                                 BigInteger.GreatestCommonDivisor(p.Nz, p.D));
        if (g <= 1) return p;
        return new Plane3((Int128)(p.Nx / g), (Int128)(p.Ny / g), (Int128)(p.Nz / g), (Int128)(p.D / g));
    }

    [Fact]
    public void CanonicalMatchesBigIntegerGcdIncludingLargeAndZeroOperands()
    {
        var rng = new Random(128);
        for (int i = 0; i < 100_000; i++)
        {
            // Common factor of up to 96 bits times small cofactors: exercises the 64-bit, the one-remainder and the
            // full 128-bit binary paths; zeros in every position.
            int gb = rng.Next(0, 97), cb = Math.Max(1, Math.Min(30, 126 - gb));
            BigInteger g = RandBits(rng, Math.Max(gb, 1));
            if (g.IsZero) g = 1;
            g = BigInteger.Abs(g) << rng.Next(0, 3);
            BigInteger C() => rng.Next(5) == 0 ? 0 : RandBits(rng, cb);
            BigInteger[] c = [C() * g, C() * g, C() * g, C() * g];
            if (c.Any(v => BigInteger.Abs(v) >= BigInteger.One << 127)) continue;
            var p = new Plane3((Int128)c[0], (Int128)c[1], (Int128)c[2], (Int128)c[3]);
            Assert.Equal(RefCanonical(p), p.Canonical());
        }
        // Random unrelated large values (gcd usually 1 or small).
        for (int i = 0; i < 50_000; i++)
        {
            var p = new Plane3(R128(rng, 126), R128(rng, 126), R128(rng, 126), R128(rng, 126));
            Assert.Equal(RefCanonical(p), p.Canonical());
        }
        // Planes from grid points (with orientation).
        for (int i = 0; i < 20_000; i++)
        {
            Vec3 P() => new(rng.NextInt64(-M, M + 1) / (1L << rng.Next(0, 30)) << rng.Next(0, 3), rng.NextInt64(-M, M + 1) >> rng.Next(0, 30), rng.NextInt64(-M, M + 1));
            var p = Plane3.FromPoints(P(), P(), P());
            var c = p.Canonical();
            Assert.Equal(RefCanonical(p), c);
        }
        Assert.Equal(new Plane3(0, 0, 0, 0), new Plane3(0, 0, 0, 0).Canonical());
        Assert.Equal(new Plane3(0, 0, -1, 1), new Plane3(0, 0, -((Int128)1 << 100), (Int128)1 << 100).Canonical());
        Assert.Equal(new Plane3(1, 0, 0, 0), new Plane3((Int128)1 << 126, 0, 0, 0).Canonical());
    }

    [Fact]
    public void CanonicalKeepsOrientationForInt128MinValue()
    {
        var p = new Plane3(Int128.MinValue, 0, 0, 0);
        Assert.Equal(Int128.Sign(RefCanonical(p).Nx), Int128.Sign(p.Canonical().Nx));
    }

    // ------------------------------------------------------------------------------------------------ hull filter

    /// <summary>The hull's floating-point side filter, replicated expression by expression from ConvexHull3.</summary>
    private static int? HullFilter(in Plane3 pl, Vec3 q)
    {
        const double eps = 1.0 / (1L << 53);
        double nx = (double)pl.Nx, ny = (double)pl.Ny, nz = (double)pl.Nz, d = (double)pl.D;
        double b0 = (Math.Abs(nx) + Math.Abs(ny) + Math.Abs(nz)) * (double)M;
        double v = nx * q.X + ny * q.Y + nz * q.Z + d;
        double bound = (b0 + Math.Abs(d)) * 8 * eps;
        if (v > bound) return 1;
        if (v < -bound) return -1;
        return null;
    }

    /// <summary>
    /// Points on (r = 0) or r units off the plane A·x + B·y + z + d0 = 0 with |x|, |y| close to 2^31 and |z| small:
    /// the evaluation n·q + d cancels ~2^62·|k| down to |k·r|, the worst case for the filter.
    /// </summary>
    private static Vec3 NearPlane(Random rng, long a, long b, long d0, long r, long range)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (attempt > 100_000) throw new InvalidOperationException("NearPlane: no point found");
            long x = rng.Next(2) == 0 ? range - rng.NextInt64(0, range / 8) : -range + rng.NextInt64(0, range / 8);
            if (rng.Next(4) == 0) x = rng.NextInt64(-range, range + 1);
            Int128 ax = (Int128)a * x;
            long y = (long)Math.Round(-(double)ax / b) + rng.Next(-2, 3);
            if (Math.Abs(y) > range) continue;
            Int128 z = r - d0 - ax - (Int128)b * y;
            if (Int128.Abs(z) > range) continue;
            return new Vec3(x, y, (long)z);
        }
    }

    [Fact]
    public void Int128ToDoubleConversionIsCorrectlyRounded()
    {
        // The filter's error bound assumes (double)Int128 is within half an ulp.
        var rng = new Random(53);
        for (int i = 0; i < 200_000; i++)
        {
            Int128 v = R128(rng, 127);
            double d = (double)v;
            var err = BigInteger.Abs(new BigInteger(d) - (BigInteger)v);
            // half ulp of |v|: 2^(bitlength - 54)
            int bl = (int)BigInteger.Abs(v).GetBitLength();
            Assert.True(bl <= 53 ? err.IsZero : err <= BigInteger.One << (bl - 54), $"{v} -> {d}");
        }
    }

    [Fact]
    public void HullSideFilterNeverContradictsTheExactSign()
    {
        var rng = new Random(2031);
        int decided = 0, undecided = 0, nearDecided = 0;
        double worst = 0;
        for (int t = 0; t < 400; t++)
        {
            long a = rng.NextInt64(1L << 28, 1L << 31), b = rng.NextInt64(a, 1L << 31);
            if (rng.Next(2) == 0) a = -a;
            long d0 = rng.NextInt64(-(1L << 29), 1L << 29);
            // Face plane through three near-extreme points on the base plane (non-canonical: n = k·(a, b, 1)).
            Vec3 p0 = NearPlane(rng, a, b, d0, 0, M), p1 = NearPlane(rng, a, b, d0, 0, M), p2 = NearPlane(rng, a, b, d0, 0, M);
            var pl = Plane3.FromPoints(p0, p1, p2);
            if (pl.IsDegenerate) continue;
            if (rng.Next(2) == 0) pl = pl.Flipped();
            for (int i = 0; i < 200; i++)
            {
                long r = rng.Next(4) switch { 0 => 0, 1 => rng.Next(-3, 4), 2 => rng.Next(-5000, 5001), _ => rng.NextInt64(-(1L << 29), 1L << 29) };
                Vec3 q = NearPlane(rng, a, b, d0, r, M);
                int exact = Predicates.Side(pl, q);
                int? f = HullFilter(pl, q);
                if (f is int s)
                {
                    Assert.Equal(exact, s);
                    decided++;
                    if (Math.Abs(r) < 5000) nearDecided++;
                }
                else undecided++;
                double approx = (double)pl.Nx * q.X + (double)pl.Ny * q.Y + (double)pl.Nz * q.Z + (double)pl.D;
                double err = Math.Abs(approx - (double)pl.Evaluate(q));
                double bound = (((Math.Abs((double)pl.Nx) + Math.Abs((double)pl.Ny) + Math.Abs((double)pl.Nz)) * M) + Math.Abs((double)pl.D)) * 8 / (1L << 53) / 8 * 5;
                worst = Math.Max(worst, err / bound);
            }
        }
        output.WriteLine($"decided {decided} (near {nearDecided}), undecided {undecided}; worst observed error / (5u·S) = {worst:F3}");
        Assert.True(undecided > 1000 && decided > 10_000);
        Assert.True(worst < 1.0);
    }

    // ------------------------------------------------------------------------------------------------ hull

    /// <summary>
    /// Brute-force check of a hull: the face planes are exactly the supporting planes of the point set (all triples),
    /// every face is strictly convex with input points as vertices, all points on a face plane lie in the face, and
    /// the surface is closed.
    /// </summary>
    private static void AssertHullCorrect(IReadOnlyList<Vec3> input, Solid hull)
    {
        var pts = input.Distinct().ToArray();
        var expected = new HashSet<Plane3>();
        for (int i = 0; i < pts.Length; i++)
            for (int j = i + 1; j < pts.Length; j++)
                for (int k = j + 1; k < pts.Length; k++)
                {
                    var pl = Plane3.FromPoints(pts[i], pts[j], pts[k]);
                    if (pl.IsDegenerate) continue;
                    bool pos = false, neg = false;
                    foreach (var q in pts)
                    {
                        int s = Predicates.Side(pl, q);
                        pos |= s > 0; neg |= s < 0;
                        if (pos && neg) break;
                    }
                    if (pos && neg) continue;
                    expected.Add((pos ? pl.Flipped() : pl).Canonical());
                }
        var set = new HashSet<Vec3>(pts);
        var actual = new HashSet<Plane3>();
        foreach (var f in hull.Faces)
        {
            Assert.True(actual.Add(f.Support), "two faces share a plane (coplanar triangles not merged)");
            foreach (var v in f.Vertices)
            {
                Assert.True(v.IsGrid && set.Contains(v.Grid), "face vertex is not an input point");
            }
            int n = f.Vertices.Length;
            for (int e = 0; e < n; e++)
                for (int j = 0; j < n; j++)
                {
                    if (j == e || j == (e + 1) % n) continue;
                    Assert.True(f.Vertices[j].SideOf(f.Edges[e]) < 0, "face not strictly convex (collinear or reflex vertex)");
                }
            foreach (var q in pts)
            {
                int s = Predicates.Side(f.Support, q);
                Assert.True(s <= 0, "input point outside the hull");
                if (s == 0)
                    foreach (var e in f.Edges)
                        Assert.True(Predicates.Side(e, q) <= 0, "point on a face plane outside the face polygon");
            }
        }
        Assert.True(expected.SetEquals(actual), $"face planes differ from brute force: expected {expected.Count}, got {actual.Count}");
        AssertClosed(hull, "hull");
    }

    private static IEnumerable<(string Name, List<Vec3> Points)> DegenerateHullInputs()
    {
        var rng = new Random(7);
        // Full lattice cube: coplanar faces with interior points, collinear points on every edge.
        for (int s = 2; s <= 4; s++)
        {
            var pts = new List<Vec3>();
            for (int x = 0; x <= s; x++) for (int y = 0; y <= s; y++) for (int z = 0; z <= s; z++)
                pts.Add(new Vec3(M - 3 * x, -M + 5 * y, 7 * z - M / 2));
            yield return ($"lattice{s}", pts);
        }
        // Tetrahedron with many points on its edges and faces, duplicates and shuffled order.
        {
            Vec3 a = new(-M, -M, -M), b = new(M, -M, -M), c = new(-M, M, -M), d = new(-M, -M, M);
            var pts = new List<Vec3> { a, b, c, d };
            for (int i = 1; i < 8; i++)
            {
                long t = M / 4 * i - M;
                pts.Add(new Vec3(t, -M, -M)); pts.Add(new Vec3(-M, t, -M)); pts.Add(new Vec3(-M, -M, t));
                pts.Add(new Vec3(t, -t, -M)); pts.Add(new Vec3(t, -M, -t)); pts.Add(new Vec3(-M, t, -t)); // on slanted edges
                pts.Add(new Vec3(-M + M / 8, -M + M / 8 * i / 4, -M)); // on the bottom face
            }
            pts.AddRange(pts.Take(10));
            yield return ("tet-edges", pts.OrderBy(_ => rng.Next()).ToList());
        }
        // Prism: all but two points of each cap collinear (thin triangles with many collinear points).
        {
            var pts = new List<Vec3>();
            for (int i = 0; i <= 10; i++) { pts.Add(new Vec3(i * 1000, 0, 0)); pts.Add(new Vec3(i * 1000, 0, 5000)); }
            pts.Add(new Vec3(3000, 1, 0)); pts.Add(new Vec3(3000, 1, 5000));
            yield return ("needle-prism", pts.OrderBy(_ => rng.Next()).ToList());
        }
        // Slanted extreme plane x + y + z = -M with many lattice points on it, plus points 1 nm off.
        {
            var pts = new List<Vec3> { new(M, M, M) };
            for (int i = 0; i < 40; i++)
            {
                long x = rng.NextInt64(-M, 1), y = rng.NextInt64(-M, 1);
                long z = -M - x - y;
                if (z < -M || z > M) continue;
                pts.Add(new Vec3(x, y, z));
                if (z + 1 <= M && rng.Next(2) == 0) pts.Add(new Vec3(x, y, z + 1));
            }
            yield return ("slanted-extreme", pts);
        }
        // Near-cancellation planes A·x + B·y + z + d0 = 0 at the coordinate limit, with points 0..3 nm off the plane.
        for (int t = 0; t < 6; t++)
        {
            long a = rng.NextInt64(1L << 28, 1L << 31), b = rng.NextInt64(a, 1L << 31), d0 = rng.NextInt64(-(1L << 29), 1L << 29);
            var pts = new List<Vec3>();
            for (int i = 0; i < 14; i++) pts.Add(NearPlane(rng, a, b, d0, 0, M));
            for (int i = 0; i < 12; i++) pts.Add(NearPlane(rng, a, b, d0, rng.Next(-3, 4), M));
            pts.Add(NearPlane(rng, a, b, d0, -(1L << 29), M));
            pts.Add(NearPlane(rng, a, b, d0, 1L << 29, M));
            yield return ($"cancel{t}", pts);
        }
        // Random small lattice (many coplanar/collinear coincidences).
        for (int t = 0; t < 40; t++)
        {
            var pts = new List<Vec3>();
            int n = rng.Next(6, 30);
            for (int i = 0; i < n; i++) pts.Add(new Vec3(rng.Next(0, 4) * 1000L, rng.Next(0, 4) * 1000L, rng.Next(0, 3) * 1000L));
            if (pts.Distinct().Count() < 4) continue;
            yield return ($"small{t}", pts);
        }
    }

    [Fact]
    public void HullOfDegenerateInputsMatchesBruteForce()
    {
        int checkedSets = 0;
        foreach (var (name, pts) in DegenerateHullInputs())
        {
            Solid hull;
            try { hull = ConvexHull3.Compute(pts); }
            catch (ArgumentException) { continue; } // coplanar / collinear input
            catch (Exception e) { throw new Exception($"{name}: {e.Message}", e); }
            try { AssertHullCorrect(pts, hull); }
            catch (Exception e) { throw new Exception($"{name}: {e.Message}", e); }
            // Order independence: shuffled input gives the same exact hull.
            var shuffled = pts.OrderBy(p => HashCode.Combine(p, 17)).ToList();
            Assert.Equal(Volume6(hull), Volume6(ConvexHull3.Compute(shuffled)));
            checkedSets++;
        }
        output.WriteLine($"{checkedSets} hulls checked");
        Assert.True(checkedSets > 30);
    }

    [Fact]
    public void HullOfRandomPointCloudsMatchesBruteForce()
    {
        var rng = new Random(4242);
        for (int t = 0; t < 60; t++)
        {
            int n = rng.Next(5, 45);
            long scale = t % 3 == 0 ? M : t % 3 == 1 ? 1_000_000 : 20;
            var pts = new List<Vec3>();
            for (int i = 0; i < n; i++) pts.Add(new Vec3(rng.NextInt64(-scale, scale + 1), rng.NextInt64(-scale, scale + 1), rng.NextInt64(-scale, scale + 1)));
            Solid hull;
            try { hull = ConvexHull3.Compute(pts); } catch (ArgumentException) { continue; }
            AssertHullCorrect(pts, hull);
        }
    }

    [Fact]
    public void HullBeyondGridRangeIsStillExactOrRejected()
    {
        var rng = new Random(40);
        const long range = 1L << 40;
        int wrong = 0;
        for (int t = 0; t < 20; t++)
        {
            long a = rng.NextInt64(1L << 28, 1L << 31), b = rng.NextInt64(a, 1L << 31);
            var pts = new List<Vec3>();
            for (int i = 0; i < 6; i++) pts.Add(NearPlane(rng, a, b, 0, 0, range));
            for (int i = 0; i < 6; i++) pts.Add(NearPlane(rng, a, b, 0, rng.Next(1, 4), range));
            pts.Add(NearPlane(rng, a, b, 0, -(1L << 38), range));
            Solid hull;
            try { hull = ConvexHull3.Compute(pts); }
            catch (ArgumentOutOfRangeException) { continue; } // rejecting the input would be fine
            foreach (var f in hull.Faces)
                foreach (var q in pts)
                    if (Predicates.Side(f.Support, q) > 0) { wrong++; goto next; }
            next:;
        }
        Assert.Equal(0, wrong);
    }

    // ------------------------------------------------------------------------------------------------ Booleans

    private const long Mm = 1_000_000;

    private static Solid Box(long x0, long y0, long z0, long x1, long y1, long z1) =>
        Solid.Box(new Vec3(x0 * Mm, y0 * Mm, z0 * Mm), new Vec3(x1 * Mm, y1 * Mm, z1 * Mm));

    private static BigInteger Overlap(long a0, long a1, long b0, long b1) => Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));

    [Fact]
    public void BooleansOfTouchingAndOverlappingBoxesHaveAnalyticVolumes()
    {
        // Boxes touching A = [0,4]^3 along faces, edges and vertices, and overlapping with coplanar faces:
        // exactly the configurations the Separated shortcut sees most.
        var a = Box(0, 0, 0, 4, 4, 4);
        long[] starts = [-3, -1, 0, 1, 3, 4];
        long[] sizes = [1, 3, 4];
        var rng = new Random(5);
        int cases = 0;
        foreach (long ox in starts) foreach (long oy in starts) foreach (long oz in starts)
        {
            if (rng.Next(3) != 0) continue;
            long sx = sizes[rng.Next(3)], sy = sizes[rng.Next(3)], sz = sizes[rng.Next(3)];
            var b = Box(ox, oy, oz, ox + sx, oy + sy, oz + sz);
            BigInteger mm3 = (BigInteger)Mm * Mm * Mm * 6;
            BigInteger va = 64 * mm3, vb = sx * sy * sz * mm3;
            BigInteger vi = Overlap(0, 4, ox, ox + sx) * Overlap(0, 4, oy, oy + sy) * Overlap(0, 4, oz, oz + sz) * mm3;
            Check(a | b, va + vb - vi, "union");
            Check(a & b, vi, "intersection");
            Check(a - b, va - vi, "difference");
            Check(b - a, vb - vi, "reverse difference");
            cases++;

            void Check(Solid s, BigInteger expected, string op)
            {
                var v = Volume6(s);
                Assert.True(v.Den.IsOne && v.Num == expected, $"{op} with B at ({ox},{oy},{oz}) size ({sx},{sy},{sz}): {v.Num} != {expected}");
                AssertClosed(s, op);
            }
        }
        output.WriteLine($"{cases} box pairs");
    }

    /// <summary>Random convex polytope on a coarse lattice (1 mm), translated by a lattice vector.</summary>
    private static Solid LatticeHull(Random rng, long ox, long oy, long oz, int size)
    {
        while (true)
        {
            var pts = new List<Vec3>();
            int n = rng.Next(4, 9);
            for (int i = 0; i < n; i++)
                pts.Add(new Vec3((ox + rng.Next(0, size + 1)) * Mm, (oy + rng.Next(0, size + 1)) * Mm, (oz + rng.Next(0, size + 1)) * Mm));
            try { return ConvexHull3.Compute(pts); } catch (ArgumentException) { }
        }
    }

    [Fact]
    public void BooleansOfTouchingLatticePolytopesAreClosedAndConsistent()
    {
        // Slanted faces on a coarse lattice: faces touching along edges/vertices, coplanar overlaps, vertices of
        // one solid on faces of the other. The union/intersection identity is checked exactly together with closure
        // of every result (a misclassified fragment leaves a hole, which closure detects).
        var rng = new Random(17);
        for (int t = 0; t < 120; t++)
        {
            var a = LatticeHull(rng, 0, 0, 0, 3);
            var b = LatticeHull(rng, rng.Next(-2, 3), rng.Next(-2, 3), rng.Next(-2, 3), 3);
            var va = Volume6(a);
            var vb = Volume6(b);
            var u = a | b;
            var i = a & b;
            var d = a - b;
            var e = b - a;
            foreach (var (s, name) in new[] { (u, "union"), (i, "intersection"), (d, "difference"), (e, "reverse difference") })
                AssertClosed(s, $"case {t} {name}");
            var vu = Volume6(u); var vi = Volume6(i); var vd = Volume6(d); var ve = Volume6(e);
            Assert.Equal(Add(va, vb), Add(vu, vi));
            Assert.Equal(va, Add(vd, vi));
            Assert.Equal(vb, Add(ve, vi));
            Assert.Equal(vu, Add(Add(vd, ve), vi));
            Assert.True(vi.Num.Sign >= 0 && vd.Num.Sign >= 0 && ve.Num.Sign >= 0);
        }
    }

    [Fact]
    public void CutChainsWithExactVerticesStayClosedAndConsistent()
    {
        // Repeated differences (vertices become exact plane intersections) with tools that touch earlier cut faces.
        var rng = new Random(23);
        for (int chain = 0; chain < 6; chain++)
        {
            var part = Box(0, 0, 0, 6, 6, 4);
            var vPart = Volume6(part);
            for (int step = 0; step < 8; step++)
            {
                var tool = rng.Next(3) == 0
                    ? Tet(new Vec3(rng.Next(0, 7) * Mm, rng.Next(0, 7) * Mm, 4 * Mm), new Vec3(rng.Next(0, 7) * Mm, rng.Next(0, 7) * Mm, 4 * Mm),
                          new Vec3(rng.Next(0, 7) * Mm, rng.Next(0, 7) * Mm, 6 * Mm), new Vec3(rng.Next(0, 7) * Mm, rng.Next(0, 7) * Mm, rng.Next(1, 4) * Mm))
                    : LatticeHull(rng, rng.Next(-1, 5), rng.Next(-1, 5), rng.Next(1, 4), 3);
                var vTool = Volume6(tool);
                var (rest, removed) = part.Split(tool);
                var union = part | tool;
                AssertClosed(rest, $"chain {chain} step {step} rest");
                AssertClosed(removed, $"chain {chain} step {step} removed");
                AssertClosed(union, $"chain {chain} step {step} union");
                var vRest = Volume6(rest);
                var vRem = Volume6(removed);
                Assert.Equal(vPart, Add(vRest, vRem));
                Assert.Equal(Add(vPart, vTool), Add(Volume6(union), vRem));
                // The removed part is inside the tool: tool − removed must have volume vTool − vRem.
                Assert.Equal(Add(vTool, vRem, -1), Volume6(tool - removed));
                part = rest;
                vPart = vRest;
                if (part.IsEmpty) break;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ BVH

    [Fact]
    public void BvhWithIdenticalBoxesStaysShallowAndFindsEverything()
    {
        // 20 000 identical faces (all centres equal): the median split on index halves still halves the count.
        var f = Face3.FromGrid([new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(0, 10, 0)]);
        var faces = Enumerable.Repeat(f, 20_000).ToList();
        var bvh = new Bvh3(faces);
        var result = new List<Face3>();
        bvh.Query(f.Box, result);
        Assert.Equal(faces.Count, result.Count);
        bvh.QueryRayX(-100, 1, 1, result);
        Assert.Equal(faces.Count, result.Count);
        bvh.Query(new Box3(100, 100, 100, 200, 200, 200), result);
        Assert.Empty(result);
        // Mixed: random boxes, compare with brute force.
        var rng = new Random(3);
        var many = new List<Face3>();
        for (int i = 0; i < 3000; i++)
        {
            long x = rng.Next(0, 50) * 100, y = rng.Next(0, 50) * 100, z = rng.Next(0, 3) * 100;
            many.Add(Face3.FromGrid([new Vec3(x, y, z), new Vec3(x + 1 + rng.Next(300), y, z), new Vec3(x, y + 1 + rng.Next(300), z + rng.Next(50))]));
        }
        var bvh2 = new Bvh3(many);
        for (int i = 0; i < 300; i++)
        {
            double x = rng.Next(0, 5000), y = rng.Next(0, 5000), z = rng.Next(-50, 300);
            var box = new Box3(x, y, z, x + rng.Next(0, 800), y + rng.Next(0, 800), z + rng.Next(0, 100));
            bvh2.Query(box, result);
            var expected = many.Where(m => m.Box.Overlaps(box)).ToHashSet();
            Assert.True(expected.SetEquals(result) && expected.Count == result.Count);
        }
    }
}
