using System.Buffers;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Stykker.NanoCut.Geometry3D;
#if !REF_BUILD
using Xunit.Abstractions;
#endif

namespace Stykker.NanoCut.Tests;

/// <summary>
/// Deterministic Boolean / hull workload for the differential check of the third optimisation round. The same source
/// is compiled against the reference commit 4384975 (with REF_BUILD defined and the assembly named
/// Stykker.NanoCut.Tests, so the internals are visible) to produce <see cref="OptimizationVerification4Tests.ReferenceDigests"/>.
/// Only APIs that exist in both versions are used here.
/// </summary>
public static class Rev4Workload
{
    private const long M = Units.MaxCoordinate;
    private const long Mm = 1_000_000;

    /// <summary>Exact textual fingerprint of a solid: face order, planes and exact vertices.</summary>
    public static string Fingerprint(Solid s)
    {
        var sb = new StringBuilder();
        foreach (var f in s.Faces)
        {
            Pl(f.Support);
            sb.Append('|');
            foreach (var e in f.Edges) { Pl(e); sb.Append(';'); }
            sb.Append('|');
            foreach (var v in f.Vertices)
            {
                var b = v.Big;
                sb.Append(b.X).Append(',').Append(b.Y).Append(',').Append(b.Z).Append(',').Append(b.W).Append(';');
            }
            sb.Append('\n');
        }
        return sb.ToString();

        void Pl(in Plane3 p) => sb.Append(p.Nx).Append(',').Append(p.Ny).Append(',').Append(p.Nz).Append(',').Append(p.D);
    }

    public static string Hash(Solid s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Fingerprint(s))))[..16];

    internal static Solid LatticeHull(Random rng, long ox, long oy, long oz, int size, long unit = Mm)
    {
        while (true)
        {
            var pts = new List<Vec3>();
            int n = rng.Next(4, 10);
            for (int i = 0; i < n; i++)
                pts.Add(new Vec3(ox + rng.Next(0, size + 1) * unit, oy + rng.Next(0, size + 1) * unit, oz + rng.Next(0, size + 1) * unit));
            try { return ConvexHull3.Compute(pts); } catch (ArgumentException) { }
        }
    }

    internal static Solid Tet(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        int o = Predicates.Orient3D(a, b, c, d);
        if (o == 0) throw new ArgumentException("flat tetrahedron");
        if (o > 0) (b, c) = (c, b);
        return Solid.FromTriangles([a, b, c, d], [0, 1, 2, 0, 3, 1, 1, 3, 2, 2, 3, 0]);
    }

    /// <summary>Coarse sphere (a few hundred triangles: above the parallel thresholds).</summary>
    internal static Solid Ball(Vec3 c, double rMm, double chordNm) => Solid.Sphere(c, rMm, Tolerance.Budget(totalUm: 1000, chordNm: chordNm));

    /// <summary>Runs the workload: one line "name hash" per result.</summary>
    public static List<string> Run()
    {
        var lines = new List<string>();
        void Add(string name, Solid s) => lines.Add($"{name} {Hash(s)}");

        // 1. Lattice polytopes (slanted faces, touching / coplanar contacts) at the origin and at the range corners.
        var rng = new Random(3301);
        Vec3[] origins = [new(0, 0, 0), new(M - 8 * Mm, M - 8 * Mm, M - 8 * Mm), new(-M, -M, -M), new(-M, M - 8 * Mm, -M + 7)];
        for (int t = 0; t < 48; t++)
        {
            var o = origins[t % origins.Length];
            var a = LatticeHull(rng, o.X, o.Y, o.Z, 3);
            var b = LatticeHull(rng, o.X + rng.Next(-2, 3) * Mm, o.Y + rng.Next(-2, 3) * Mm, o.Z + rng.Next(-2, 3) * Mm, 3);
            Add($"lat{t}u", a | b);
            Add($"lat{t}i", a & b);
            Add($"lat{t}d", a - b);
            Add($"lat{t}e", b - a);
        }

        // 2. Cut chains: vertices become exact plane intersections, later cuts touch earlier cut faces.
        for (int chain = 0; chain < 6; chain++)
        {
            var o = origins[chain % origins.Length];
            var part = Solid.Box(o, new Vec3(o.X + 6 * Mm, o.Y + 6 * Mm, o.Z + 4 * Mm));
            for (int step = 0; step < 8 && !part.IsEmpty; step++)
            {
                Solid tool;
                if (rng.Next(3) == 0)
                {
                    try
                    {
                        tool = Tet(new Vec3(o.X + rng.Next(0, 7) * Mm, o.Y + rng.Next(0, 7) * Mm, o.Z + 4 * Mm),
                                   new Vec3(o.X + rng.Next(0, 7) * Mm, o.Y + rng.Next(0, 7) * Mm, o.Z + 4 * Mm),
                                   new Vec3(o.X + rng.Next(0, 7) * Mm, o.Y + rng.Next(0, 7) * Mm, o.Z + 6 * Mm),
                                   new Vec3(o.X + rng.Next(0, 7) * Mm, o.Y + rng.Next(0, 7) * Mm, o.Z + rng.Next(1, 4) * Mm + 13));
                    }
                    catch (ArgumentException) { continue; }
                }
                else tool = LatticeHull(rng, o.X + rng.Next(-1, 5) * Mm + 17, o.Y + rng.Next(-1, 5) * Mm - 3, o.Z + rng.Next(1, 4) * Mm, 3);
                var (rest, removed) = part.Split(tool);
                Add($"chain{chain}.{step}r", rest);
                Add($"chain{chain}.{step}m", removed);
                Add($"chain{chain}.{step}u", part | tool);
                part = rest;
            }
        }

        // 3. Coarse spheres and odd-sized lattice hulls: hundreds of faces (parallel classification, BVH arrays rented
        //    in several sizes), sphere vertices near the range limit.
        var s1 = Ball(new Vec3(0, 0, 0), 3, 40_000);
        var s2 = Ball(new Vec3(1_234_567, -765_432, 345_678), 2.5, 30_000);
        var s3 = Ball(new Vec3(M - 4 * Mm, -M + 4 * Mm, M - 3 * Mm - 11), 3, 60_000);
        var s4 = Ball(new Vec3(M - 4 * Mm + 2_222_223, -M + 4 * Mm - 1_111_111, M - 3 * Mm - 1_000_011), 2, 30_000);
        Add("s12u", s1 | s2); Add("s12i", s1 & s2); Add("s12d", s1 - s2); Add("s21d", s2 - s1);
        Add("s34u", s3 | s4); Add("s34i", s3 & s4); Add("s34d", s3 - s4);
        var box = Solid.Box(new Vec3(-1_000_000, -4_000_000, -500_000), new Vec3(4_000_000, 333_333, 2_500_000));
        Add("s1box", (s1 - box) | (s2 & box));
        var cyl = Solid.Cylinder(new Vec3(-4 * Mm, 123_457, -77_777), new Vec3(4 * Mm, 223_457, 177_777), 1.25, Tolerance.Budget(totalUm: 1000, chordNm: 10_000));
        Add("cyl", (s1 | s2) - cyl);
        Add("cyl2", cyl - (s1 & s2));

        // 3b. Randomly rotated boxes near the range corners (slanted planes with |n| up to ~2^45 through grid points near
        //     2^31: large, nearly cancelling side-test terms), cut repeatedly so exact vertices meet slanted planes.
        var rr = new Random(4242);
        for (int t = 0; t < 10; t++)
        {
            var c = origins[1 + t % 3];
            long cx = c.X + 4 * Mm, cy = c.Y + 4 * Mm, cz = c.Z + 4 * Mm;
            Solid Rot(long hx, long hy, long hz)
            {
                var bx = Solid.Box(new Vec3(cx - hx, cy - hy, cz - hz), new Vec3(cx + hx, cy + hy, cz + hz));
                var pose = Pose3.Rotation(rr.NextDouble() * Math.PI, rr.NextDouble() - 0.5, rr.NextDouble() - 0.5, rr.NextDouble() + 0.1, new Vec3(cx, cy, cz));
                return bx.Transform(pose);
            }
            var a = Rot(2 * Mm + rr.Next(0, 1000), 1500 * 1000 + rr.Next(0, 1000), 1 * Mm + rr.Next(0, 1000));
            var b = Rot(1 * Mm + rr.Next(0, 1000), 2 * Mm + rr.Next(0, 1000), 1200 * 1000 + rr.Next(0, 1000));
            var d = a - b;
            Add($"rot{t}u", a | b);
            Add($"rot{t}i", a & b);
            Add($"rot{t}d", d);
            var e = Rot(700 * 1000 + rr.Next(0, 1000), 2500 * 1000, 900 * 1000);
            Add($"rot{t}dd", d - e);
            Add($"rot{t}di", d & e);
        }

        // 4. Large hulls (>= 256 single triangles: parallel face construction) and a hull with coplanar groups.
        var hr = new Random(77);
        var cloud = Enumerable.Range(0, 2500).Select(_ => new Vec3(hr.NextInt64(-M, M + 1), hr.NextInt64(-M, M + 1), hr.NextInt64(-M, M + 1))).ToList();
        var hull = ConvexHull3.Compute(cloud);
        Add("hull", hull);
        var lattice = new List<Vec3>();
        for (int i = 0; i < 1500; i++)
        {
            double th = hr.NextDouble() * 2 * Math.PI, ph = Math.Acos(2 * hr.NextDouble() - 1);
            lattice.Add(new Vec3((long)Math.Round(40 * Math.Sin(ph) * Math.Cos(th)) * 10_000, (long)Math.Round(40 * Math.Sin(ph) * Math.Sin(th)) * 10_000, (long)Math.Round(40 * Math.Cos(ph)) * 10_000));
        }
        var hull2 = ConvexHull3.Compute(lattice);
        Add("hull2", hull2);
        Add("hullx", hull2 - s1);
        return lines;
    }
}

