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
        // Geometric digest: the exact rational volume from two reference points (equal only for a closed surface). The
        // face decomposition may legitimately change between versions (fewer splits); the point set may not.
        void Add(string name, Solid s)
        {
            var v0 = OptimizationVerificationTests.Volume6(s, 0, 0, 0);
            var v1 = OptimizationVerificationTests.Volume6(s, 7_000_001, -3_000_017, 11_000_003);
            lines.Add($"{name} {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{v0.Num}/{v0.Den} {v1.Num}/{v1.Den}")))[..16]}");
        }

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
    /// Geometric digests of <see cref="Rev4Workload.Run"/>: per result the exact rational volume from two reference
    /// points. Originally bit-identical fingerprints against commit 4384975 (round 4 kept faces, order and vertices); from
    /// round 6 on fewer splits change the face decomposition, so the digests were regenerated from commit b9a5faf with the
    /// geometric digest. The exact volumes of all 396 results were checked to be equal between b9a5faf and the change.
    /// </summary>
    internal const string ReferenceDigests = """
lat0u 12EBF998A03DB930
lat0i 15BB2671F7721BC7
lat0d 43BFA03EEF0C7269
lat0e 24B2637EAB62B67D
lat1u CE7B1D5FDC1E1AFC
lat1i 15BB2671F7721BC7
lat1d 6333F38AAFF678E5
lat1e 70EA9EDB39758E90
lat2u AA45EB70A66A3502
lat2i 15BB2671F7721BC7
lat2d 24B2637EAB62B67D
lat2e 70EA9EDB39758E90
lat3u 7D6944B30698FD73
lat3i 15BB2671F7721BC7
lat3d 0088CF856743B8F9
lat3e 57A6A6708A6D100C
lat4u FEA2CE16288B7AB8
lat4i CC1B3C8DEAF5588F
lat4d 5D5344E87553698A
lat4e C1A6546E77CED74E
lat5u AA4EF4F2512E3D94
lat5i 15BB2671F7721BC7
lat5d 864998128634C576
lat5e EED46632133419A7
lat6u A267C035A7F68853
lat6i 15BB2671F7721BC7
lat6d 43BFA03EEF0C7269
lat6e 8F5B17A92CF4DAA7
lat7u D18021705219C606
lat7i D1E0AE7706ED4D5C
lat7d E03252CE17E1A7AC
lat7e E7464F868057B9E3
lat8u F64BC11E8FD28FBC
lat8i 40E67DA066B2C94A
lat8d D4DD882A0F6E99EA
lat8e F4C8E6616CB23B67
lat9u 98B8AC3BC41152B7
lat9i 15BB2671F7721BC7
lat9d CE7B1D5FDC1E1AFC
lat9e D2DB99BD6085DDC6
lat10u E1F159FBFC91984C
lat10i 44269B5DFC152D4A
lat10d 88C3A522C58948E6
lat10e 3D810742542E2F92
lat11u E3C534B9F7F9554A
lat11i 15BB2671F7721BC7
lat11d 6AB53ABF9213F629
lat11e AA4EF4F2512E3D94
lat12u 018EAA14743DE04E
lat12i 15BB2671F7721BC7
lat12d FDC6AFA2A9A7B5CF
lat12e EB4D64DA5654216C
lat13u E3C534B9F7F9554A
lat13i 15BB2671F7721BC7
lat13d 70EA9EDB39758E90
lat13e 70F181B70CC865EF
lat14u 5B83007A36195EFD
lat14i 15BB2671F7721BC7
lat14d 57A6A6708A6D100C
lat14e 3921699665C7F47E
lat15u F33394C4C9E4EA35
lat15i 15BB2671F7721BC7
lat15d 7879EDEA6386F6FF
lat15e EB4D64DA5654216C
lat16u EDBDC6D0BAD42AE9
lat16i BF08B62E48E71C6A
lat16d C3C745D5FCFD8ADB
lat16e A6DE6758B1BFE5FF
lat17u B9DCBEBA87BEFD8C
lat17i 32039BB36ACFE0EE
lat17d C48F9CE784DE8135
lat17e AFE31DC8BB1BE7DB
lat18u EA13BA738982F7EB
lat18i 15BB2671F7721BC7
lat18d 7593F70812233B08
lat18e D515BE01A4E667F3
lat19u F2B2810696626A75
lat19i 15BB2671F7721BC7
lat19d CE7B1D5FDC1E1AFC
lat19e 692F07C6576BC132
lat20u 94412EF28F075309
lat20i 15BB2671F7721BC7
lat20d 57A6A6708A6D100C
lat20e E554DABE74098E91
lat21u E3C534B9F7F9554A
lat21i 15BB2671F7721BC7
lat21d 6811A34F183CAE77
lat21e 24B2637EAB62B67D
lat22u D75D569695C23EC6
lat22i 15BB2671F7721BC7
lat22d EB02ECC3723A9049
lat22e C86482EBE8D027B7
lat23u 7593F70812233B08
lat23i 15BB2671F7721BC7
lat23d 7879EDEA6386F6FF
lat23e A0DD89F6B7042598
lat24u A073338C72D5B6DC
lat24i A41A39CD815D7A3B
lat24d 5FA683EF0E5E84F5
lat24e FEB0C5992050001F
lat25u 6333F38AAFF678E5
lat25i 15BB2671F7721BC7
lat25d 8B2CBE027C6355AE
lat25e 8F5B17A92CF4DAA7
lat26u 3921699665C7F47E
lat26i 15BB2671F7721BC7
lat26d 704A9A433F2C242C
lat26e 8F5B17A92CF4DAA7
lat27u 8AC248315B38E68A
lat27i 15BB2671F7721BC7
lat27d EB02ECC3723A9049
lat27e D515BE01A4E667F3
lat28u 0405CEB0FE241CC8
lat28i C1FF4977521D01EA
lat28d 85AF5913BCABF11B
lat28e 85AF5913BCABF11B
lat29u D515BE01A4E667F3
lat29i 15BB2671F7721BC7
lat29d 6811A34F183CAE77
lat29e 8F5B17A92CF4DAA7
lat30u FDC6AFA2A9A7B5CF
lat30i 15BB2671F7721BC7
lat30d 864998128634C576
lat30e F33394C4C9E4EA35
lat31u 44F3556601FB13E4
lat31i 1B79EB5623B93D94
lat31d 4440FA5EB2FA0AF4
lat31e 331A7455B1CFF589
lat32u D41DB856C0C1C8DF
lat32i B2E3FC6ED380F644
lat32d 9910059219038932
lat32e A40307486AE70F3A
lat33u 581773193FFC2E60
lat33i CA9AD27C94C2B9E1
lat33d 5D98BC6162D9CD27
lat33e 721596078C968405
lat34u 1890D5403DA0558D
lat34i 15BB2671F7721BC7
lat34d 9819D1416C6BD292
lat34e 12EBF998A03DB930
lat35u 3466A4A4362895B2
lat35i ACAFAB7FC4F9669E
lat35d 7CF69C856066EF19
lat35e 0B77D5C2D5FB9D61
lat36u 1D218F8380F4DB19
lat36i 15BB2671F7721BC7
lat36d A267C035A7F68853
lat36e 12EBF998A03DB930
lat37u AA45EB70A66A3502
lat37i 15BB2671F7721BC7
lat37d 43BFA03EEF0C7269
lat37e 864998128634C576
lat38u F37CFC95BCF66790
lat38i 15BB2671F7721BC7
lat38d AA45EB70A66A3502
lat38e E3C534B9F7F9554A
lat39u D93F20C9D704AAC6
lat39i 15BB2671F7721BC7
lat39d 43BFA03EEF0C7269
lat39e FDC6AFA2A9A7B5CF
lat40u E3C534B9F7F9554A
lat40i 15BB2671F7721BC7
lat40d 483FF7B8A50A64E5
lat40e C86482EBE8D027B7
lat41u E1EB626221D26A17
lat41i 5F7601D40181F664
lat41d E5B08AB3180FC7EC
lat41e B8186809AF046060
lat42u 704A9A433F2C242C
lat42i 15BB2671F7721BC7
lat42d 8F5B17A92CF4DAA7
lat42e AFBD97AA9D43FD52
lat43u F33394C4C9E4EA35
lat43i 15BB2671F7721BC7
lat43d 8B2CBE027C6355AE
lat43e 483FF7B8A50A64E5
lat44u AFBD97AA9D43FD52
lat44i 15BB2671F7721BC7
lat44d EED46632133419A7
lat44e 0A897F7B8622D2FE
lat45u 33DE526F0E096E45
lat45i 15BB2671F7721BC7
lat45d A267C035A7F68853
lat45e CD4C40535EBB157B
lat46u EB4D64DA5654216C
lat46i 15BB2671F7721BC7
lat46d 483FF7B8A50A64E5
lat46e DF9DA39796487CDD
lat47u 704A9A433F2C242C
lat47i 15BB2671F7721BC7
lat47d 0A897F7B8622D2FE
lat47e 70F181B70CC865EF
chain0.0r A7B59651787D6397
chain0.0m 15BB2671F7721BC7
chain0.0u 054CA2D8393AD483
chain0.1r 398AB26D1E769FD4
chain0.1m 401A3CC6B28AC266
chain0.1u 97B67993BC366CE3
chain0.2r F6FCAF30284DCEEE
chain0.2m D486B2922CFD0C90
chain0.2u 4EADBE62A6BD6760
chain0.3r 78152277564C8526
chain0.3m FD9F8723A55ACEA2
chain0.3u EBB8E5E9B2D0A7A0
chain0.4r D244EC85FECBBCC8
chain0.4m 5E262E05ACBC3029
chain0.4u BC6A05DBEAEB6260
chain0.5r D244EC85FECBBCC8
chain0.5m 15BB2671F7721BC7
chain0.5u 28D6EA343963EE02
chain0.6r 8964A237988FBA4D
chain0.6m C3D3929F0D365824
chain0.6u 851A09EBC1B5192C
chain0.7r A0C6791EFE6E1F78
chain0.7m EF2569BA42987AC6
chain0.7u ACD2A11824411F72
chain1.0r 55B564504189D2EF
chain1.0m 115E63945F82AAE7
chain1.0u 3D5866F9F2292F04
chain1.1r 252D95499CF0C7D3
chain1.1m 6333F38AAFF678E5
chain1.1u 55B564504189D2EF
chain1.2r B2653420C92C23AF
chain1.2m 5F7F938E7225C41D
chain1.2u 443DE03C4E60F793
chain1.3r 04D8EB61F4F08C91
chain1.3m B5BBBE173F252224
chain1.3u AB8999F40A562FC7
chain1.4r 6A4C67D85B45960F
chain1.4m 4D75466F876A1CA5
chain1.4u 9924945A96EA9C7B
chain1.5r 1D268C7146E4174D
chain1.5m C7AD8F831883D036
chain1.5u 912C705C2B1ECC45
chain1.6r 0A8FEEF49C161DB7
chain1.6m 88A06A469631E407
chain1.6u AEFAF18123C7B2D4
chain1.7r FB611FB5BF501BA2
chain1.7m 1A5525E251C25CF8
chain1.7u A6143D18DC4B69EE
chain2.0r C6A0E9711AC5362C
chain2.0m 483FF7B8A50A64E5
chain2.0u A7B59651787D6397
chain2.1r C6A0E9711AC5362C
chain2.1m 15BB2671F7721BC7
chain2.1u A7B59651787D6397
chain2.2r D4FD4E378B56260A
chain2.2m 4F867F99A79BE5FD
chain2.2u A718BE5CACF91259
chain2.3r D4FD4E378B56260A
chain2.3m 15BB2671F7721BC7
chain2.3u 37C03DE878367D14
chain2.4r C983592A97832D7E
chain2.4m 4FFD0658A9034DCA
chain2.4u 1FED4DDE63ABCA97
chain2.5r 3FB2A4276151C471
chain2.5m 23020B6B932A3DE1
chain2.5u 1F15E5ECC361508E
chain2.6r 2326B221CDE2B9B3
chain2.6m 15B80249690CFB7A
chain2.6u 4F2A7F715F532264
chain2.7r 415376AD4267E6A9
chain2.7m 35FCE575726CF9D7
chain2.7u D3F7C068FAAD60DF
chain3.0r 9EDE299295F29E5E
chain3.0m D1DE715F0CE51B58
chain3.0u 516F80FCA4CA072D
chain3.1r B1FF345F2867DB5D
chain3.1m 52069E06AFC0072C
chain3.1u EFEB4054358E7C65
chain3.2r CBE8F7F9B4FA47FE
chain3.2m 9A982B42650B866D
chain3.2u D2943947806F16B9
chain3.3r A3A8FD17B1BE7D11
chain3.3m DE852C0C9C739DFA
chain3.3u 5DE16F8EF288B3EF
chain3.4r 658A030657E00C46
chain3.4m DA4D2D68F8C2B4C6
chain3.4u 762364DA1D986075
chain3.5r 88F08A9599959632
chain3.5m 9E0E70FADE2770FC
chain3.5u 5F27275F08EF0B6C
chain3.6r B3391D22A15AE1E2
chain3.6m CE73AC988C23D709
chain3.6u 2D73E98EB93102EA
chain3.7r 0BBED3543A56C72C
chain3.7m 5F2993B2DA786749
chain3.7u D6A98ABF47248BF8
chain4.0r 91C7ADF4C9FDFC4A
chain4.0m 9753E3FBAC126F1F
chain4.0u 6837F32C9DC6A715
chain4.1r 19A9633F498DD327
chain4.1m 24B2637EAB62B67D
chain4.1u 91C7ADF4C9FDFC4A
chain4.2r 01D4355853AC097D
chain4.2m C4B28E7FE2C9F32A
chain4.2u 0EC33DC9BB443123
chain4.3r CCF82ADDE026BD14
chain4.3m E43B74E6B87A159A
chain4.3u 6EE3AE95E9F1B9EB
chain4.5r 5D45655C138A769F
chain4.5m 13B014CD8331A26D
chain4.5u 1CBE96FEE37CE35F
chain4.6r A8B07E174333AEE6
chain4.6m 52FEE7E7510BA896
chain4.6u 22AEEBFCE0A0A77B
chain4.7r 12498E3F91F15A45
chain4.7m 39981CBF01DE06AE
chain4.7u 4221C71D8D34EDC7
chain5.0r 40F9193377E4FF0D
chain5.0m 3D961117B64D973D
chain5.0u 839946C02F2E1EB8
chain5.1r 7E06C66821337B63
chain5.1m E9DA497E333DB3A6
chain5.1u 7AFE4C4A41CED7B6
chain5.2r 24E31B4D6378D663
chain5.2m 0CE6F6AF31E46615
chain5.2u 0B2D0D37309C0A74
chain5.3r E7FB9F1882FACDF5
chain5.3m AFAB46DE988C1DD7
chain5.3u A9909C458EDE0EA7
chain5.4r D2DD928F252617C2
chain5.4m AA4EF4F2512E3D94
chain5.4u E7FB9F1882FACDF5
chain5.5r 442543971DB982A7
chain5.5m 51D92FF8E57A0BF1
chain5.5u C390B44E08243BD7
chain5.6r 442543971DB982A7
chain5.6m 15BB2671F7721BC7
chain5.6u B435B2BF47F657DA
chain5.7r F4C8F4DE2E7B491D
chain5.7m BBAA71DBCF4A88DD
chain5.7u 6DDA595B402F8E5B
s12u E0542B99812ECEF7
s12i F887EC0BF62C627E
s12d 39BF7A5E4846ED61
s21d 433F529FE3B6C754
s34u ABC83BF698EFFAB0
s34i 525DAB6748A3D74E
s34d 35469C376A734984
s1box 9128971963038BF2
cyl 007658416AF7E2CB
cyl2 32CE69921FCDD615
rot0u AAEAF61CD15C05AF
rot0i F58BB44F8A3EF048
rot0d D480C5E3B17188A0
rot0dd 395EE9DC84D2B8C9
rot0di 446ED1EBE47CEE1A
rot1u B081EB4AB4489976
rot1i 3BDC690F1FFF921D
rot1d 8C162ECEF4FD9217
rot1dd 518B636A26C94FDC
rot1di 5EDACD9CC08E925A
rot2u E18B84CF09F2FB86
rot2i 513849B62D7B3BBF
rot2d 3DE5D8BFB17A4730
rot2dd 13FBE6E56F6542EE
rot2di 7B21D3FA1777C25E
rot3u A40E8CB91807EF78
rot3i 4D71A084BBA5EFF1
rot3d 4F0AC39DEF797574
rot3dd 756ABE96D1B235AC
rot3di D531EDFC8B967F4A
rot4u AE24523DF54797BA
rot4i 17127D0FCDCDC1A3
rot4d E5B8DC805DAE9257
rot4dd 0ABA2CBCB2D51851
rot4di A59A21CAD419D7EE
rot5u 00F519E61F280DD7
rot5i A41CCF776BF9EC22
rot5d 3754E783BF239C6F
rot5dd 5BA3DAE965C33C76
rot5di 3E50568CBB8FAA99
rot6u 955D71F0EFBA2A38
rot6i E7FB131FAEEB4DD9
rot6d 22C5B3BA9AE09919
rot6dd F48BC17EDA4AFBF8
rot6di 81DD991240F3BD54
rot7u D2C8854C1C4D8523
rot7i AAFBA2AB6A89020C
rot7d BC40FF939AD22502
rot7dd 83C0A8092BC1E64E
rot7di F3C403C797873DC4
rot8u 4EEB182171677BA3
rot8i DC057C8DAC32CD44
rot8d 8411E3D22708C76B
rot8dd 927851AA5A5ADF70
rot8di 6EB17A4EFD5C0618
rot9u 25297F0974D8BE5B
rot9i 5DDC0F95D1678476
rot9d A30D2496F4B2409E
rot9dd 7D8625B4AAB93B69
rot9di 9E0DCC3AC07C21C8
hull 4C8C61C697DB51C8
hull2 4CEF692A496484A3
hullx 15BB2671F7721BC7
""";

    [Fact]
    public void WorkloadIsGeometricallyIdenticalToReference()
    {
        var expected = ReferenceDigests.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var got = Rev4Workload.Run();
        Assert.Equal(expected.Length, got.Count);
        var diffs = new List<string>();
        for (int i = 0; i < got.Count; i++) if (expected[i] != got[i]) diffs.Add($"{expected[i]} -> {got[i]}");
        output.WriteLine($"{got.Count} results, {diffs.Count} differ");
        Assert.True(diffs.Count == 0, "geometry differs from the reference: " + string.Join("; ", diffs.Take(10)));
    }
}
#endif
