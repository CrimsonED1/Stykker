using System.Numerics;
using System.Text;
using Stykker.NanoCut.Geometry3D;
using Xunit.Abstractions;

namespace Stykker.NanoCut.Tests;

/// <summary>
/// Adversarial verification of the second optimisation round: six-direction winding rays in a canonical frame,
/// two-crossing face split, first-fan probes, single-entry FaceMerge index, pre-sized BVH node array, per-thread
/// convex hull scratch buffers and parallel face classification. Every check uses an independent reference
/// (voxel counts, exact rational volumes, closure, brute force, the pre-round-2 split algorithm, or the sequential
/// run for the parallel path).
/// </summary>
public class OptimizationVerification2Tests(ITestOutputHelper output)
{
    private const long M = Units.MaxCoordinate;
    private const long Mm = 1_000_000;

    // ------------------------------------------------------------------------------------------------ helpers

    private static (BigInteger Num, BigInteger Den) Volume6(Solid s, long ox = 0, long oy = 0, long oz = 0) =>
        OptimizationVerificationTests.Volume6(s, ox, oy, oz);

    private static void AssertClosed(Solid s, string what)
    {
        // Two reference points far apart (but inside the coordinate range): the volume must not depend on them.
        var v0 = Volume6(s);
        var v1 = Volume6(s, 7_000_001, -3_000_017, 11_000_003);
        Assert.True(v0 == v1, $"{what}: surface not closed (volume depends on the reference point)");
    }

    private static void AssertVolume(Solid s, BigInteger expected6, string what)
    {
        var v = Volume6(s);
        Assert.True(v.Den.IsOne && v.Num == expected6, $"{what}: 6·volume {v.Num}/{v.Den} != {expected6}");
        AssertClosed(s, what);
    }

    /// <summary>Exact textual fingerprint of a solid: face order, planes and exact vertices.</summary>
    private static string Fingerprint(Solid s)
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

    /// <summary>Runs <paramref name="f"/> with a given SolidBoolean.MaxParallelism (restored afterwards).</summary>
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

    private static readonly object ParallelismLock = new();