#if !REF_BUILD
/// <summary>
/// Adversarial verification of the third optimisation round: (12) floating-point side filter with cached plane doubles,
/// (13) reused ray probe and per-thread FaceMerge index, (14) pooled BVH arrays and alternating fragment lists,
/// (15) parallel hull triangle construction, and the orient3d hull fallback (85bf470).
/// </summary>
public class OptimizationVerification4Tests(ITestOutputHelper output)
{
    private const long M = Units.MaxCoordinate;
    private const long Mm = 1_000_000;

    private static (BigInteger Num, BigInteger Den) Volume6(Solid s, long ox = 0, long oy = 0, long oz = 0) =>
        OptimizationVerificationTests.Volume6(s, ox, oy, oz);

    private static void AssertClosed(Solid s, string what)
    {
        var v0 = Volume6(s);
        var v1 = Volume6(s, 7_000_001, -3_000_017, 11_000_003);
        Assert.True(v0 == v1, $"{what}: surface not closed (volume depends on the reference point)");
    }

    private static readonly object ParallelismLock = new();

    private static T WithParallelism<T>(int p, Func<T> f)
    {
        lock (ParallelismLock)
        {
            int old = SolidBoolean.MaxParallelism;
            SolidBoolean.MaxParallelism = p;
            try { return f(); }
            finally { SolidBoolean.MaxParallelism = old; }
        }
    }

    private static BigInteger RandBig(Random rng, int bits)
    {
        if (bits <= 0) return 0;
        var bytes = new byte[(bits + 7) / 8 + 1];
        rng.NextBytes(bytes);
        bytes[^1] = 0;
        var v = new BigInteger(bytes) >> (8 * (bytes.Length - 1) - bits);
        return rng.Next(2) == 0 ? v : -v;
    }

    private static int SignOf(in Plane3 p, (BigInteger X, BigInteger Y, BigInteger Z, BigInteger W) q) =>
        ((BigInteger)p.Nx * q.X + (BigInteger)p.Ny * q.Y + (BigInteger)p.Nz * q.Z + (BigInteger)p.D * q.W).Sign;

    private static int Filtered(in Point3 q, in Plane3 p) =>
        q.SideOf(p, (double)p.Nx, (double)p.Ny, (double)p.Nz, (double)p.D);

    // ------------------------------------------------------------------------------------------------ (12) side filter

    /// <summary>
    /// Grid points exactly on, or one or two units off, planes through grid points near the range limit (|n| ~ 2^65,
    /// |d| ~ 2^97: the four terms nearly cancel at large magnitude), and arbitrary Int128 planes with large normals and
    /// points far beyond 2^31 (up to 2^62, not exactly representable as doubles beyond 2^53). The filtered side test must
    /// equal the exact BigInteger sign in every case.
    /// </summary>
    [Fact]
    public void FilteredSideOfGridPointsEqualsExactSignNearCancellation()
    {
        var rng = new Random(1201);
        int zero = 0, total = 0;
        for (int t = 0; t < 4000; t++)
        {
            Plane3 pl;
            Vec3 q;
            if (t % 2 == 0)
            {
                // Plane through three grid points near the range limit; q a lattice combination of them (on the plane)
                // shifted by a tiny offset. The combination may leave the grid range (Vec3 is not range-checked).
                Vec3 R() => new(rng.NextInt64(-M, M + 1), rng.NextInt64(-M, M + 1), rng.NextInt64(-M, M + 1));
                Vec3 a = R(), b = R(), c = R();
                if (t % 4 == 0) { b = new Vec3(a.X + rng.Next(-3, 4), a.Y + rng.Next(-3, 4), b.Z); }
                pl = Plane3.FromPoints(a, b, c);
                if (pl.IsDegenerate) continue;
                long s = rng.Next(-1, 3), u = rng.Next(-1, 3);
                q = new Vec3(a.X + s * (b.X - a.X) + u * (c.X - a.X) + rng.Next(-2, 3),
                             a.Y + s * (b.Y - a.Y) + u * (c.Y - a.Y) + (rng.Next(3) == 0 ? rng.Next(-2, 3) : 0),
                             a.Z + s * (b.Z - a.Z) + u * (c.Z - a.Z));
            }
            else
            {
                // Arbitrary plane with |n| < 2^60 through a point with |coordinate| < 2^62, then d nudged by delta.
                int nb = rng.Next(1, 61), pb = rng.Next(1, 63);
                var nx = RandBig(rng, nb); var ny = RandBig(rng, rng.Next(0, nb + 1)); var nz = RandBig(rng, rng.Next(0, nb + 1));
                var px = RandBig(rng, pb); var py = RandBig(rng, pb); var pz = RandBig(rng, rng.Next(0, pb + 1));
                var d = -(nx * px + ny * py + nz * pz) + rng.Next(-3, 4);
                pl = new Plane3((Int128)nx, (Int128)ny, (Int128)nz, (Int128)d);
                q = new Vec3((long)px, (long)py, (long)pz);
            }
            int expected = SignOf(pl, (q.X, q.Y, q.Z, 1));
            var pt = new Point3(q);
            Assert.True(expected == Filtered(pt, pl), $"case {t}: filtered side {Filtered(pt, pl)} != exact {expected} for {pl} at ({q.X},{q.Y},{q.Z})");
            if (expected == 0) zero++;
            total++;
        }
        output.WriteLine($"{total} cases, {zero} on the plane");
        Assert.True(zero > 300);
    }

    /// <summary>
    /// Exact vertices (three-plane intersections of planes through grid points near the range limit) tested against
    /// planes through them: a·P1 + b·P2 (a, b up to 2^20, so |n| up to 2^86 and |d| up to 2^119 with near-total
    /// cancellation), with d nudged by ±1, ±2. The sign is known exactly (W &gt; 0). The cached X/Y/Z are only
    /// approximations; the filter must still never decide wrongly.
    /// </summary>
    [Fact]
    public void FilteredSideOfExactPointsEqualsExactSignForPlanesThroughThem()
    {
        var rng = new Random(1202);
        int tested = 0;
        for (int t = 0; t < 600; t++)
        {
            Vec3 R(long r) => new(rng.NextInt64(-r, r + 1), rng.NextInt64(-r, r + 1), rng.NextInt64(-r, r + 1));
            long range = t % 3 == 0 ? M : t % 3 == 1 ? 1000 : M / 1024;
            var planes = new Plane3[3];
            for (int k = 0; k < 3; k++)
            {
                do planes[k] = Plane3.FromPoints(R(range), R(range), R(range)); while (planes[k].IsDegenerate);
                if (rng.Next(2) == 0) planes[k] = planes[k].Canonical();
            }
            var h = Plane3.Intersect(planes[0], planes[1], planes[2]);
            if (h is null) continue;
            var pt = new Point3(h.Value);
            var big = pt.Big;
            for (int k = 0; k < 3; k++) Assert.Equal(0, Filtered(pt, planes[k]));
            for (int r = 0; r < 12; r++)
            {
                var p1 = planes[rng.Next(3)]; var p2 = planes[rng.Next(3)];
                BigInteger a = RandBig(rng, rng.Next(0, 21)), b = RandBig(rng, rng.Next(0, 21));
                int delta = rng.Next(-2, 3);
                BigInteger nx = a * (BigInteger)p1.Nx + b * (BigInteger)p2.Nx, ny = a * (BigInteger)p1.Ny + b * (BigInteger)p2.Ny,
                           nz = a * (BigInteger)p1.Nz + b * (BigInteger)p2.Nz, d = a * (BigInteger)p1.D + b * (BigInteger)p2.D + delta;
                var bound = BigInteger.One << 126;
                if (BigInteger.Abs(nx) >= bound || BigInteger.Abs(ny) >= bound || BigInteger.Abs(nz) >= bound || BigInteger.Abs(d) >= bound) continue;
                var pl = new Plane3((Int128)nx, (Int128)ny, (Int128)nz, (Int128)d);
                int expected = SignOf(pl, big);
                if (nx.IsZero && ny.IsZero && nz.IsZero && delta == 0) continue;
                // The plane passes through the point exactly when delta == 0 (W > 0, so the sign is that of delta·W).
                Assert.Equal(Math.Sign(delta), expected);
                Assert.True(expected == Filtered(pt, pl), $"case {t}.{r}: filtered {Filtered(pt, pl)} != exact {expected}");
                Assert.Equal(expected, pt.SideOf(pl));
                tested++;
            }
            // Random grid planes nearby (mostly decided by the filter).
            for (int r = 0; r < 6; r++)
            {
                var pl = Plane3.FromPoints(R(range), R(range), R(range));
                if (pl.IsDegenerate) continue;
                Assert.Equal(SignOf(pl, big), Filtered(pt, pl));
                tested++;
            }
        }
        output.WriteLine($"{tested} exact-point side tests");
        Assert.True(tested > 5000);
    }

    /// <summary>
    /// The cached doubles used by Separated / SideSummary / TouchesOrCrosses must be those of the same plane object:
    /// support at offset 0, edge i at 4·(i + 1) – for faces from every constructor path (FromGrid, Split pieces,
    /// FaceMerge joins, Reversed) as they occur in Boolean results.
    /// </summary>
    [Fact]
    public void PlanesDMatchesSupportAndEdgesForAllResultFaces()
    {
        var rng = new Random(1203);
        int faces = 0;
        for (int t = 0; t < 30; t++)
        {
            var a = Rev4Workload.LatticeHull(rng, 0, 0, 0, 3);
            var b = Rev4Workload.LatticeHull(rng, rng.Next(-2, 3) * Mm, rng.Next(-2, 3) * Mm, rng.Next(-2, 3) * Mm, 3);
            foreach (var s in new[] { a | b, a & b, a - b, b - a })
                foreach (var f in s.Faces.Concat(s.Faces.Select(x => x.Reversed())))
                {
                    var k = f.PlanesD;
                    Assert.Equal(4 * (f.Edges.Length + 1), k.Length);
                    Check(k, 0, f.Support);
                    for (int i = 0; i < f.Edges.Length; i++) Check(k, 4 * (i + 1), f.Edges[i]);
                    faces++;
                }
        }
        output.WriteLine($"{faces} faces");

        static void Check(double[] k, int o, in Plane3 p)
        {
            Assert.Equal((double)p.Nx, k[o]);
            Assert.Equal((double)p.Ny, k[o + 1]);
            Assert.Equal((double)p.Nz, k[o + 2]);
            Assert.Equal((double)p.D, k[o + 3]);
        }
    }