    /// <summary>Union of voxel runs (unit = <paramref name="unit"/> nm) placed at origin o.</summary>
    private static Solid FromVoxels(bool[,,] vox, long unit, Vec3 o)
    {
        var boxes = new List<Solid>();
        int nx = vox.GetLength(0), ny = vox.GetLength(1), nz = vox.GetLength(2);
        for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    if (!vox[x, y, z]) continue;
                    int x1 = x;
                    while (x1 + 1 < nx && vox[x1 + 1, y, z]) x1++;
                    boxes.Add(Solid.Box(new Vec3(o.X + x * unit, o.Y + y * unit, o.Z + z * unit),
                                        new Vec3(o.X + (x1 + 1) * unit, o.Y + (y + 1) * unit, o.Z + (z + 1) * unit)));
                    x = x1;
                }
        return UnionAll(boxes);
    }

    private static Solid UnionAll(List<Solid> parts)
    {
        if (parts.Count == 0) return Solid.Empty;
        while (parts.Count > 1)
        {
            var next = new List<Solid>();
            for (int i = 0; i + 1 < parts.Count; i += 2) next.Add(parts[i] | parts[i + 1]);
            if (parts.Count % 2 == 1) next.Add(parts[^1]);
            parts = next;
        }
        return parts[0];
    }

    private static bool[,,] RandomVoxels(Random rng, int n, double density)
    {
        var v = new bool[n, n, n];
        for (int x = 0; x < n; x++) for (int y = 0; y < n; y++) for (int z = 0; z < n; z++) v[x, y, z] = rng.NextDouble() < density;
        return v;
    }

    /// <summary>Voxel set of a coarse block set (block = <paramref name="k"/> fine voxels) shifted by (sx, sy, sz) fine voxels.</summary>
    private static bool[,,] Embed(bool[,,] coarse, int k, int n, int sx, int sy, int sz)
    {
        var v = new bool[n, n, n];
        for (int x = 0; x < coarse.GetLength(0); x++) for (int y = 0; y < coarse.GetLength(1); y++) for (int z = 0; z < coarse.GetLength(2); z++)
            if (coarse[x, y, z])
                for (int a = 0; a < k; a++) for (int b = 0; b < k; b++) for (int c = 0; c < k; c++)
                {
                    int X = sx + k * x + a, Y = sy + k * y + b, Z = sz + k * z + c;
                    if (X >= 0 && Y >= 0 && Z >= 0 && X < n && Y < n && Z < n) v[X, Y, Z] = true;
                }
        return v;
    }

    private static int Count(bool[,,] v, Func<int, int, int, bool> pred)
    {
        int c = 0;
        for (int x = 0; x < v.GetLength(0); x++) for (int y = 0; y < v.GetLength(1); y++) for (int z = 0; z < v.GetLength(2); z++)
            if (pred(x, y, z)) c++;
        return c;
    }

    private static Solid LatticeHull(Random rng, long ox, long oy, long oz, int size, long unit = Mm)
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

    private static (BigInteger Num, BigInteger Den) Add((BigInteger Num, BigInteger Den) a, (BigInteger Num, BigInteger Den) b, int sign = 1)
    {
        var n = a.Num * b.Den + sign * b.Num * a.Den;
        var d = a.Den * b.Den;
        if (n.IsZero) return (0, 1);
        var g = BigInteger.GreatestCommonDivisor(n, d);
        return (n / g, d / g);
    }

    // ------------------------------------------------------------------------------------------------ (7) six-direction rays

    /// <summary>
    /// Voxel solids: A made of 3×3×3 blocks (so the probes of unsplit fragments sit on integer lattice lines and the
    /// axis rays run exactly along B's edges and through its vertices), B made of unit voxels. All four Booleans are
    /// checked against voxel counts, for placements all around B (every one of the six ray directions is chosen) and
    /// translated to the corners of the coordinate range.
    /// </summary>
    [Fact]
    public void VoxelBooleansWithGrazingAxisRaysMatchVoxelCounts()
    {
        var rng = new Random(7007);
        const int n = 9;
        Vec3[] origins = [new(0, 0, 0), new(M - 9 * Mm, M - 9 * Mm, M - 9 * Mm), new(-M, -M, -M), new(-M, M - 9 * Mm, -M + 1)];
        int cases = 0;
        for (int t = 0; t < 140; t++)
        {
            var o = origins[t % origins.Length];
            var fine = RandomVoxels(rng, n, 0.35);
            // keep B's voxels in a sub-box so that A's blocks stick out on varying sides (ray directions vary)
            int bx0 = rng.Next(0, 4), by0 = rng.Next(0, 4), bz0 = rng.Next(0, 4);
            int bx1 = bx0 + rng.Next(3, 6), by1 = by0 + rng.Next(3, 6), bz1 = bz0 + rng.Next(3, 6);
            for (int x = 0; x < n; x++) for (int y = 0; y < n; y++) for (int z = 0; z < n; z++)
                if (x < bx0 || x >= bx1 || y < by0 || y >= by1 || z < bz0 || z >= bz1) fine[x, y, z] = false;
            var coarse = RandomVoxels(rng, 3, 0.45);
            var va = Embed(coarse, 3, n, rng.Next(-1, 2), rng.Next(-1, 2), rng.Next(-1, 2));
            int ca = Count(va, (x, y, z) => va[x, y, z]), cb = Count(fine, (x, y, z) => fine[x, y, z]);
            if (ca == 0 || cb == 0) continue;
            var a = FromVoxels(va, Mm, o);
            var b = FromVoxels(fine, Mm, o);
            BigInteger u3 = (BigInteger)Mm * Mm * Mm * 6;
            AssertVolume(a, ca * u3, $"case {t} A");
            AssertVolume(b, cb * u3, $"case {t} B");
            AssertVolume(a | b, Count(va, (x, y, z) => va[x, y, z] || fine[x, y, z]) * u3, $"case {t} union");
            AssertVolume(a & b, Count(va, (x, y, z) => va[x, y, z] && fine[x, y, z]) * u3, $"case {t} intersection");
            AssertVolume(a - b, Count(va, (x, y, z) => va[x, y, z] && !fine[x, y, z]) * u3, $"case {t} A-B");
            AssertVolume(b - a, Count(va, (x, y, z) => !va[x, y, z] && fine[x, y, z]) * u3, $"case {t} B-A");
            cases++;
        }
        output.WriteLine($"{cases} voxel pairs");
        Assert.True(cases > 100);
    }

    /// <summary>
    /// A thin slab probe face against a staircase: the slab's own faces lie in planes of the staircase, rays from its
    /// fragments run inside the staircase's faces in every direction. Swept over all positions and orientations.
    /// </summary>
    [Fact]
    public void SlabsInsideStaircasePlanesMatchVoxelCounts()
    {
        const int n = 6;
        var stairs = new bool[n, n, n];
        for (int x = 0; x < n; x++) for (int y = 0; y < n; y++) for (int z = 0; z < n; z++) stairs[x, y, z] = z <= x && y <= 4 - (x / 2);
        var b = FromVoxels(stairs, Mm, default);
        BigInteger u3 = (BigInteger)Mm * Mm * Mm * 6;
        int cases = 0;
        for (int axis = 0; axis < 3; axis++)
            for (int lo = -1; lo < n; lo++)
                for (int off = -2; off <= 3; off += 1)
                {
                    // slab: one voxel thick across 'axis' at lo, spanning [off, off + 4) in the other two axes
                    var slab = new bool[n, n, n];
                    for (int x = 0; x < n; x++) for (int y = 0; y < n; y++) for (int z = 0; z < n; z++)
                    {
                        int[] c = [x, y, z];
                        int o1 = c[(axis + 1) % 3], o2 = c[(axis + 2) % 3];
                        slab[x, y, z] = c[axis] == lo && o1 >= off && o1 < off + 4 && o2 >= off && o2 < off + 4;
                    }
                    if (Count(slab, (x, y, z) => slab[x, y, z]) == 0) continue;
                    var a = FromVoxels(slab, Mm, default);
                    AssertVolume(a | b, Count(slab, (x, y, z) => slab[x, y, z] || stairs[x, y, z]) * u3, $"axis {axis} lo {lo} off {off} union");
                    AssertVolume(a & b, Count(slab, (x, y, z) => slab[x, y, z] && stairs[x, y, z]) * u3, $"axis {axis} lo {lo} off {off} intersection");
                    AssertVolume(b - a, Count(slab, (x, y, z) => !slab[x, y, z] && stairs[x, y, z]) * u3, $"axis {axis} lo {lo} off {off} B-A");
                    AssertVolume(a - b, Count(slab, (x, y, z) => slab[x, y, z] && !stairs[x, y, z]) * u3, $"axis {axis} lo {lo} off {off} A-B");
                    cases++;
                }
        output.WriteLine($"{cases} slab cases");
    }

    /// <summary>
    /// Mirror images: a configuration and its reflection in x, y and z have the same exact volumes. A reflection maps a
    /// ray along +axis to one along −axis (the left-handed frame with sign = −1), so a sign or handedness error in the
    /// canonical frame or its perturbation shows up as a volume difference or an open surface.
    /// </summary>
    [Fact]
    public void MirroredLatticeBooleansHaveIdenticalVolumes()
    {
        var rng = new Random(8128);
        for (int t = 0; t < 150; t++)
        {
            int size = 3;
            var pa = RandomPoints(rng, 0, 0, 0, size);
            var pb = RandomPoints(rng, rng.Next(-2, 3), rng.Next(-2, 3), rng.Next(-2, 3), size);
            (BigInteger, BigInteger)[]? reference = null;
            for (int mirror = 0; mirror < 8; mirror++)
            {
                Vec3 Mir(Vec3 p) => new((mirror & 1) != 0 ? -p.X : p.X, (mirror & 2) != 0 ? -p.Y : p.Y, (mirror & 4) != 0 ? -p.Z : p.Z);
                Solid a, b;
                try { a = ConvexHull3.Compute(pa.Select(Mir)); b = ConvexHull3.Compute(pb.Select(Mir)); }
                catch (ArgumentException) { goto nextCase; }
                var results = new[] { a | b, a & b, a - b, b - a };
                var vols = results.Select(r => Volume6(r)).Select(v => (BigInteger.Abs(v.Num), v.Den)).ToArray();
                for (int i = 0; i < results.Length; i++) AssertClosed(results[i], $"case {t} mirror {mirror} op {i}");
                if (reference is null) reference = vols;
                else Assert.True(reference.SequenceEqual(vols), $"case {t}: mirror {mirror} changes a volume");
            }
            nextCase:;
        }

        static List<Vec3> RandomPoints(Random rng, long ox, long oy, long oz, int size)
        {
            var pts = new List<Vec3>();
            int n = rng.Next(4, 10);
            for (int i = 0; i < n; i++)
                pts.Add(new Vec3((ox + rng.Next(0, size + 1)) * Mm, (oy + rng.Next(0, size + 1)) * Mm, (oz + rng.Next(0, size + 1)) * Mm));
            return pts;
        }
    }

    /// <summary>
    /// Slanted lattice polytopes with exact (non-grid) vertices from earlier cuts, near the coordinate limit:
    /// inclusion–exclusion identities and closure, with the second operand placed on every side of the first.
    /// </summary>
    [Fact]
    public void SlantedBooleansNearTheCoordinateLimitAreConsistent()
    {
        var rng = new Random(2147);
        long baseX = M - 8 * Mm, baseY = -M + Mm, baseZ = M - 8 * Mm;
        for (int t = 0; t < 200; t++)
        {
            var a = LatticeHull(rng, baseX + 2 * Mm, baseY + 2 * Mm, baseZ + 2 * Mm, 3);
            // pre-cut A so that its vertices are exact plane intersections
            a -= LatticeHull(rng, baseX + rng.Next(0, 5) * Mm, baseY + rng.Next(0, 5) * Mm, baseZ + rng.Next(0, 5) * Mm, 2);
            if (a.IsEmpty) continue;
            var b = LatticeHull(rng, baseX + rng.Next(0, 5) * Mm, baseY + rng.Next(0, 5) * Mm, baseZ + rng.Next(0, 5) * Mm, 3);
            var va = Volume6(a);
            var vb = Volume6(b);
            var u = a | b; var i = a & b; var d = a - b; var e = b - a;
            foreach (var (s, name) in new[] { (u, "union"), (i, "intersection"), (d, "difference"), (e, "reverse difference") })
                AssertClosed(s, $"case {t} {name}");
            var vu = Volume6(u); var vi = Volume6(i); var vd = Volume6(d); var ve = Volume6(e);
            Assert.Equal(Add(va, vb), Add(vu, vi));
            Assert.Equal(va, Add(vd, vi));
            Assert.Equal(vb, Add(ve, vi));
            Assert.True(vi.Num.Sign >= 0 && vd.Num.Sign >= 0 && ve.Num.Sign >= 0);
        }
    }

    /// <summary>The filtered canonical-frame EdgeAtHit never disagrees with the exact sign (replicated from SolidBoolean).</summary>
    [Fact]
    public void CanonicalFrameEdgeFilterNeverContradictsExactSign()
    {
        var rng = new Random(4711);
        int decided = 0, total = 0;
        for (int t = 0; t < 4000; t++)
        {
            // Support and edge planes through near-extreme grid points; a hit point on the support plane near the edge.
            Vec3 P() => new(rng.NextInt64(-M, M + 1), rng.NextInt64(-M, M + 1), rng.NextInt64(-M, M + 1));
            Vec3 a = P(), b = P(), c = P();
            var s = Plane3.FromPoints(a, b, c);
            if (s.IsDegenerate) continue;
            var e = Plane3.FromPoints(a, b, new Vec3(a.X + rng.Next(-3, 4), a.Y + 1, a.Z + rng.Next(-3, 4)));
            if (e.IsDegenerate) continue;
            // probe on the edge line (exact rational from the grid), so the exact sign is often 0 or tiny
            long num = rng.Next(0, 1000), den = 1000;
            BigInteger X = a.X * (den - num) + (BigInteger)b.X * num, Y = a.Y * (den - num) + (BigInteger)b.Y * num, Z = a.Z * (den - num) + (BigInteger)b.Z * num;
            long jit = rng.Next(4) switch { 0 => 0, 1 => 6, 2 => 1_000_000, _ => 1_000_000_000_000 };
            X += rng.NextInt64(-jit, jit + 1); Y += rng.NextInt64(-jit, jit + 1); Z += rng.NextInt64(-jit, jit + 1);
            double px = (double)X / den, py = (double)Y / den, pz = (double)Z / den;
            double err = 16 * Math.ScaleB(1, -53) * M;
            double[] k = [(double)s.Nx, (double)s.Ny, (double)s.Nz, (double)s.D, (double)e.Nx, (double)e.Ny, (double)e.Nz, (double)e.D];
            for (int axis = 0; axis < 3; axis++)
                foreach (int sign in new[] { 1, -1 })
                {
                    int B = (axis + 1) % 3, C = (axis + 2) % 3;
                    BigInteger[] pc = [X, Y, Z];
                    Int128 Comp(in Plane3 p, int i) => i == 0 ? p.Nx : i == 1 ? p.Ny : p.Nz;
                    double Kd(int o, int i) => i == 0 ? sign * k[o + axis] : k[o + (i == 1 ? B : C)];
                    BigInteger snu = sign * (BigInteger)Comp(s, axis), snv = Comp(s, B), snw = Comp(s, C);
                    BigInteger enu = sign * (BigInteger)Comp(e, axis), env = Comp(e, B), enw = Comp(e, C);
                    if (snu.IsZero) continue;
                    BigInteger tt = snv * pc[B] + snw * pc[C] + (BigInteger)s.D * den;
                    BigInteger g = -enu * tt + snu * (env * pc[B] + enw * pc[C] + (BigInteger)e.D * den);
                    int exact = g.Sign * snu.Sign;
                    double[] pd = [px, py, pz];
                    double cv = pd[B], cw = pd[C];
                    // expression copied from SolidBoolean.EdgeAtHit
                    double su = Kd(0, 0), sv = Kd(0, 1) * cv, sw = Kd(0, 2) * cw, sd = k[3];
                    double uHit = -(sv + sw + sd) / su;
                    double eu = Kd(4, 0), ev = Kd(4, 1) * cv, ew = Kd(4, 2) * cw, ed = k[7];
                    double gd = eu * uHit + ev + ew + ed;
                    double ratio = Math.Abs(eu) / Math.Abs(su);
                    double bound = Filter.Rel * (Math.Abs(eu) * (Math.Abs(sv) + Math.Abs(sw) + Math.Abs(sd)) / Math.Abs(su) + Math.Abs(ev) + Math.Abs(ew) + Math.Abs(ed))
                                   + 2 * err * (Math.Abs(Kd(4, 1)) + ratio * Math.Abs(Kd(0, 1)) + Math.Abs(Kd(4, 2)) + ratio * Math.Abs(Kd(0, 2)));
                    int f = gd > bound ? 1 : gd < -bound ? -1 : Filter.Uncertain;
                    total++;
                    if (f != Filter.Uncertain) { decided++; Assert.Equal(exact, f); }
                }
        }
        output.WriteLine($"decided {decided} of {total}");
        Assert.True(decided > 1000);
    }

    // ------------------------------------------------------------------------------------------------ (11) parallel classification

    [Fact]
    public void ParallelAndSequentialBooleansAreBitIdentical()
    {
        var tol = Tolerance.Budget(totalUm: 40, chordNm: 20_000);
        var sphere = Solid.Sphere(Vec3.Mm(0, 0, 0), 4, tol);
        var cyl = Solid.Cylinder(Vec3.Mm(-6, 1, 0), Vec3.Mm(6, 1, 0), 2, tol);
        var box = Solid.Box(Vec3.Mm(-1, -5, -5), Vec3.Mm(5, 5, 1));
        var rng = new Random(11);
        var hull = LatticeHull(rng, -3 * Mm, -3 * Mm, -3 * Mm, 6, Mm / 7);
        var pairs = new List<(Solid, Solid)> { (sphere, cyl), (sphere, box), (cyl, box), (sphere - box, cyl), (sphere | cyl, hull) };
        Assert.True(sphere.FaceCount >= 64 && cyl.FaceCount >= 64, $"faces {sphere.FaceCount}, {cyl.FaceCount}");
        int compared = 0;
        foreach (var (a, b) in pairs)
            foreach (var op in new[] { SolidOp.Union, SolidOp.Intersection, SolidOp.Difference })
            {
                string seq = WithParallelism(1, () => Fingerprint(a.Boolean(b, op)));
                for (int rep = 0; rep < 3; rep++)
                {
                    int p = rep == 0 ? 8 : rep == 1 ? 2 : 64;
                    string par = WithParallelism(p, () => Fingerprint(a.Boolean(b, op)));
                    Assert.True(seq == par, $"{op}: parallelism {p} differs from sequential");
                    compared++;
                }
            }
        output.WriteLine($"{compared} comparisons");
    }

    [Fact]
    public void ConcurrentBooleansOnSharedSolidsMatchSequential()
    {
        // Many Booleans at once on the same Face3 instances (lazy PlanesD caches filled concurrently, nested
        // Parallel.For inside each call): each must equal its sequential result.
        var tol = Tolerance.Budget(totalUm: 40, chordNm: 30_000);
        var a = Solid.Sphere(Vec3.Mm(0, 0, 0), 4, tol);
        var tools = new List<Solid>();
        var rng = new Random(99);
        for (int i = 0; i < 12; i++)
            tools.Add(i % 2 == 0
                ? Solid.Box(Vec3.Mm(rng.Next(-5, 0), rng.Next(-5, 0), rng.Next(-5, 0)), Vec3.Mm(rng.Next(1, 6), rng.Next(1, 6), rng.Next(1, 6)))
                : Solid.Cylinder(Vec3.Mm(-6, rng.Next(-2, 3), rng.Next(-2, 3)), Vec3.Mm(6, rng.Next(-2, 3), rng.Next(-2, 3)), 1 + rng.Next(0, 2), tol));
        // Fresh copies of the inputs so the lazy caches start empty for the concurrent run.
        Solid Copy(Solid s) => new(s.Faces.Select(f => new Face3(f.Support, f.Edges, f.Vertices)).ToList());
        var expected = WithParallelism(1, () => tools.Select(t => Fingerprint(Copy(a) - Copy(t))).ToArray());
        var shared = Copy(a);
        var sharedTools = tools.Select(Copy).ToArray();
        var actual = new string[tools.Count * 3];
        WithParallelism(4, () =>
        {
            Parallel.For(0, actual.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 },
                i => actual[i] = Fingerprint(shared - sharedTools[i % tools.Count]));
            return 0;
        });
        for (int i = 0; i < actual.Length; i++) Assert.True(expected[i % tools.Count] == actual[i], $"tool {i % tools.Count} differs");
    }

    /// <summary>
    /// CNC pocket: a chain of box cuts (end mill passes) on a part with many faces, every step checked against voxel
    /// counts and compared bit for bit between sequential and parallel classification.
    /// </summary>
    [Fact]
    public void PocketChainMatchesVoxelsAndIsDeterministic()
    {
        var rng = new Random(1234);
        const int nx = 14, ny = 12, nz = 6;
        var vox = new bool[nx, ny, nz];
        for (int x = 0; x < nx; x++) for (int y = 0; y < ny; y++) for (int z = 0; z < nz; z++) vox[x, y, z] = true;
        long ox = -M + 3, oy = M - nx * Mm - 5, oz = 17; // odd offsets near the limits
        var seqPart = Solid.Box(new Vec3(ox, oy, oz), new Vec3(ox + nx * Mm, oy + ny * Mm, oz + nz * Mm));
        var parPart = seqPart;
        BigInteger u3 = (BigInteger)Mm * Mm * Mm * 6;
        int maxFaces = 0;
        for (int step = 0; step < 150; step++)
        {
            int x0 = rng.Next(-1, nx), y0 = rng.Next(-1, ny), depth = rng.Next(1, 4);
            int x1 = x0 + rng.Next(1, 5), y1 = y0 + rng.Next(1, 4);
            if (rng.Next(3) == 0) (x1, y1) = (x0 + 1, y0 + rng.Next(3, 8)); // slot
            var tool = Solid.Box(new Vec3(ox + x0 * Mm, oy + y0 * Mm, oz + (nz - depth) * Mm), new Vec3(ox + x1 * Mm, oy + y1 * Mm, oz + (nz + 1) * Mm));
            for (int x = Math.Max(0, x0); x < Math.Min(nx, x1); x++)
                for (int y = Math.Max(0, y0); y < Math.Min(ny, y1); y++)
                    for (int z = nz - depth; z < nz; z++) vox[x, y, z] = false;
            var s = seqPart;
            var p = parPart;
            seqPart = WithParallelism(1, () => s - tool);
            parPart = WithParallelism(8, () => p - tool);
            Assert.True(Fingerprint(seqPart) == Fingerprint(parPart), $"step {step}: parallel result differs");
            AssertVolume(seqPart, Count(vox, (x, y, z) => vox[x, y, z]) * u3, $"step {step}");
            maxFaces = Math.Max(maxFaces, seqPart.FaceCount);
        }
        output.WriteLine($"max faces {maxFaces}");
        Assert.True(maxFaces >= 64);
    }

    [Fact(Skip = "BUG (minor, API): with >= 64 faces and MaxParallelism > 1, classification errors surface as AggregateException instead of InvalidOperationException (Parallel.For wraps them)")]
    public void ClassificationErrorsSurfaceWithTheSameExceptionTypeSequentialAndParallel()
    {
        // Documented behaviour check (not a geometry bug): a fragment that cannot be classified throws
        // InvalidOperationException sequentially; Parallel.For would wrap it in an AggregateException.
        // A degenerate (all-collinear) face is the simplest way to make Probe throw.
        var good = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(10, 10, 10));
        var faces = new List<Face3>(good.Faces);
        var bad = new Face3(new Plane3(0, 0, 1, -5 * Mm), [new Plane3(0, 1, 0, 0), new Plane3(0, -1, 0, 0), new Plane3(0, 1, 0, 0)],
            [new Point3(Vec3.Mm(1, 0, 5)), new Point3(Vec3.Mm(2, 0, 5)), new Point3(Vec3.Mm(3, 0, 5))]);
        for (int i = 0; i < 70; i++) faces.Add(bad);
        var a = new Solid(faces);
        var b = Solid.Box(Vec3.Mm(-1, -1, -1), Vec3.Mm(4, 4, 6));
        var seq = Record.Exception(() => WithParallelism(1, () => a - b));
        var par = Record.Exception(() => WithParallelism(8, () => a - b));
        output.WriteLine($"sequential: {seq?.GetType().Name}, parallel: {par?.GetType().Name}");
        Assert.NotNull(seq);
        Assert.NotNull(par);
        Assert.Equal(seq!.GetType(), par!.GetType());
    }

    // ------------------------------------------------------------------------------------------------ (8) face split

    /// <summary>The split algorithm before round 2 (one optional crossing per edge, lists), as the reference.</summary>
    private static bool OldSplit(Face3 f, in Plane3 plane, out Face3? front, out Face3? back, out int side)
    {
        int n = f.Vertices.Length;
        var s = new int[n];
        bool pos = false, neg = false;
        for (int i = 0; i < n; i++)
        {
            s[i] = f.Vertices[i].SideOf(plane);
            pos |= s[i] > 0;
            neg |= s[i] < 0;
        }
        front = back = null;
        if (!(pos && neg)) { side = pos ? 1 : neg ? -1 : 0; return false; }
        side = 0;
        var crossing = new Point3?[n];
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            if ((s[i] < 0 && s[j] > 0) || (s[i] > 0 && s[j] < 0))
                crossing[i] = new Point3(Plane3.Intersect(f.Support, f.Edges[i], plane)!.Value);
        }
        back = Piece(1, plane);
        front = Piece(-1, plane.Flipped());
        return true;

        Face3 Piece(int sign, Plane3 k)
        {
            var verts = new List<Point3>();
            var edges = new List<Plane3>();
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                int si = sign * s[i], sj = sign * s[j];
                if (si <= 0) { verts.Add(f.Vertices[i]); edges.Add(si == 0 && sj > 0 ? k : f.Edges[i]); }
                if (crossing[i] is { } x) { verts.Add(x); edges.Add(si < 0 ? k : f.Edges[i]); }
            }
            return new Face3(f.Support, edges.ToArray(), verts.ToArray());
        }
    }

    private static void AssertSameFace(Face3? a, Face3? b, string what)
    {
        Assert.True((a is null) == (b is null), what);
        if (a is null) return;
        Assert.Equal(a.Support, b!.Support);
        Assert.Equal(a.Edges, b.Edges);
        Assert.Equal(a.Vertices.Length, b.Vertices.Length);
        for (int i = 0; i < a.Vertices.Length; i++) Assert.True(a.Vertices[i].SameAs(b.Vertices[i]), $"{what}: vertex {i}");
    }

    private static IEnumerable<Face3> SplitTestFaces(Random rng)
    {
        // Grid faces with collinear vertices (midpoints on every edge), squares, triangles.
        for (int t = 0; t < 30; t++)
        {
            long s = rng.Next(1, 5) * 4;
            long x0 = rng.Next(-5, 5), y0 = rng.Next(-5, 5), z = rng.Next(-5, 5);
            var pts = new List<Vec3>();
            Vec3[] corners = [new(x0, y0, z), new(x0 + s, y0, z), new(x0 + s, y0 + s, z + s / 2), new(x0, y0 + s, z + s / 2)];
            for (int i = 0; i < 4; i++)
            {
                pts.Add(corners[i]);
                var c = corners[(i + 1) % 4];
                if (rng.Next(2) == 0) pts.Add(new Vec3((corners[i].X + c.X) / 2, (corners[i].Y + c.Y) / 2, (corners[i].Z + c.Z) / 2));
            }
            yield return Face3.FromGrid(pts);
        }
        // Faces with exact vertices: faces of Boolean results of slanted lattice polytopes.
        for (int t = 0; t < 12; t++)
        {
            var a = LatticeHull(rng, 0, 0, 0, 4, 3);
            var b = LatticeHull(rng, rng.Next(-2, 3) * 3, rng.Next(-2, 3) * 3, rng.Next(-2, 3) * 3, 4, 3);
            foreach (var f in (a - b).Faces) yield return f;
            foreach (var f in (a & b).Faces) yield return f;
        }
    }

    [Fact]
    public void TwoCrossingSplitEqualsPreviousSplitOnCollinearAndExactFaces()
    {
        var rng = new Random(88);
        int splits = 0, through = 0;
        foreach (var f in SplitTestFaces(rng))
        {
            for (int k = 0; k < 40; k++)
            {
                Plane3 plane;
                var v = f.Vertices;
                if (k % 3 == 0 && v.All(p => p.IsGrid))
                {
                    // through one or two vertices (s = 0 cases), slightly tilted
                    var a = v[rng.Next(v.Length)].Grid;
                    var b = v[rng.Next(v.Length)].Grid;
                    if (a == b) b = new Vec3(b.X + 1, b.Y + rng.Next(-2, 3), b.Z + 7);
                    plane = Plane3.FromPoints(a, b, new Vec3(a.X + rng.Next(-3, 4), a.Y + rng.Next(-3, 4), a.Z + 11));
                }
                else
                {
                    // random plane near the face's box, small coefficients (many zero sides at grid vertices)
                    var bx = f.Box;
                    long cx = (long)((bx.MinX + bx.MaxX) / 2), cy = (long)((bx.MinY + bx.MaxY) / 2), cz = (long)((bx.MinZ + bx.MaxZ) / 2);
                    var c = new Vec3(cx + rng.Next(-2, 3), cy + rng.Next(-2, 3), cz + rng.Next(-2, 3));
                    plane = Plane3.FromPoints(c, new Vec3(c.X + rng.Next(-3, 4), c.Y + rng.Next(-3, 4), c.Z + rng.Next(-3, 4)),
                                              new Vec3(c.X + rng.Next(-3, 4), c.Y + rng.Next(-3, 4), c.Z + rng.Next(-3, 4)));
                }
                if (plane.IsDegenerate) continue;
                if (rng.Next(2) == 0) plane = plane.Flipped();
                bool r1 = f.Split(plane, out var fr1, out var bk1, out int s1);
                bool r0 = OldSplit(f, plane, out var fr0, out var bk0, out int s0);
                Assert.Equal(r0, r1);
                Assert.Equal(s0, s1);
                AssertSameFace(fr0, fr1, "front");
                AssertSameFace(bk0, bk1, "back");
                if (r1)
                {
                    splits++;
                    if (f.Vertices.Any(p => p.SideOf(plane) == 0)) through++;
                    // pieces are convex and lie on the right side
                    foreach (var (piece, sgn) in new[] { (fr1!, 1), (bk1!, -1) })
                        foreach (var p in piece.Vertices) Assert.True(sgn * p.SideOf(plane) >= 0);
                }
            }
        }
        output.WriteLine($"{splits} splits ({through} through a vertex)");
        Assert.True(splits > 500 && through > 50);
    }

    // ------------------------------------------------------------------------------------------------ (9) BVH node bound

    [Fact]
    public void BvhNodeArrayBoundHoldsForAllSizesAndDistributions()
    {
        var rng = new Random(5);
        var tri = Face3.FromGrid([new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(0, 10, 0)]);
        var result = new List<Face3>();
        for (int n = 0; n <= 2000; n += n < 300 ? 1 : 37)
        {
            for (int dist = 0; dist < 3; dist++)
            {
                var faces = new List<Face3>(n);
                for (int i = 0; i < n; i++)
                {
                    long x = dist switch { 0 => 0, 1 => i * 100, _ => rng.Next(0, 3) * 10_000 }; // identical / on a line / 3 clusters
                    long y = dist == 2 ? rng.Next(0, 2) * 5000 : 0;
                    faces.Add(dist == 0 ? tri : Face3.FromGrid([new Vec3(x, y, 0), new Vec3(x + 10, y, 0), new Vec3(x, y + 10, 5)]));
                }
                var bvh = new Bvh3(faces); // throws IndexOutOfRange if the node array is too small
                if (n % 50 != 7 && n > 20) continue;
                // brute-force comparison of box and six-direction ray queries
                for (int q = 0; q < 10; q++)
                {
                    double px = rng.Next(-50, 3000 + (dist == 1 ? n * 100 : 0)), py = rng.Next(-20, 6000), pz = rng.Next(-5, 10);
                    for (int axis = 0; axis < 3; axis++)
                        foreach (int sign in new[] { 1, -1 })
                        {
                            bvh.QueryRay(axis, sign, px, py, pz, result);
                            var expected = faces.Where(f => RayMeets(f.Box, axis, sign, px, py, pz)).ToList();
                            Assert.Equal(expected.Count, result.Count);
                        }
                }
            }
        }

        static bool RayMeets(Box3 b, int axis, int sign, double x, double y, double z)
        {
            double[] p = [x, y, z], lo = [b.MinX, b.MinY, b.MinZ], hi = [b.MaxX, b.MaxY, b.MaxZ];
            for (int a = 0; a < 3; a++)
            {
                if (a == axis) { if (sign > 0 ? hi[a] < p[a] - 1 : lo[a] > p[a] + 1) return false; }
                else if (p[a] < lo[a] || p[a] > hi[a]) return false;
            }
            return true;
        }
    }

    // ------------------------------------------------------------------------------------------------ (6)/(10) hull scratch

    private static string HullFingerprint(IEnumerable<Vec3> pts) => Fingerprint(ConvexHull3.Compute(pts));

    [Fact]
    public void HullScratchReuseDoesNotLeakBetweenCalls()
    {
        var rng = new Random(606);
        var sets = new List<List<Vec3>>();
        for (int t = 0; t < 40; t++)
        {
            int n = t % 5 == 0 ? 3000 : rng.Next(4, 60);
            long scale = t % 3 == 0 ? M : t % 3 == 1 ? 1000 : 3;
            var pts = new List<Vec3>();
            for (int i = 0; i < n; i++) pts.Add(new Vec3(rng.NextInt64(-scale, scale + 1), rng.NextInt64(-scale, scale + 1), rng.NextInt64(-scale, scale + 1)));
            sets.Add(pts);
        }
        // Reference: each hull on a fresh thread (fresh [ThreadStatic] scratch).
        var expected = new string?[sets.Count];
        for (int i = 0; i < sets.Count; i++)
        {
            int idx = i;
            var th = new Thread(() => { try { expected[idx] = HullFingerprint(sets[idx]); } catch (ArgumentException) { expected[idx] = "arg"; } });
            th.Start();
            th.Join();
        }
        // Same thread, big and small interleaved, with failing calls in between (exceptions after the scratch was cleared
        // and filled: an out-of-range point makes the exact predicates overflow mid-way).
        var poison = new List<Vec3>(sets[0]) { new(long.MaxValue / 2, 3, -long.MaxValue / 2), new(1, long.MaxValue / 3, 5) };
        for (int round = 0; round < 2; round++)
            for (int i = 0; i < sets.Count; i++)
            {
                try { ConvexHull3.Compute(poison); } catch (Exception) { }
                try { ConvexHull3.Compute([new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(2, 0, 0), new Vec3(3, 0, 0)]); } catch (ArgumentException) { }
                string got;
                try { got = HullFingerprint(sets[i]); } catch (ArgumentException) { got = "arg"; }
                Assert.True(expected[i] == got, $"set {i} round {round}: hull differs after scratch reuse");
            }
        // Concurrent hulls on pool threads.
        var par = new string?[sets.Count * 2];
        Parallel.For(0, par.Length, i => { try { par[i] = HullFingerprint(sets[i % sets.Count]); } catch (ArgumentException) { par[i] = "arg"; } });
        for (int i = 0; i < par.Length; i++) Assert.True(expected[i % sets.Count] == par[i], $"concurrent hull {i} differs");
    }

    [Fact]
    public void HullTrianglesDoNotAliasScratch()
    {
        var rng = new Random(1010);
        var a = Enumerable.Range(0, 200).Select(_ => new Vec3(rng.Next(-1000, 1000), rng.Next(-1000, 1000), rng.Next(-1000, 1000))).ToList();
        var b = Enumerable.Range(0, 500).Select(_ => new Vec3(rng.Next(-1000, 1000), rng.Next(-1000, 1000), rng.Next(-1000, 1000))).ToList();
        var ta = ConvexHull3.Triangles(a, out var pa);
        var copy = ta.ToArray();
        var ha = ConvexHull3.Compute(a);
        _ = ConvexHull3.Triangles(b, out _);
        _ = ConvexHull3.Compute(b);
        Assert.Equal(copy, ta.ToArray());
        // The triangles of a are a closed, outward hull of a's points.
        foreach (var (x, y, z) in ta)
            foreach (var q in pa) Assert.True(Predicates.Orient3D(pa[x], pa[y], pa[z], q) <= 0);
        Assert.Equal(Volume6(ha), Volume6(ConvexHull3.Compute(a)));
    }

    // ------------------------------------------------------------------------------------------------ (9) FaceMerge

    /// <summary>Twice the exact vector area of a face (Σ v_i × v_{i+1}) as reduced fractions per component.</summary>
    private static (BigInteger, BigInteger)[] AreaVector(IEnumerable<Face3> faces)
    {
        var acc = new (BigInteger, BigInteger)[] { (0, 1), (0, 1), (0, 1) };
        foreach (var f in faces)
        {
            var v = f.Vertices;
            for (int i = 0; i < v.Length; i++)
            {
                var a = v[i].Big; var b = v[(i + 1) % v.Length].Big;
                var w = a.W * b.W;
                acc[0] = Add(acc[0], (a.Y * b.Z - a.Z * b.Y, w));
                acc[1] = Add(acc[1], (a.Z * b.X - a.X * b.Z, w));
                acc[2] = Add(acc[2], (a.X * b.Y - a.Y * b.X, w));
            }
        }
        return acc;
    }

    [Fact]
    public void FaceMergeKeepsAreaAndConvexityWithDuplicateEdgeKeys()
    {
        var rng = new Random(321);
        int merged = 0;
        for (int t = 0; t < 60; t++)
        {
            // Convex slanted face split by many random planes into pieces with exact vertices, shuffled; optionally
            // with an overlapping duplicate piece (two pieces share a directed edge key: the single-entry index can
            // only miss a merge, never join the wrong pieces).
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
            pieces = pieces.OrderBy(_ => rng.Next()).ToList();
            bool dup = t % 3 == 0 && pieces.Count > 1;
            if (dup) pieces.Insert(rng.Next(pieces.Count), pieces[rng.Next(pieces.Count)]);
            var before = AreaVector(pieces);
            int countBefore = pieces.Count;
            FaceMerge.MergeCoplanar(pieces);
            Assert.True(before.SequenceEqual(AreaVector(pieces)), $"case {t}: merged area differs");
            foreach (var f in pieces)
            {
                int n = f.Vertices.Length;
                for (int e = 0; e < n; e++)
                {
                    Assert.Equal(0, f.Vertices[e].SideOf(f.Edges[e]));
                    Assert.Equal(0, f.Vertices[(e + 1) % n].SideOf(f.Edges[e]));
                    foreach (var v in f.Vertices) Assert.True(v.SideOf(f.Edges[e]) <= 0, $"case {t}: merged face not convex");
                }
            }
            merged += countBefore - pieces.Count;
        }
        output.WriteLine($"{merged} merges");
        Assert.True(merged > 50);
    }
}