    // ------------------------------------------------------------------------------------------------ 85bf470 orient3d

    /// <summary>
    /// The hull's exact fallback changed from Side(FromPoints(a, b, c), q) to Orient3D(a, b, c, q). Both evaluate
    /// n·(q − a) with the same n; in wrapping Int128 arithmetic they agree modulo 2^128, so their signs must agree
    /// everywhere (inside the 2^31 range exactly, beyond it equally wrong). Checked on nearly coplanar quadruples at
    /// magnitudes 2^20 … 2^61 against each other and, where the value fits, against BigInteger.
    /// </summary>
    [Fact]
    public void Orient3DFallbackHasTheSameSignAsTheFormerPlaneSideTest()
    {
        var rng = new Random(1204);
        int[] mags = [20, 31, 33, 40, 42, 45, 50, 61];
        int exactChecked = 0;
        foreach (int mb in mags)
        {
            long r = 1L << mb;
            for (int t = 0; t < 3000; t++)
            {
                Vec3 R() => new(rng.NextInt64(-r, r), rng.NextInt64(-r, r), rng.NextInt64(-r, r));
                Vec3 a = R(), b = R(), c = R();
                Vec3 q;
                if (t % 2 == 0) q = R();
                else
                {
                    // Near the plane: lattice combination plus a tiny offset (may leave the range slightly).
                    long s = rng.Next(-1, 2), u = rng.Next(-1, 2);
                    q = new Vec3(a.X + s * (b.X - a.X) / 2 + u * (c.X - a.X) / 2 + rng.Next(-1, 2), a.Y + s * (b.Y - a.Y) / 2 + u * (c.Y - a.Y) / 2,
                                 a.Z + s * (b.Z - a.Z) / 2 + u * (c.Z - a.Z) / 2);
                }
                int oldSign = Predicates.Side(Plane3.FromPoints(a, b, c), q);
                int newSign = Predicates.Orient3D(a, b, c, q);
                Assert.True(oldSign == newSign, $"2^{mb}: sign {newSign} != former {oldSign}");
                if (mb <= 31)
                {
                    BigInteger bx = b.X - a.X, by = b.Y - a.Y, bz = b.Z - a.Z, cx = c.X - a.X, cy = c.Y - a.Y, cz = c.Z - a.Z;
                    BigInteger dx = q.X - a.X, dy = q.Y - a.Y, dz = q.Z - a.Z;
                    var v = (by * cz - bz * cy) * dx + (bz * cx - bx * cz) * dy + (bx * cy - by * cx) * dz;
                    Assert.Equal(v.Sign, newSign);
                    exactChecked++;
                }
            }
        }
        output.WriteLine($"{exactChecked} exact comparisons");
    }

    // ------------------------------------------------------------------------------------------------ determinism

    private static List<(string Name, Func<Solid> Make)> HeavyCases()
    {
        var s1 = Rev4Workload.Ball(new Vec3(0, 0, 0), 3, 40_000);
        var s2 = Rev4Workload.Ball(new Vec3(1_234_567, -765_432, 345_678), 2.5, 30_000);
        var s3 = Rev4Workload.Ball(new Vec3(M - 4 * Mm, -M + 4 * Mm, M - 3 * Mm - 11), 3, 60_000);
        var s4 = Rev4Workload.Ball(new Vec3(M - 4 * Mm + 2_222_223, -M + 4 * Mm - 1_111_111, M - 3 * Mm - 1_000_011), 2, 30_000);
        var rng = new Random(88);
        var cloud = Enumerable.Range(0, 1200).Select(_ => new Vec3(rng.NextInt64(-M, M + 1), rng.NextInt64(-M, M + 1), rng.NextInt64(-M, M + 1))).ToList();
        var shell = Enumerable.Range(0, 900).Select(_ =>
        {
            double th = rng.NextDouble() * 2 * Math.PI, ph = Math.Acos(2 * rng.NextDouble() - 1);
            return new Vec3((long)(3e6 * Math.Sin(ph) * Math.Cos(th)), (long)(3e6 * Math.Sin(ph) * Math.Sin(th)), (long)(3e6 * Math.Cos(ph)));
        }).ToList();
        return
        [
            ("s1|s2", () => s1 | s2), ("s1&s2", () => s1 & s2), ("s1-s2", () => s1 - s2), ("s2-s1", () => s2 - s1),
            ("s3|s4", () => s3 | s4), ("s3-s4", () => s3 - s4),
            ("hull", () => ConvexHull3.Compute(cloud)), ("shell", () => ConvexHull3.Compute(shell)),
            ("shell-s1", () => ConvexHull3.Compute(shell) - s2),
        ];
    }

    /// <summary>
    /// Parallelism 1, 2, 4, 8 (different partitionings: the per-thread probe, fragment lists and buffers are reused in
    /// different orders) must give bit-identical results, also when repeated.
    /// </summary>
    [Fact]
    public void ResultsAreIdenticalForParallelism1To8()
    {
        var cases = HeavyCases();
        var reference = WithParallelism(1, () => cases.Select(c => Rev4Workload.Fingerprint(c.Make())).ToList());
        foreach (int p in new[] { 2, 4, 8, 3 })
            for (int rep = 0; rep < 2; rep++)
            {
                var got = WithParallelism(p, () => cases.Select(c => Rev4Workload.Fingerprint(c.Make())).ToList());
                for (int i = 0; i < cases.Count; i++)
                    Assert.True(reference[i] == got[i], $"{cases[i].Name}: parallelism {p} (rep {rep}) differs from sequential");
            }
        for (int i = 0; i < cases.Count; i++) AssertClosed(cases[i].Make(), cases[i].Name);
    }

    /// <summary>
    /// Many Booleans at once on the same solid objects (shared faces, lazily published PlanesD, one FaceMerge index and
    /// probe per thread, pooled BVH arrays rented and returned concurrently), interleaved with hulls and small Booleans.
    /// Every result must equal the sequential one.
    /// </summary>
    [Fact]
    public void ConcurrentBooleansOnSharedSolidsEqualSequentialResults()
    {
        var cases = HeavyCases();
        var rng = new Random(1205);
        var small = new List<(Solid A, Solid B)>();
        for (int i = 0; i < 12; i++)
            small.Add((Rev4Workload.LatticeHull(rng, 0, 0, 0, 3), Rev4Workload.LatticeHull(rng, rng.Next(-2, 3) * Mm, rng.Next(-2, 3) * Mm, 0, 3)));
        var expected = WithParallelism(1, () => cases.Select(c => Rev4Workload.Fingerprint(c.Make())).ToList());
        var expectedSmall = WithParallelism(1, () => small.Select(s => Rev4Workload.Fingerprint((s.A - s.B) | (s.B & s.A))).ToList());
        // Fresh solids (PlanesD not yet cached) shared by all tasks.
        var fresh = HeavyCases();
        int n = fresh.Count * 3 + small.Count * 3;
        var got = new string[n];
        WithParallelism(4, () =>
        {
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                if (i < fresh.Count * 3) got[i] = Rev4Workload.Fingerprint(fresh[i % fresh.Count].Make());
                else
                {
                    var s = small[(i - fresh.Count * 3) % small.Count];
                    got[i] = Rev4Workload.Fingerprint((s.A - s.B) | (s.B & s.A));
                }
            });
            return 0;
        });
        for (int i = 0; i < n; i++)
        {
            string exp = i < fresh.Count * 3 ? expected[i % fresh.Count] : expectedSmall[(i - fresh.Count * 3) % small.Count];
            Assert.True(exp == got[i], $"concurrent result {i} differs from the sequential one");
        }
    }

    // ------------------------------------------------------------------------------------------------ (14) pooled BVH

    /// <summary>
    /// BVHs built after larger ones reuse bigger pooled arrays holding stale nodes, faces and boxes: queries must only
    /// see the real count. Dispose is idempotent, returns a cleared face array, and a disposed BVH finds nothing.
    /// </summary>
    [Fact]
    public void PooledBvhArraysNeverExposeStaleEntries()
    {
        var rng = new Random(1206);
        var result = new List<Face3>();
        int[] sizes = [3000, 37, 1, 0, 513, 9, 2000, 8, 17, 1024, 3];
        foreach (int n in sizes.Concat(sizes.Reverse()))
        {
            var faces = new List<Face3>(n);
            for (int i = 0; i < n; i++)
            {
                long x = rng.Next(0, 5000), y = rng.Next(0, 5000);
                faces.Add(Face3.FromGrid([new Vec3(x, y, 0), new Vec3(x + 10, y, 0), new Vec3(x, y + 10, rng.Next(0, 9))]));
            }
            var bvh = new Bvh3(faces);
            for (int q = 0; q < 40; q++)
            {
                double x0 = rng.Next(-100, 5100), y0 = rng.Next(-100, 5100);
                var box = new Box3(x0, y0, -1, x0 + rng.Next(0, 800), y0 + rng.Next(0, 800), 20);
                bvh.Query(box, result);
                var expected = faces.Where(f => f.Box.Overlaps(box)).ToHashSet(ReferenceEqualityComparer.Instance);
                Assert.Equal(expected.Count, result.Count);
                Assert.All(result, f => Assert.Contains(f, expected));
                bvh.QueryRay(rng.Next(3), rng.Next(2) * 2 - 1, x0, y0, 3, result);
                Assert.All(result, f => Assert.Contains(f, faces));
            }
            bvh.Dispose();
            bvh.Dispose();
            bvh.Query(new Box3(-1e9, -1e9, -1e9, 1e9, 1e9, 1e9), result);
            Assert.Empty(result);
            Assert.Equal(Box3.Empty, bvh.Bounds);
            if (n > 0)
            {
                // The returned face array must have been cleared (no face references kept alive by the pool).
                var rented = ArrayPool<Face3>.Shared.Rent(n);
                Assert.All(rented, f => Assert.Null(f));
                ArrayPool<Face3>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Booleans with the empty solid (zero-length pooled arrays) in both orders, interleaved with real ones.</summary>
    [Fact]
    public void BooleansWithEmptySolidsAndInterleavedSizesAreExact()
    {
        var rng = new Random(1207);
        var big = Rev4Workload.Ball(new Vec3(0, 0, 0), 2, 30_000);
        BigInteger u = (BigInteger)Mm * Mm * Mm * 6;
        for (int t = 0; t < 10; t++)
        {
            var a = Rev4Workload.LatticeHull(rng, 0, 0, 0, 3);
            var va = Volume6(a);
            Assert.Equal(va, Volume6(a | Solid.Empty));
            Assert.Equal(va, Volume6(Solid.Empty | a));
            Assert.Equal(va, Volume6(a - Solid.Empty));
            Assert.True((a & Solid.Empty).IsEmpty);
            Assert.True((Solid.Empty - a).IsEmpty);
            Assert.True((Solid.Empty & Solid.Empty).IsEmpty);
            var bx = Solid.Box(new Vec3(-Mm, -Mm, -Mm), new Vec3(Mm, Mm, Mm));
            var r = (big - bx) | a;
            AssertClosed(r, $"case {t}");
            Assert.Equal((BigInteger)8 * u, Volume6(bx).Num);
        }
    }

    // ------------------------------------------------------------------------------------------------ (13) FaceMerge

    /// <summary>The per-thread FaceMerge index under concurrency: merging the same piece sets on many threads at once.</summary>
    [Fact]
    public void ConcurrentFaceMergesEqualSequentialMerges()
    {
        var rng = new Random(1208);
        var sets = new List<List<Face3>>();
        for (int t = 0; t < 24; t++)
        {
            var face = Face3.FromGrid([new Vec3(0, 0, 0), new Vec3(1000, 0, 0), new Vec3(1000, 1000, 333), new Vec3(0, 1000, 333)]);
            var pieces = new List<Face3> { face };
            for (int c = 0; c < rng.Next(2, 9); c++)
            {
                var p0 = new Vec3(rng.Next(0, 1000), rng.Next(0, 1000), rng.Next(-50, 400));
                var pl = Plane3.FromPoints(p0, new Vec3(p0.X + rng.Next(-9, 10), p0.Y + rng.Next(-9, 10), p0.Z + 1000), new Vec3(p0.X + rng.Next(-9, 10), p0.Y + rng.Next(-9, 10), p0.Z - 7));
                if (pl.IsDegenerate) continue;
                var next = new List<Face3>();
                foreach (var f in pieces)
                    if (f.Split(pl, out var fr, out var bk, out _)) { next.Add(fr!); next.Add(bk!); }
                    else next.Add(f);
                pieces = next;
            }
            sets.Add(pieces.OrderBy(_ => rng.Next()).ToList());
        }
        string Merge(List<Face3> s)
        {
            var copy = new List<Face3>(s);
            FaceMerge.MergeCoplanar(copy);
            return Rev4Workload.Fingerprint(new Solid(copy));
        }
        var expected = sets.Select(Merge).ToList();
        var got = new string[sets.Count * 8];
        Parallel.For(0, got.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => got[i] = Merge(sets[i % sets.Count]));
        for (int i = 0; i < got.Length; i++) Assert.True(expected[i % sets.Count] == got[i], $"merge {i} differs");
        // Same thread again after all of that (index left filled by the previous calls).
        for (int i = 0; i < sets.Count; i++) Assert.Equal(expected[i], Merge(sets[i]));
    }

    // ------------------------------------------------------------------------------------------------ (15) parallel hull

    /// <summary>
    /// The hull builds single-triangle faces in parallel above 256 triangles; an exception inside Parallel.For would
    /// surface as AggregateException instead of the sequential exception type. Within contract FromGrid cannot throw
    /// for a hull triangle (non-degenerate by construction). Out of range (clusters near 2^61, where plane offsets
    /// overflow Int128), the triangulation itself fails first (KeyNotFoundException) or succeeds with planes that
    /// FromGrid accepts, so the parallel path never changes the outcome: sequential and parallel must agree.
    /// </summary>
    [Fact]
    public void HullThrowsTheSameExceptionTypeSequentialAndParallel()
    {
        var rng = new Random(1209);
        int compared = 0, threw = 0;
        for (int t = 0; t < 30 && threw < 3; t++)
        {
            // A cluster far from the origin: orient3d differences stay small (the hull itself is exact), but the plane
            // offset d = −n·a of FromGrid overflows Int128.
            long off = (1L << 61) + rng.NextInt64(0, 1L << 40);
            long size = 1L << rng.Next(30, 36);
            var pts = new List<Vec3>();
            for (int i = 0; i < 700; i++)
                pts.Add(new Vec3(off + rng.NextInt64(0, size), -off + rng.NextInt64(0, size), off / 3 + rng.NextInt64(0, size)));
            string Outcome(int p) => WithParallelism(p, () =>
            {
                try { return "ok " + Rev4Workload.Hash(ConvexHull3.Compute(pts)); }
                catch (Exception e) { return e.GetType().Name; }
            });
            string seq = Outcome(1), par = Outcome(4);
            output.WriteLine($"case {t}: sequential {seq}, parallel {par}");
            Assert.True(seq == par, $"case {t}: sequential '{seq}' vs parallel '{par}'");
            compared++;
            if (!seq.StartsWith("ok", StringComparison.Ordinal)) threw++;
        }
        Assert.True(compared > 0);
    }

    // ------------------------------------------------------------------------------------------------ differential

    /// <summary>
    /// Hashes of the exact fingerprints of <see cref="Rev4Workload.Run"/> computed with the reference commit 4384975
    /// (before round 3): the same source file compiled with REF_BUILD against that commit's src/ (assembly named
    /// Stykker.NanoCut.Tests). Round 3 claims bit-identical results (same faces, same order, same exact vertices).
    /// </summary>
    internal const string ReferenceDigests = """
lat0u 5F020168D52A446F
lat0i E3B0C44298FC1C14
lat0d 1D038299D1AA04B0
lat0e 6409547F77AA00C3
lat1u 2CBC2F695240DC57
lat1i E3B0C44298FC1C14
lat1d A4A3DB2A91598155
lat1e 456285A2881BD0AF
lat2u F7A3F1637FFA740C
lat2i E3B0C44298FC1C14
lat2d A2FE55B0467E325C
lat2e 94D8DE4C35A4D630
lat3u 874C7F1C15C139C6
lat3i E3B0C44298FC1C14
lat3d FA673510BFBCBA6B
lat3e 019548561CF46853
lat4u E01181D2D93D1F92
lat4i 84F9B80CAD9F6377
lat4d 482BF23EB3C84593
lat4e 9DB72D9CC69F8083
lat5u 89EEE75E7925824C
lat5i E3B0C44298FC1C14
lat5d 7371C7259F14CCD5
lat5e 2A3681E263741A40
lat6u C4F20C02ADAFFF38
lat6i E3B0C44298FC1C14
lat6d 52A77E091AD1EBD9
lat6e B5DA320C2070795F
lat7u 72E2A0ED98F6C7DE
lat7i 94F10BE95B1784FC
lat7d 2704A7C0C6AF1A3D
lat7e 29694B86FDD0ADB4
lat8u 68B3F2B84A0C0510
lat8i EBE18BA4D17C5A88
lat8d 5A499AF2060DC2AD
lat8e 3632D7DA71BDA6DF
lat9u 85065E4429A13A4A
lat9i E3B0C44298FC1C14
lat9d 8DB4596915BDA70D
lat9e 17D7DC130188FB41
lat10u FECDEBFAAE43C7F0
lat10i 1A156E642876D8B8
lat10d 854E9A34C18E8D9B
lat10e 023120528144A077
lat11u 4ED5AC4CEEDB1D9F
lat11i E3B0C44298FC1C14
lat11d 2F681D2345AFF430
lat11e 0B39020809282DD7
lat12u 4E940D8DF76026A0
lat12i E3B0C44298FC1C14
lat12d CE22584BD3EA6D96
lat12e 2F1B45D6099F98FC
lat13u 49739DB2A065E172
lat13i E3B0C44298FC1C14
lat13d 3EC038472426F55F
lat13e B3F844A591A6702C
lat14u 484A12D2BF64E35C
lat14i E3B0C44298FC1C14
lat14d 66A4F11593A1537E
lat14e E1D4EABD28332575
lat15u A06EA7D6F5BBE9FF
lat15i E3B0C44298FC1C14
lat15d D035FCCD7113DCCF
lat15e 17ECFDF0D7733943
lat16u A2660EBC7FEA0411
lat16i E8ACB98976A52F86
lat16d BF64EDA507B34617
lat16e AAAE1178CD118F31
lat17u 6B94F52F6B194333
lat17i DD543B0B89D1F12F
lat17d 0E3CF71F08366E3E
lat17e A1C6EA29F91DA44C
lat18u EF46FBB903FBF891
lat18i E3B0C44298FC1C14
lat18d 79F509B31F077F52
lat18e BFD35CA25F9D61A2
lat19u C2027E328B19397F
lat19i E3B0C44298FC1C14
lat19d 844E1A12080265F1
lat19e C7048AC1DA7A1364
lat20u DD54BCECDFAA4A8E
lat20i E3B0C44298FC1C14
lat20d 3484BD577C6D782C
lat20e 10D5200C3461783A
lat21u 0DE661505F6EEEA2
lat21i E3B0C44298FC1C14
lat21d A32628A57CFFA458
lat21e E21EE726D474137F
lat22u E73FF98F7D34A092
lat22i E3B0C44298FC1C14
lat22d C64DE3BAA7C84229
lat22e 3E4FCA8D80CF5968
lat23u 3C8CB9377A051C25
lat23i E3B0C44298FC1C14
lat23d C7206D2D431054B5
lat23e E6281489B39A7ABC
lat24u 2AD12607D92159B9
lat24i AC46719C85E33A2E
lat24d B692F14915841998
lat24e B04A882AD46AC86A
lat25u A13FE3B608E184D7
lat25i E3B0C44298FC1C14
lat25d ED8343D8FDAC708B
lat25e 6FA1338B00D56149
lat26u 0695D6BF7B6D43E8
lat26i E3B0C44298FC1C14
lat26d D0E57DA5E427B534
lat26e DD5226ABB4A06D1E
lat27u 32BD8DFD36E4CC90
lat27i E3B0C44298FC1C14
lat27d 9782BB858D70C1B1
lat27e 89C6629FF8B28A89
lat28u F00CEF873400D7FC
lat28i A3DEC2B7B24227BC
lat28d ED28B94021413AE2
lat28e A86DB3E795B356B1
lat29u 1DB611319B36FF0D
lat29i E3B0C44298FC1C14
lat29d BC3424817B51FFE0
lat29e 408C5A7756FC2A73
lat30u 0094C32F0E72840E
lat30i E3B0C44298FC1C14
lat30d 659B838526BF9AAF
lat30e CA474AAB96D46073
lat31u B1573C0C4E916384
lat31i A7EE059C164DA08F
lat31d 0C1BB603AE5C81EA
lat31e A3743A6E28BB9AE9
lat32u 1680C3F0D46464F4
lat32i 74EE8928EE973D39
lat32d 43C2FEC966039BD9
lat32e 4D1E27E80D8151CA
lat33u 34BCF5017FB97863
lat33i BAC66CEBA47B3796
lat33d 2554A1C5C48CCCD5
lat33e 8B6D00E85BD06228
lat34u 9F9D83E5723CF45A
lat34i E3B0C44298FC1C14
lat34d 52FC64E3869FC819
lat34e 88968024D476C0FE
lat35u 3A88FB4D0DF86077
lat35i A8C2DF68BBC23342
lat35d 2DAC951868CC5E46
lat35e DC7E34AB6FDCE918
lat36u FD384C2ED3CD4852
lat36i E3B0C44298FC1C14
lat36d 1E4EC9758DEBFA43
lat36e 860B299203B688F6
lat37u 4B2FA20B8BA8E2D0
lat37i E3B0C44298FC1C14
lat37d 3A14CA2087394492
lat37e ACB6F75C410078FD
lat38u 0EF958474A6E3EF0
lat38i E3B0C44298FC1C14
lat38d EFBAA1A5F674E040
lat38e 16C24468B2C65721
lat39u 6E3B4152B2603AAA
lat39i E3B0C44298FC1C14
lat39d E1203FD729EC9B5E
lat39e AB67EED681AF55C4
lat40u D0AC3893AD6F1F91
lat40i E3B0C44298FC1C14
lat40d 362E4195B2170C57
lat40e 6BFDB4ACBDDAC170
lat41u 1AFB390273D13099
lat41i DAFF6003E4BF9B6B
lat41d 531B0822E86ED883
lat41e 80C8DBAA7EEEC3DF
lat42u 51133D6137C1230A
lat42i E3B0C44298FC1C14
lat42d 46262407EF7A554D
lat42e 3DC4C0184D720890
lat43u 5BD51CE56E67B99B
lat43i E3B0C44298FC1C14
lat43d 68ECFFC4E83DB0AA
lat43e 4F00C65241BC2151
lat44u E35CF8759BFDBE62
lat44i E3B0C44298FC1C14
lat44d 488EBAAD502614AE
lat44e D6E796059C15DBDE
lat45u D1EC13DF32EF8350
lat45i E3B0C44298FC1C14
lat45d AE559FD71985868F
lat45e 54B4A9AE7176D546
lat46u CE4C051694312806
lat46i E3B0C44298FC1C14
lat46d 4F679BDB79190F45
lat46e 13608C44F8694F72
lat47u 3CAA532DF97F560B
lat47i E3B0C44298FC1C14
lat47d E742F334E6376381
lat47e 04824E3C563F5D11
chain0.0r F84FC86C69DBCC1B
chain0.0m E3B0C44298FC1C14
chain0.0u 546DD5A96AFF5BD1
chain0.1r 48A4EBBE101D17A8
chain0.1m 89F4F8E56B8BAB83
chain0.1u 5F48B799BAFD51BC
chain0.2r B365F61AC7F96C4A
chain0.2m 59AC64F6C0EDDC8B
chain0.2u 4C27C15EAB6C05B4
chain0.3r B4106F11FE4FACEB
chain0.3m C936CE32CB9E2FD2
chain0.3u 62B50E34861996D2
chain0.4r D2248DCF4D74A519
chain0.4m 9F68369C4E65EAEE
chain0.4u 0E9D75F448E619F4
chain0.5r D2248DCF4D74A519
chain0.5m E3B0C44298FC1C14
chain0.5u FDBD29CE7E498491
chain0.6r F358412AC0C9AC69
chain0.6m A9300362756C0CCC
chain0.6u 58DF69B5A3B564DE
chain0.7r DCB64DD9F87D4118
chain0.7m 9C1259291D193553
chain0.7u FD2EAFDC049A37C6
chain1.0r 04A26138711B12F4
chain1.0m 88B9E289D8C74CD6
chain1.0u 75F241B747B1F49C
chain1.1r 612598495D240739
chain1.1m 724BE3280FA3B921
chain1.1u 04A26138711B12F4
chain1.2r 621BB91AC7B22805
chain1.2m FFDE5815D84ACDB0
chain1.2u E0B46EF9BCCEA9DB
chain1.3r F7DEEE09CAB0E4D7
chain1.3m F7D2DF5ADEFA25EF
chain1.3u 5CEF89F5E28610F4
chain1.4r E38B17E6ED433C6C
chain1.4m 8E55C05A9AFB8DAF
chain1.4u 15FEFE63310B1796
chain1.5r 73599EB37D19DAC2
chain1.5m E59E04F8716F9628
chain1.5u 7DE8DF204DFC587A
chain1.6r 06BA3DDEBCCD2A7E
chain1.6m 2760E11AADDC084C
chain1.6u 2F31361A854CD93B
chain1.7r 9D47883A32B3A315
chain1.7m BB38D809D4D49517
chain1.7u 1876A6A4A0221DB9
chain2.0r 9E77DEF8485C86EF
chain2.0m 685F7E0C5FE20F5D
chain2.0u B9535EF8DC67F330
chain2.1r 9E77DEF8485C86EF
chain2.1m E3B0C44298FC1C14
chain2.1u 939128E3B467262A
chain2.2r 6221FC87AEDF2620
chain2.2m 0ACF8849E6B0BAEC
chain2.2u E8CBB40E3F5FFED0
chain2.3r 6221FC87AEDF2620
chain2.3m E3B0C44298FC1C14
chain2.3u 8E36FA1AD722F32A
chain2.4r E9C02D3527554293
chain2.4m 675C93BF3F776533
chain2.4u D38A23D36F1CAD55
chain2.5r 02AB98C9E801F052
chain2.5m EEB572F4FED7D03F
chain2.5u 511933341CB73258
chain2.6r 5D32D2D0911EC51B
chain2.6m 689290A5C2037B0C
chain2.6u C2BDC70742297543
chain2.7r A999E1B8C1E519C5
chain2.7m 784F4EA164EAE1A2
chain2.7u 788DF8584B317876
chain3.0r 3C88CC8E65D9E5EF
chain3.0m 4CE702425BAF9DD7
chain3.0u 34E68A8725426773
chain3.1r 4C7F9EE9EE4BD8A9
chain3.1m 4A58A09367ECFE13
chain3.1u 5D827409F46C3154
chain3.2r F36D666FE21FF9C2
chain3.2m B43382AB4A35B275
chain3.2u 8064E2EBA1F4EDC0
chain3.3r 10A2DF4609F5FFC3
chain3.3m DE78CA93C3DC0B31
chain3.3u A402F269D254E5EF
chain3.4r 0618DE9A313A1CC3
chain3.4m 798F38FF56BB00F6
chain3.4u 468583988BD5E364
chain3.5r 7CD448BA860BBF20
chain3.5m 1FFB0A4B376E9974
chain3.5u CEDF03D2D3974471
chain3.6r 9298988E4DDAE207
chain3.6m 85E93BC476C8F949
chain3.6u 1C27620787430D0C
chain3.7r 5831763E80A23C96
chain3.7m 395BE7DDF70AC952
chain3.7u C6706F9C11C418A4
chain4.0r BA9E87C79B26D518
chain4.0m 765D13B0C4358E1A
chain4.0u DA29C25E7535074E
chain4.1r 8C42C5BF82462DF2
chain4.1m 1BBCC2E4C43EC5CE
chain4.1u BA9E87C79B26D518
chain4.2r 7CF9AD1385FF9CFF
chain4.2m E33521CDBB7058C3
chain4.2u FCC7958AC59D288C
chain4.3r C822B55005887BE5
chain4.3m 702ACBDC6D084449
chain4.3u E7D57ED576C6248E
chain4.5r 84CA3630B71F43C2
chain4.5m 92D0CFAE866754AC
chain4.5u 5CEEE3C1975ECC3C
chain4.6r 9BB78BB246F5303D
chain4.6m 24794ABE5316C066
chain4.6u 041D384D43CAE523
chain4.7r 29AEE33CF46CF2D7
chain4.7m 807321BBFC862E8F
chain4.7u 1B4284315B5B4DDF
chain5.0r 830B5F34B7670F27
chain5.0m 3797F9CD0326FADF
chain5.0u 1EB652A63D1A6D0B
chain5.1r C6179D38AD5FC4A8
chain5.1m C85A47FDDD09A865
chain5.1u 06E3DF9E975490BB
chain5.2r A51DC6FA54028C28
chain5.2m E34285205E58683E
chain5.2u DDDA95D33DE02031
chain5.3r C9A702750C419A0B
chain5.3m 8A1A80C3AF53E7FC
chain5.3u 27F19478C87113EB
chain5.4r 80D246E91D2D0304
chain5.4m 68CAB5155E3D44C9
chain5.4u C9A702750C419A0B
chain5.5r 54A427A7943864B6
chain5.5m BC7E640C5DBC010C
chain5.5u 4EB699ABA670453D
chain5.6r 54A427A7943864B6
chain5.6m E3B0C44298FC1C14
chain5.6u 856E7083BFE7E414
chain5.7r CA3A6BE33F306638
chain5.7m 9DD236117DE580D9
chain5.7u AA734EDFEB4ECA3D
s12u B96A9BBD2DAA0E31
s12i 1552EBF66A8057E1
s12d 500A427BFE4591F7
s21d 09889A3E75837AD0
s34u A97B644B524A7F70
s34i 53875BD11C02A376
s34d C2BD6495BF19BCC3
s1box C8D4DD252D00A5E3
cyl 9CB913EA8AFDA049
cyl2 47AB5CC339EEC602
rot0u 635B625602867B05
rot0i 5099427CB9262CB3
rot0d 2E3807342D54D776
rot0dd CFAE4F8D1F273EA7
rot0di 51B7AB49FD73B97D
rot1u 9643A710F3B6469F
rot1i A41CFB438948C7A2
rot1d D8E0BB4B432671D2
rot1dd AC2B91307CDC1694
rot1di E36D2A421B85727C
rot2u 8E6E4B6D5B3D0E4D
rot2i 057862C525DE6963
rot2d FC1D8AE961690F59
rot2dd 7CA784B66138A6A3
rot2di 1A4BE10B88AE0B08
rot3u 4DA117082825428C
rot3i 0176A382DABEB5BB
rot3d 31E0B109DF5CC482
rot3dd D32C3F300B299687
rot3di 8F55CB1A121FE558
rot4u 98892586E186AE2A
rot4i 02866C76B429D79D
rot4d 7385D254AD709A88
rot4dd 8E249BF6C302D57A
rot4di 55BC1F85DD6658B4
rot5u 2E8CBF892267B3AA
rot5i 7D1D2EF476767209
rot5d A7F82C80B5157547
rot5dd 62ADE8B9F75C9251
rot5di A50DC75418E12B44
rot6u 79C66EABDC8CA9CA
rot6i 18677CF7CB8CC640
rot6d A9438695DBE44ECB
rot6dd 83EEDDBA643A45E7
rot6di F6E608F141FCB7AC
rot7u 04D2F59D7B0B47CB
rot7i 8F34BCA5D4FE5C1E
rot7d 077B04F4DD818DF6
rot7dd 0F1DE832BD605FE4
rot7di 2D7E772404DB031D
rot8u 098153C31061ADAD
rot8i 09E80F6DD9808E19
rot8d D72A442F136C555D
rot8dd 8F004CE75B7E4ABE
rot8di 1375CC90007A7F02
rot9u 5353EA050CA0C2CA
rot9i 592B9638F9548452
rot9d 6D76EE8A7E7F34D4
rot9dd 7D070E2ED8A51739
rot9di 5C5006052EFC7D47
hull 15866EF58F4660E3
hull2 DBF4A7CF56B1D77E
hullx E3B0C44298FC1C14
""";

    [Fact]
    public void WorkloadIsBitIdenticalToReferenceCommit()
    {
        var expected = ReferenceDigests.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var got = Rev4Workload.Run();
        Assert.Equal(expected.Length, got.Count);
        var diffs = new List<string>();
        for (int i = 0; i < got.Count; i++) if (expected[i] != got[i]) diffs.Add($"{expected[i]} -> {got[i]}");
        output.WriteLine($"{got.Count} results, {diffs.Count} differ");
        Assert.True(diffs.Count == 0, "differs from 4384975: " + string.Join("; ", diffs.Take(10)));
    }
}
#endif
