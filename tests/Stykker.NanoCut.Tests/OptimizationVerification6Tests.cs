using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Stykker.NanoCut.Geometry3D;
#if !REF_BUILD
using Xunit.Abstractions;
#endif

namespace Stykker.NanoCut.Tests;

/// <summary>
/// Deterministic workload aimed at the per-fragment split skip of round 6 (8506ce4): coplanar overlaps (voxel unions,
/// lattice translates), faces touching along edges / at vertices, slivers, and cut chains near ±2^31 whose later tools
/// reuse earlier cut planes. Compiled unchanged against the pre-round reference b9a5faf (REF_BUILD) to produce
/// <see cref="OptimizationVerification6Tests.ReferenceDigests"/>; only APIs present in both versions are used.
/// The digest is geometric and independent of the face decomposition: exact volume and exact first moments
/// (∫x, ∫y, ∫z) about two reference points – a different point set with equal volume almost never matches.
/// </summary>
public static class Rev6Workload
{
    private const long M = Units.MaxCoordinate;
    private const long Mm = 1_000_000;

    /// <summary>Exact 24·(V·o-shifted first moments) and 6·V of a closed surface, as reduced rationals (text).</summary>
    public static string Moments(Solid s, long ox, long oy, long oz)
    {
        // Each fan triangle (a, b, c) with the reference point o spans a tetrahedron: 6V = det(a, b, c) and
        // 24·∫x = det · (a.x + b.x + c.x) (o shifted to the origin).
        var acc = new (BigInteger N, BigInteger D)[4];
        for (int i = 0; i < 4; i++) acc[i] = (0, 1);
        foreach (var f in s.Faces)
        {
            var v = f.Vertices;
            var a = Shift(v[0].Big);
            for (int i = 1; i + 1 < v.Length; i++)
            {
                var b = Shift(v[i].Big);
                var c = Shift(v[i + 1].Big);
                BigInteger det = a.X * (b.Y * c.Z - b.Z * c.Y) - a.Y * (b.X * c.Z - b.Z * c.X) + a.Z * (b.X * c.Y - b.Y * c.X);
                if (det.IsZero) continue;
                BigInteger w = a.W * b.W * c.W;
                Add(ref acc[0], det, w);
                BigInteger sx = a.X * b.W * c.W + b.X * a.W * c.W + c.X * a.W * b.W;
                BigInteger sy = a.Y * b.W * c.W + b.Y * a.W * c.W + c.Y * a.W * b.W;
                BigInteger sz = a.Z * b.W * c.W + b.Z * a.W * c.W + c.Z * a.W * b.W;
                Add(ref acc[1], det * sx, w * w);
                Add(ref acc[2], det * sy, w * w);
                Add(ref acc[3], det * sz, w * w);
            }
        }
        return string.Join(" ", acc.Select(r => $"{r.N}/{r.D}"));

        (BigInteger X, BigInteger Y, BigInteger Z, BigInteger W) Shift((BigInteger X, BigInteger Y, BigInteger Z, BigInteger W) p) =>
            (p.X - ox * p.W, p.Y - oy * p.W, p.Z - oz * p.W, p.W);

        static void Add(ref (BigInteger N, BigInteger D) r, BigInteger n, BigInteger d)
        {
            if (d.Sign < 0) { n = -n; d = -d; }
            var num = r.N * d + n * r.D;
            var den = r.D * d;
            if (num.IsZero) { r = (0, 1); return; }
            var g = BigInteger.GreatestCommonDivisor(num, den);
            r = (num / g, den / g);
        }
    }

    public static string Digest(Solid s)
    {
        var text = Moments(s, 0, 0, 0) + "|" + Moments(s, 7_000_001, -3_000_017, 11_000_003);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    internal static Solid FromVoxels(bool[,,] vox, long unit, Vec3 o)
    {
        var parts = new List<Solid>();
        int nx = vox.GetLength(0), ny = vox.GetLength(1), nz = vox.GetLength(2);
        for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    if (!vox[x, y, z]) continue;
                    int x1 = x;
                    while (x1 + 1 < nx && vox[x1 + 1, y, z]) x1++;
                    parts.Add(Solid.Box(new Vec3(o.X + x * unit, o.Y + y * unit, o.Z + z * unit),
                                        new Vec3(o.X + (x1 + 1) * unit, o.Y + (y + 1) * unit, o.Z + (z + 1) * unit)));
                    x = x1;
                }
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

    internal static bool[,,] RandomVoxels(Random rng, int n, double density)
    {
        var v = new bool[n, n, n];
        for (int x = 0; x < n; x++) for (int y = 0; y < n; y++) for (int z = 0; z < n; z++) v[x, y, z] = rng.NextDouble() < density;
        return v;
    }

    internal static List<Vec3> LatticePoints(Random rng, int size, int count)
    {
        var pts = new List<Vec3>();
        for (int i = 0; i < count; i++) pts.Add(new Vec3(rng.Next(0, size + 1), rng.Next(0, size + 1), rng.Next(0, size + 1)));
        return pts;
    }

    /// <summary>Hull of lattice points (index space) scaled by unit and placed at o; null if flat.</summary>
    internal static Solid? Hull(List<Vec3> idx, long unit, Vec3 o)
    {
        try { return ConvexHull3.Compute(idx.Select(p => new Vec3(o.X + p.X * unit, o.Y + p.Y * unit, o.Z + p.Z * unit))); }
        catch (ArgumentException) { return null; }
    }

    internal static Solid Tet(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        int o = Predicates.Orient3D(a, b, c, d);
        if (o == 0) throw new ArgumentException("flat tetrahedron");
        if (o > 0) (b, c) = (c, b);
        return Solid.FromTriangles([a, b, c, d], [0, 1, 2, 0, 3, 1, 1, 3, 2, 2, 3, 0]);
    }

    internal static Solid Box(Vec3 o, long x0, long y0, long z0, long x1, long y1, long z1) =>
        Solid.Box(new Vec3(o.X + x0, o.Y + y0, o.Z + z0), new Vec3(o.X + x1, o.Y + y1, o.Z + z1));

    internal static readonly Vec3[] Origins =
        [new(0, 0, 0), new(M - 8 * Mm, M - 8 * Mm, M - 8 * Mm), new(-M, -M, -M), new(-M, M - 8 * Mm, -M + 7)];

    /// <summary>Runs the workload; calls <paramref name="sink"/> with every (name, result).</summary>
    public static void Run(Action<string, Solid> sink)
    {
        // 1. Voxel solids against shifted voxel solids: coplanar overlaps (OnSame and OnOpposite), faces meeting along
        //    edges and at vertices, at the origin and near the range corners.
        var rng = new Random(6001);
        for (int t = 0; t < 16; t++)
        {
            var o = Origins[t % Origins.Length];
            var a = FromVoxels(RandomVoxels(rng, 4, 0.45), Mm, o);
            var b = FromVoxels(RandomVoxels(rng, 4, 0.45), Mm, new Vec3(o.X + rng.Next(-1, 2) * Mm, o.Y + rng.Next(-1, 2) * Mm, o.Z + rng.Next(0, 2) * Mm));
            sink($"vox{t}a", a);
            sink($"vox{t}u", a | b);
            sink($"vox{t}i", a & b);
            sink($"vox{t}d", a - b);
            sink($"vox{t}e", b - a);
        }

        // 2. Lattice hulls against lattice translates of themselves and of other lattice hulls: coplanar faces of
        //    opposite and same orientation, shared edges, single shared vertices.
        for (int t = 0; t < 120; t++)
        {
            var o = Origins[t % Origins.Length];
            var pa = LatticePoints(rng, 2, rng.Next(4, 9));
            var a = Hull(pa, Mm, o);
            if (a is null) continue;
            var sh = new Vec3(rng.Next(-2, 3) * Mm, rng.Next(-2, 3) * Mm, rng.Next(0, 3) * Mm);
            var b = t % 2 == 0 ? Hull(pa, Mm, new Vec3(o.X + sh.X, o.Y + sh.Y, o.Z + sh.Z))
                               : Hull(LatticePoints(rng, 2, rng.Next(4, 9)), Mm, new Vec3(o.X + sh.X, o.Y + sh.Y, o.Z + sh.Z));
            if (b is null) continue;
            sink($"lt{t}u", a | b);
            sink($"lt{t}i", a & b);
            sink($"lt{t}d", a - b);
            sink($"lt{t}e", b - a);
        }

        // 3. Hand-made touching configurations: face-partial, edge-only, vertex-only, coplanar strips, slanted faces
        //    sharing an edge line with an axis face.
        for (int oi = 0; oi < Origins.Length; oi++)
        {
            var o = Origins[oi];
            var big = Box(o, 0, 0, 0, 4 * Mm, 4 * Mm, 2 * Mm);
            Solid[] tools =
            [
                Box(o, Mm, Mm, 2 * Mm, 3 * Mm, 3 * Mm, 3 * Mm),           // on top, partial face contact
                Box(o, 4 * Mm, 4 * Mm, 0, 5 * Mm, 5 * Mm, 2 * Mm),        // vertical edge contact
                Box(o, 4 * Mm, 4 * Mm, 2 * Mm, 5 * Mm, 5 * Mm, 3 * Mm),   // vertex contact
                Box(o, 4 * Mm, Mm, 2 * Mm, 5 * Mm, 3 * Mm, 3 * Mm),       // horizontal edge contact (partial)
                Box(o, Mm, -Mm, Mm, 3 * Mm, 5 * Mm, 2 * Mm),              // slab sharing the top plane, crossing
                Box(o, -Mm, Mm, 0, 5 * Mm, 3 * Mm, 2 * Mm),               // same height: coplanar top and bottom
                Box(o, 2 * Mm, 2 * Mm, Mm, 6 * Mm, 6 * Mm, 3 * Mm),       // corner overlap
                Tet(new Vec3(o.X + 4 * Mm, o.Y, o.Z + 2 * Mm), new Vec3(o.X + 4 * Mm, o.Y + 4 * Mm, o.Z + 2 * Mm),
                    new Vec3(o.X + 6 * Mm, o.Y + 2 * Mm, o.Z + 2 * Mm), new Vec3(o.X + 5 * Mm, o.Y + 2 * Mm, o.Z + 4 * Mm)), // edge on edge
                Tet(new Vec3(o.X + 2 * Mm, o.Y + 2 * Mm, o.Z + 2 * Mm), new Vec3(o.X + 3 * Mm, o.Y + 2 * Mm, o.Z + 3 * Mm),
                    new Vec3(o.X + 2 * Mm, o.Y + 3 * Mm, o.Z + 3 * Mm), new Vec3(o.X + Mm, o.Y + Mm, o.Z + 3 * Mm)),         // vertex on face
                Tet(new Vec3(o.X, o.Y, o.Z + 2 * Mm), new Vec3(o.X + 4 * Mm, o.Y + 4 * Mm, o.Z + 2 * Mm),
                    new Vec3(o.X + 4 * Mm, o.Y, o.Z + 2 * Mm), new Vec3(o.X + 2 * Mm, o.Y + Mm, o.Z + Mm)),                  // coplanar triangle, digs in
                Tet(new Vec3(o.X, o.Y, o.Z + 2 * Mm), new Vec3(o.X + 4 * Mm, o.Y + 4 * Mm, o.Z + 2 * Mm),
                    new Vec3(o.X + 4 * Mm, o.Y, o.Z + 2 * Mm), new Vec3(o.X + 2 * Mm, o.Y + Mm, o.Z + 3 * Mm)),              // coplanar triangle, outside
                Tet(new Vec3(o.X + Mm, o.Y + Mm, o.Z + 2 * Mm + 1), new Vec3(o.X + 3 * Mm, o.Y + Mm, o.Z + 2 * Mm),
                    new Vec3(o.X + Mm, o.Y + 3 * Mm, o.Z + 2 * Mm - 1), new Vec3(o.X + 2 * Mm, o.Y + 2 * Mm, o.Z + 2 * Mm + 3)), // sliver
            ];
            for (int k = 0; k < tools.Length; k++)
            {
                sink($"touch{oi}.{k}u", big | tools[k]);
                sink($"touch{oi}.{k}i", big & tools[k]);
                sink($"touch{oi}.{k}d", big - tools[k]);
                sink($"touch{oi}.{k}e", tools[k] - big);
            }
            // Chained: the result of the previous op meets the next tool along the planes it was cut by.
            var acc = big;
            for (int k = 0; k < tools.Length; k++)
            {
                acc = k % 3 == 2 ? acc | tools[k] : acc - tools[k];
                sink($"touch{oi}.chain{k}", acc);
            }
        }

        // 4. Cut chains near the range corners: tools on the same lattice (later cut faces coplanar with earlier ones),
        //    slanted lattice hulls (exact vertices) and odd offsets (slivers, exact non-grid vertices on cut faces).
        for (int chain = 0; chain < 16; chain++)
        {
            var o = Origins[chain % Origins.Length];
            var part = Box(o, 0, 0, 0, 6 * Mm, 6 * Mm, 4 * Mm);
            for (int step = 0; step < 10 && !part.IsEmpty; step++)
            {
                long unit = step % 3 == 2 ? Mm / 2 : Mm;
                long jx = chain % 2 == 1 && step % 4 == 3 ? 1 : 0;
                var tool = Hull(LatticePoints(rng, 3, rng.Next(4, 9)), unit,
                    new Vec3(o.X + rng.Next(-1, 5) * Mm + jx, o.Y + rng.Next(-1, 5) * Mm, o.Z + rng.Next(1, 4) * Mm - jx));
                if (tool is null) continue;
                var (rest, removed) = part.Split(tool);
                sink($"cc{chain}.{step}r", rest);
                sink($"cc{chain}.{step}m", removed);
                part = rest;
            }
            sink($"cc{chain}.final", part);
        }

        // 5. Many small coplanar tools on one large face (one face split by many coplanar edge planes, owners shared and
        //    distinct), then a slanted cut through all of them.
        for (int oi = 0; oi < 2; oi++)
        {
            var o = Origins[oi * 2];
            var plate = Box(o, 0, 0, 0, 8 * Mm, 8 * Mm, Mm);
            var studs = new List<Solid>();
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    if ((i + j + oi) % 3 != 0)
                        studs.Add(Box(o, 2 * i * Mm, 2 * j * Mm + (i % 2) * Mm / 2, Mm, (2 * i + 1) * Mm + (j == 2 ? Mm : 0), (2 * j + 1) * Mm, 2 * Mm));
            var withStuds = plate;
            foreach (var s in studs) withStuds |= s;
            sink($"plate{oi}", withStuds);
            var pocketPts = new List<Vec3> { new(1, 1, 0), new(7, 1, 0), new(1, 7, 0), new(6, 6, 0), new(1, 1, 3), new(7, 2, 3), new(2, 7, 3) };
            var pocket = Hull(pocketPts, Mm, new Vec3(o.X, o.Y, o.Z + Mm / 2))!;
            sink($"plate{oi}d", withStuds - pocket);
            sink($"plate{oi}i", withStuds & pocket);
            sink($"plate{oi}x", pocket - withStuds);
        }

        // 6. Faceted balls (hundreds of faces: parallel classification, deeper BVH) cut by lattice boxes and hulls that
        //    share planes with each other, near a range corner.
        for (int oi = 0; oi < 2; oi++)
        {
            var o = Origins[oi];
            var ball = Solid.Sphere(new Vec3(o.X + 4 * Mm, o.Y + 4 * Mm, o.Z + 4 * Mm), 3, Tolerance.Budget(totalUm: 1000, chordNm: 40_000));
            var cur = ball;
            for (int k = 0; k < 6; k++)
            {
                var t1 = Box(o, (1 + k % 3) * Mm, Mm, 2 * Mm, (4 + k % 2) * Mm, 7 * Mm, (3 + k % 2) * Mm);
                cur = k % 2 == 0 ? cur - t1 : cur | Box(o, 3 * Mm, 3 * Mm, 6 * Mm, 5 * Mm, 5 * Mm, 8 * Mm);
                sink($"ball{oi}.{k}", cur);
                var h = Hull(LatticePoints(rng, 3, 7), Mm, new Vec3(o.X + rng.Next(1, 4) * Mm, o.Y + rng.Next(1, 4) * Mm, o.Z + rng.Next(1, 4) * Mm));
                if (h is null) continue;
                cur -= h;
                sink($"ball{oi}.{k}h", cur);
            }
        }
    }
}

#if !REF_BUILD
/// <summary>
/// Adversarial verification of round 6: (1) the per-fragment split skip by the single owner face of a splitting
/// plane (8506ce4), (2) the quickselect median split of the BVH build (390ce72), and the geometric reference digest
/// of e2ace8b.
/// </summary>
public class OptimizationVerification6Tests(ITestOutputHelper output)
{
    private const long Mm = 1_000_000;
    private const long M = Units.MaxCoordinate;

    private static (BigInteger Num, BigInteger Den) Volume6(Solid s, long ox = 0, long oy = 0, long oz = 0) =>
        OptimizationVerificationTests.Volume6(s, ox, oy, oz);

    private static void AssertClosed(Solid s, string what)
    {
        Assert.True(Volume6(s) == Volume6(s, 7_000_001, -3_000_017, 11_000_003), $"{what}: surface not closed");
    }

    private static (BigInteger Num, BigInteger Den) Add((BigInteger Num, BigInteger Den) a, (BigInteger Num, BigInteger Den) b, int sign = 1)
    {
        var n = a.Num * b.Den + sign * b.Num * a.Den;
        var d = a.Den * b.Den;
        if (n.IsZero) return (0, 1);
        var g = BigInteger.GreatestCommonDivisor(n, d);
        return (n / g, d / g);
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

    // ------------------------------------------------------------------------------------------------ differential

    /// <summary>
    /// Digests (exact volume and first moments about two points) of every workload result, produced by the pre-round
    /// build b9a5faf. Moments pin down the point set far more tightly than the volume alone (e2ace8b): a result that
    /// is wrong by a region of zero net volume, or by two regions of equal volume, moves the centroid.
    /// </summary>
    internal const string ReferenceDigests = """
vox0a 74BBE24408E8B0E6
vox0u BB7E4D3D6A5F4183
vox0i 600A24E8846154B3
vox0d 8FC651625C62EF68
vox0e 2420B613C84BC3CF
vox1a 62528507795AE4E5
vox1u 81A9DF7EA5F7ECF4
vox1i 7EC7783DBF5CF5B9
vox1d C3A78A81A485A784
vox1e A0318E844AC83E25
vox2a 4A0077E05DD054B6
vox2u 5777E783779EF7B6
vox2i 9F697861D4DE9AAF
vox2d E81266209E25DA6D
vox2e 8C75B780AFD6AFCC
vox3a AAABCB2EE7BCF99B
vox3u 5619FB29F2294218
vox3i 16FD66E8C876D7A7
vox3d 51A36AC06D4570A3
vox3e 06E7FBB1DE9A882F
vox4a 7D25306701E6B9C5
vox4u 78798FC636FAE028
vox4i 37F3F9AA3B40A2ED
vox4d 68A52367F841AC2B
vox4e 66A44C477564540C
vox5a C2E8980D2A1FC6C5
vox5u FF275769555A310D
vox5i 8321252D544C0012
vox5d 114D3A93CA96830B
vox5e CAE436AD1E194C15
vox6a 93B92EDAED9A8C9E
vox6u AAAE77E33058432E
vox6i 99324F432E7535A3
vox6d 15380ABE596913C9
vox6e 3299A3A695E4E4A5
vox7a D7523A66F4AAC54D
vox7u 3051ED31982E0713
vox7i 2C6D994D692151AF
vox7d 6C0BD02FEB59C7E7
vox7e 714E20B1DDAAF80A
vox8a A1F81C202A25ABF1
vox8u B60A3D58DB9F369D
vox8i 87BD8C48B42D782C
vox8d 1E5360C5AD5FADE2
vox8e 56B52BFF7DAAE18F
vox9a 8EFA1CA6472F1019
vox9u 5B525A715BFF4E72
vox9i 6D8A59287671B6BD
vox9d F510AA37670FF165
vox9e 6AFB9D9E3A512846
vox10a 6F3DA7500BF3FAB7
vox10u 3CAD766D4F809F0D
vox10i A4F6C8F1F50F35F7
vox10d 310D9EBDEA76ACDB
vox10e 66F657BB2F8A381B
vox11a FAC0C9AC93719BFB
vox11u 1113F884CC11B2F5
vox11i A586F7D4F8B988F4
vox11d C79BD9CF1A27698E
vox11e E68867BDFD6FB67B
vox12a 2BA714C222FFEE97
vox12u E23A23DD9C880584
vox12i 459EB07F9E5D9FDC
vox12d EEB4A1C809885FEE
vox12e 59CDCE40D99963DB
vox13a B2008C77844396F0
vox13u 04A85D5612E8F39F
vox13i 11089164AE7912A9
vox13d A6A93BB06D697505
vox13e C4A2ECDB427088D8
vox14a B31A9E61A054DD0F
vox14u AB1F0F44265102C9
vox14i 46671196DC43D0A6
vox14d 34CEEDDE7E077B06
vox14e A992193E9BF9D0E0
vox15a E7444926AA26F7D6
vox15u C12C8C245BD29301
vox15i B68A500333BE2E8C
vox15d D81DE17D857B8FB5
vox15e 73915FC2D7605C43
lt0u 85CC6B424A3A1E71
lt0i E5C63610938D06CA
lt0d 3EBA475749D757A3
lt0e CE1003F579731C9C
lt2u AFDB06AB8A660EE6
lt2i E5C63610938D06CA
lt2d 4F6D4DF0238A9942
lt2e A42377B8FE4A8F5C
lt3u 3303DC12DBD9DB82
lt3i E5C63610938D06CA
lt3d 7B34AA9862E6B092
lt3e 680EDE6E5F8E99B5
lt4u F4C1B70003ECE727
lt4i E5C63610938D06CA
lt4d F6C795FF11C9D0CD
lt4e 0AD576929CD08FBE
lt5u 33463149B2979CCB
lt5i E5C63610938D06CA
lt5d D5DEEE2C2B42F48F
lt5e 47EC2FE0D89DE255
lt6u A7335E7C65820407
lt6i E5C63610938D06CA
lt6d 8285B89E61E3FA68
lt6e B92922744015FB7F
lt7u 9D7FD2A449FC99AC
lt7i E5C63610938D06CA
lt7d 28DA21FA1AF3C396
lt7e C58034C83C587F49
lt8u C8E41F0C074AA1C5
lt8i E5C63610938D06CA
lt8d 1B8BF9E254CD0234
lt8e 7DE5020896A37978
lt9u F501FE28A3EB6CC2
lt9i CE272EC830883AC7
lt9d D669515F50FC22E1
lt9e F1DC37CD8D781A1D
lt10u 998D185B47BEBAC8
lt10i FA1CB156C5300182
lt10d 8B734FEC8A3FEBAE
lt10e 13F8DC2160F7712D
lt11u 9B99448E9994BABC
lt11i E5C63610938D06CA
lt11d DF352E3CAB304055
lt11e BB85BA99CEA2B1CA
lt12u 0840AFFAB1506A4A
lt12i E5C63610938D06CA
lt12d 45F02528B4B0E976
lt12e 0C2B26D9072DD2C9
lt14u D72217FB758685AA
lt14i E5C63610938D06CA
lt14d 432A0843C44782A0
lt14e 297A2DDF705CC744
lt15u 3AF5D6B6535F8464
lt15i E5C63610938D06CA
lt15d 80277AA74F62116F
lt15e 263B4E10A4825597
lt16u 3E6D704CE8D7F0B2
lt16i E5C63610938D06CA
lt16d 5A96B39B9EC6F311
lt16e 1136ACE83FD6AA79
lt17u 9637C87CB39A27FA
lt17i E5C63610938D06CA
lt17d 45BD32913386051C
lt17e A7743CCBA98FBAD9
lt21u BE77CB472CCACA41
lt21i 5B1FB3DDBB45F1F9
lt21d 002D82C12C505694
lt21e C192987A71C0423F
lt22u 8203E7478D9BA873
lt22i E5C63610938D06CA
lt22d D88E7AB58B9AC8FD
lt22e 1EE29AC6F9FB2DF0
lt24u 294FF78F8B16B7C6
lt24i A0CA0937F7ED69AD
lt24d F6C81DE638742B81
lt24e 47DD904E21FBF18A
lt26u 66811BC58F6CF9B8
lt26i E5C63610938D06CA
lt26d 7933237581AECFB5
lt26e DCEC7BFA2211C3F9
lt28u ED225A1B9CF84515
lt28i E5C63610938D06CA
lt28d 2569E6D444392BC3
lt28e 6498A8606229A553
lt29u B71BB5015CFF071B
lt29i E5C63610938D06CA
lt29d 229899AC5ACC3D4B
lt29e 5E727C6F28CD1CAF
lt30u BCBB4D6D6C6EDA89
lt30i E5C63610938D06CA
lt30d EB73395CC36380F4
lt30e A118D98D613705E2
lt31u C977CA0D0EEBEEA8
lt31i E5C63610938D06CA
lt31d ABDCFD0E478B1722
lt31e 1D0F7120EA247083
lt32u 0B8F5F5F96106F3E
lt32i E5C63610938D06CA
lt32d 20C4791BC7B1ABB8
lt32e AF9BD451371A6834
lt34u 5C6EB70D92A74072
lt34i E5C63610938D06CA
lt34d 023B6EE9C49FAFBD
lt34e 4C16497F8294C724
lt35u 0E70BE7B1D2EB922
lt35i E5C63610938D06CA
lt35d 196F633B03AFE7FC
lt35e 55D8A0EC0B08EB7D
lt36u 7EFAC44B3BD68E8C
lt36i E5C63610938D06CA
lt36d 2C1F8AD00440CDF1
lt36e 5B2B3284E1831214
lt37u AFDB141C315200BA
lt37i E5C63610938D06CA
lt37d ACEB8687051A2AB8
lt37e 9C86AEB14FFBCA72
lt38u 2001C6FF4B022774
lt38i E5C63610938D06CA
lt38d DFE80AB475BBE512
lt38e 1D2AE13F8D5113C1
lt39u 74A30B0AD1E8899F
lt39i E5C63610938D06CA
lt39d A9CF45F6081CE0D9
lt39e 29C7F77B9454BD3C
lt40u BCD20D961E7EBA4D
lt40i E5C63610938D06CA
lt40d 866DBAD6477331DF
lt40e 0B87AC61C9CEE7CF
lt41u FB64E02B23938DED
lt41i E5C63610938D06CA
lt41d 03B584AE8160D852
lt41e EE398B4D9F25AFC5
lt42u 1D0B6B867777F7C3
lt42i E5C63610938D06CA
lt42d 858AF584BCCA6CC8
lt42e FE7A9ECA7A73663C
lt43u BA9CC1E3DF798B21
lt43i E5C63610938D06CA
lt43d CDC5C226F88AA2DF
lt43e 516DADE2D39FE208
lt44u BE0D107748141D04
lt44i B998081A6AC9FDD8
lt44d 74337DAFBB723F33
lt44e E912B1F106E60F3E
lt45u D4524BF675335202
lt45i E5C63610938D06CA
lt45d DF4302094998B92F
lt45e 26E35CB06790F7C6
lt46u BFB1ED056034603C
lt46i E5C63610938D06CA
lt46d 51E12785F15A708A
lt46e 7FCEF871F832B110
lt47u B92FCE3FF43DCF18
lt47i E5C63610938D06CA
lt47d B10B1FA57C2A34EE
lt47e 323D8498432F0D04
lt48u 5705F046F7263393
lt48i E5C63610938D06CA
lt48d CFC823A70DC116F8
lt48e 32824BBAFFB0FA9F
lt49u BCB894DCB2904A63
lt49i E5C63610938D06CA
lt49d 9E289C8E18C9EB8A
lt49e E7D8069EC070C809
lt50u 781C0CC0442F8455
lt50i E5C63610938D06CA
lt50d DDD536BA9898B280
lt50e 72772C13187FA33A
lt52u 131C6524D6CF905F
lt52i E5C63610938D06CA
lt52d 1DBDFAD24361BDCF
lt52e 2783636057D0F339
lt53u E4CD533579A3F603
lt53i E5C63610938D06CA
lt53d 175BA41BA7CB711A
lt53e D13AE9B673AA4853
lt54u EB0424A386538358
lt54i EB0424A386538358
lt54d E5C63610938D06CA
lt54e E5C63610938D06CA
lt56u CEBCBC0F6C45F544
lt56i E5C63610938D06CA
lt56d 08A5F1282F2078C4
lt56e EA57F72AF4B4BEB1
lt57u 5B519770FB0A8ACC
lt57i E5C63610938D06CA
lt57d 5DCD55533399E366
lt57e AEB59E41AC20D0D3
lt58u 96A9A5A49FD920B8
lt58i E5C63610938D06CA
lt58d 1F2CE0FF9FE327B7
lt58e F44383F2AB904778
lt59u 71FDD71415B23BEE
lt59i E5C63610938D06CA
lt59d 50B0F3FC391E7CD3
lt59e 6DF0EAB11C7B8134
lt60u D03C9845E8D4CC15
lt60i E5C63610938D06CA
lt60d DB83CDDE521F47CB
lt60e 0ED69AD2740654EC
lt61u 3CCC19BC68FEB50C
lt61i E5C63610938D06CA
lt61d 73B41410E0BBA092
lt61e CE2B4FA7408C7BF1
lt62u 653F4C378CCF2838
lt62i E5C63610938D06CA
lt62d DB05F8703FFCA9F5
lt62e DE28E157A30B7A53
lt63u E251A8510A801C5A
lt63i B81C2B0A7DEB29BF
lt63d CB434FA8C9DE1FAC
lt63e 1A2E3602DE794AA5
lt64u 3C3AC18DED623D84
lt64i E5C63610938D06CA
lt64d 8D350E04CEF229E7
lt64e 6F89E466AC9129FB
lt65u 4F23338A6E913F50
lt65i E5C63610938D06CA
lt65d E8EB18500D5D142F
lt65e D7D1386588B31A45
lt66u 50B9150F84D6276B
lt66i E5C63610938D06CA
lt66d 006FEBF2C0E7C4FB
lt66e 6E43AC5DA79AC1AD
lt67u CB556C3D8B86C9FD
lt67i E5C63610938D06CA
lt67d 6F54A74351B28188
lt67e 8BAF25A26C05D84F
lt68u F76CC3C4858326ED
lt68i E5C63610938D06CA
lt68d 0BD4A369125E6882
lt68e 4D6B65821C2E44BF
lt69u C5B4D4EE09571951
lt69i E5C63610938D06CA
lt69d DD1575BCA00ABDB3
lt69e 0D58E95DA4A3EE71
lt70u 62F2CCC9FA7133C4
lt70i E5C63610938D06CA
lt70d 0D67483E9D18F1DD
lt70e 96B1DFE0DA2C08F6
lt71u ED33BD148DEE80AC
lt71i 1264C14B498525A1
lt71d AD9C7A6621D845D0
lt71e 477FF3DAACEF70DE
lt73u 577EB05F656A8300
lt73i EC17FACCC71DF8AA
lt73d 2F06EF5AF59A27E4
lt73e 60A94CA8C5542943
lt76u E56BCFE8A633039E
lt76i E5C63610938D06CA
lt76d 335DB8D087A0C147
lt76e D511412DD67E1F88
lt77u E4CC49A2E7E8B651
lt77i 7812B17C2C0964D0
lt77d 5162208EF1A49113
lt77e 45E44E11BCA1A0FD
lt78u 5F72F531F46520B6
lt78i E5C63610938D06CA
lt78d 7C2AB2CC2BCB9945
lt78e 10BEF4E87F049630
lt80u E320A6AE494B98D7
lt80i E5C63610938D06CA
lt80d 44B2270FDA90255B
lt80e 1CF5E053AAE9BEDB
lt82u D5954227B3A15B91
lt82i E5C63610938D06CA
lt82d BBC0D272ECE0DD4F
lt82e 14E7096378D46997
lt83u 335B819CA9EAE479
lt83i E5C63610938D06CA
lt83d E1A7325666964403
lt83e 2483570360F15454
lt84u C48BC8F79F66778C
lt84i E5C63610938D06CA
lt84d D02DCCF5500316A7
lt84e C0ED50B5CC36121A
lt85u DCC0D87F9BDFB7A0
lt85i E5C63610938D06CA
lt85d 381ACF05BE366C42
lt85e D0FDE16C906A4464
lt86u 07D5E61D77F32230
lt86i E5C63610938D06CA
lt86d 5AAF9A59316AFD59
lt86e 0BC8D7822C687F45
lt87u 3FC754370FF03F4A
lt87i E5C63610938D06CA
lt87d C98E4E328382BA03
lt87e 457B124E75C4320E
lt88u C1D7BCE9F0E43AB8
lt88i E5C63610938D06CA
lt88d C1493FA4E41F1E89
lt88e E86D40F6A8FF36DB
lt90u 8BEA952191DFFC00
lt90i E5C63610938D06CA
lt90d A97E30669D2B6E53
lt90e 58978A1C7283F3C9
lt91u BB25BF5E431CE6B1
lt91i E5C63610938D06CA
lt91d A4A9F5AE4FCD2AAC
lt91e F1875F3DE2DD4892
lt92u 02EF497B2FBCBD11
lt92i E5C63610938D06CA
lt92d 3FBF8180C9186313
lt92e BBAB66334466D11A
lt93u 1A344D49EBCBC239
lt93i 2D5B8B2361FF0F43
lt93d 599D4D5B741BBECC
lt93e 1E9FD7015163DA3B
lt94u 903DDD8C572E0155
lt94i E7E291878C7ECD98
lt94d 659125D8B4B39205
lt94e 98230CD4632C1C7C
lt95u 11347F788B008705
lt95i E5C63610938D06CA
lt95d 1D71F7EA7D709937
lt95e 4D859E7EC5D2BF23
lt96u 215A7DD1EE33810E
lt96i E5C63610938D06CA
lt96d 5783716878B8DEA5
lt96e EF402B46FD7FE7E3
lt97u 98475D6E25D04679
lt97i E5C63610938D06CA
lt97d 1B80012C1FB01581
lt97e E6F1D566C172C438
lt98u B66D3EA3CAD9419C
lt98i E5C63610938D06CA
lt98d C8C8C4D4911F4BC0
lt98e 932F09E445849F3E
lt99u D4AF24A808AA3DD0
lt99i E5C63610938D06CA
lt99d 6BD8E45CEFD5AEE6
lt99e 15AEA4B6B18A9A75
lt100u C312E194E155FEA5
lt100i E5C63610938D06CA
lt100d 2B0828012EE1A36E
lt100e 56DFEB5D6BEC1F93
lt101u 9E34129018BA0B57
lt101i E5C63610938D06CA
lt101d 577C5CBEF225874D
lt101e 6433B2CD168BE7CC
lt102u C77F4366FD9EEFF8
lt102i E5C63610938D06CA
lt102d 141F44CC953DB278
lt102e 7CC79D71883B8B53
lt104u 2DF0EFF39C0A64D6
lt104i E5C63610938D06CA
lt104d 1E93B1B214408D77
lt104e 0A5BFF80A774380D
lt105u 4D8D899AD1285CBE
lt105i E5C63610938D06CA
lt105d 875157F3A1C9CA4A
lt105e 39374087039B528F
lt106u DA6273995E96EBCF
lt106i E5C63610938D06CA
lt106d 5D8B2CE192CD5785
lt106e CBFBDC504C281313
lt107u 2D8C087B44854F1E
lt107i 4E21F0A24DC3D78B
lt107d 070FF026BEF142AF
lt107e D6D9833DC17DF5C0
lt108u 3B2B259021D89CE4
lt108i E5C63610938D06CA
lt108d C88FD9323CC56E4B
lt108e 49CD9D0E0413B33A
lt109u 2B17A1C210D8F159
lt109i E5C63610938D06CA
lt109d D5A2598D5BA718E0
lt109e ECF26017BA103C75
lt110u DE050ED29FAB01BD
lt110i E5C63610938D06CA
lt110d D564A1B9F6FB7A8D
lt110e D16B69F2F0DBCEC3
lt111u 6CE3863D94696D74
lt111i E5C63610938D06CA
lt111d 19D0FCEA36AECED5
lt111e 7E2ABF95FC4290EF
lt112u B8BFEA7F46FE04F2
lt112i E5C63610938D06CA
lt112d 336FDF132B6A6DEE
lt112e E01D288348120069
lt113u F1587B0BF6C76E0C
lt113i E5C63610938D06CA
lt113d 55BE2351D1662CE3
lt113e 96601EC7C2A342B4
lt114u C6955750051F08D0
lt114i E5C63610938D06CA
lt114d 22525E8FAA9E984A
lt114e 0A1F3D2F9567E152
lt115u 336DF984AAEE616D
lt115i E5C63610938D06CA
lt115d 0F2C198E54948FC6
lt115e 8C69FFD098A7F628
lt116u 139F324CC9639AAD
lt116i E5C63610938D06CA
lt116d 9E90D39802AC771D
lt116e 2E5D3F1B0CD0847A
lt118u 09C694F2CF4E2A49
lt118i E5C63610938D06CA
lt118d B40C41AFB589C9EF
lt118e 5D1B01AE8B29F4C2
lt119u 798640549D39260E
lt119i E5C63610938D06CA
lt119d 717C3987BFD7D3FF
lt119e C27BEF8FC87A9E18
touch0.0u DE2C6B72942B79E2
touch0.0i E5C63610938D06CA
touch0.0d A2953D8963621578
touch0.0e F8751FC94872C42C
touch0.1u AE85B52126D568AF
touch0.1i E5C63610938D06CA
touch0.1d A2953D8963621578
touch0.1e 374FA8048743079D
touch0.2u D44421804534A63D
touch0.2i E5C63610938D06CA
touch0.2d A2953D8963621578
touch0.2e C4E5B55B1C3CCAC5
touch0.3u A5983BDB46F24557
touch0.3i E5C63610938D06CA
touch0.3d A2953D8963621578
touch0.3e D6B1950C84732153
touch0.4u B58D3A84C5881E08
touch0.4i 73080EF6C2645822
touch0.4d 8580A4208C69F439
touch0.4e 4A59E388C95B8F4B
touch0.5u 644E688DB3FB0582
touch0.5i FAE8ADC1D21C834A
touch0.5d FAE8ADC1D21C834A
touch0.5e DD4072FB48023146
touch0.6u D542BF31172B6F61
touch0.6i E528473DD99840A8
touch0.6d E9E5AE650C2C3472
touch0.6e 5D00849D732077FA
touch0.7u 9D638BEA63999542
touch0.7i E5C63610938D06CA
touch0.7d A2953D8963621578
touch0.7e 849686725716FDCE
touch0.8u 091EC12A988AC5DA
touch0.8i E5C63610938D06CA
touch0.8d A2953D8963621578
touch0.8e 291305F390F39A66
touch0.9u A2953D8963621578
touch0.9i C4BF3A318DECD654
touch0.9d 70D21F825B1A6010
touch0.9e E5C63610938D06CA
touch0.10u 867A98DCA25568A2
touch0.10i E5C63610938D06CA
touch0.10d A2953D8963621578
touch0.10e 40F1116ACD53261B
touch0.11u 6456FA566C845F5D
touch0.11i B542F6B88077F97A
touch0.11d FF30E4FB9E8A88EC
touch0.11e 4574666A260BE7EE
touch0.chain0 A2953D8963621578
touch0.chain1 A2953D8963621578
touch0.chain2 D44421804534A63D
touch0.chain3 D44421804534A63D
touch0.chain4 31AF4D1AF54D832A
touch0.chain5 5D6FEB044DA2898F
touch0.chain6 2180E51D62986640
touch0.chain7 2180E51D62986640
touch0.chain8 A82C233DA1999E6C
touch0.chain9 F28F24FF5C80B374
touch0.chain10 F28F24FF5C80B374
touch0.chain11 1BE458F86110E152
touch1.0u DC7CC0AE1F08CC39
touch1.0i E5C63610938D06CA
touch1.0d A41BEDC9B8022F3E
touch1.0e 029E0063E42412AB
touch1.1u 0C3E3D1310807AEB
touch1.1i E5C63610938D06CA
touch1.1d A41BEDC9B8022F3E
touch1.1e FFCE1D136EFCA1A8
touch1.2u 6E7433115C75B769
touch1.2i E5C63610938D06CA
touch1.2d A41BEDC9B8022F3E
touch1.2e CD395E5C3DDCB0A4
touch1.3u 42FAFBAF808A7181
touch1.3i E5C63610938D06CA
touch1.3d A41BEDC9B8022F3E
touch1.3e B67D665251533677
touch1.4u E70ECDAAEC68D063
touch1.4i 5E487392D4A13948
touch1.4d F71BCBA8805C4888
touch1.4e 5F266A9810AB3DAA
touch1.5u 6E737E33ADC408A6
touch1.5i 6FCF41509C0FCFE0
touch1.5d 6FCF41509C0FCFE0
touch1.5e 9DC5C9AD09BB3856
touch1.6u C594BC8BC6E44F57
touch1.6i 39F1593C6CBB4F62
touch1.6d CB2C4C09EC67EA8C
touch1.6e 3429904F52C6AFE5
touch1.7u C24B007548CAC805
touch1.7i E5C63610938D06CA
touch1.7d A41BEDC9B8022F3E
touch1.7e 9A58837D1A270CA3
touch1.8u 0BB3F0079BDF7E8C
touch1.8i E5C63610938D06CA
touch1.8d A41BEDC9B8022F3E
touch1.8e 9CD3B296DB633633
touch1.9u A41BEDC9B8022F3E
touch1.9i 4368CC2759A855F7
touch1.9d 2EE9BB233BBA11D7
touch1.9e E5C63610938D06CA
touch1.10u A65206519D2755FB
touch1.10i E5C63610938D06CA
touch1.10d A41BEDC9B8022F3E
touch1.10e E44E663F974873EA
touch1.11u 2103D1CF0606030E
touch1.11i 99E5DC53B18A4EE8
touch1.11d 45E345161697C36B
touch1.11e 77AB9E7437ECBFBC
touch1.chain0 A41BEDC9B8022F3E
touch1.chain1 A41BEDC9B8022F3E
touch1.chain2 6E7433115C75B769
touch1.chain3 6E7433115C75B769
touch1.chain4 DA9AB590DAD0F9C6
touch1.chain5 2483553AE6339E7C
touch1.chain6 B0BE77FC33EBA343
touch1.chain7 B0BE77FC33EBA343
touch1.chain8 81E6027AD7727AD5
touch1.chain9 93709A9CFE651BE8
touch1.chain10 93709A9CFE651BE8
touch1.chain11 F324B9B95BEE815B
touch2.0u ED6B2ABC280C5040
touch2.0i E5C63610938D06CA
touch2.0d 34D840267C1E6110
touch2.0e 4635AEE15D926D83
touch2.1u 6CA0CF6382ECA356
touch2.1i E5C63610938D06CA
touch2.1d 34D840267C1E6110
touch2.1e 25A7B31EDA38F033
touch2.2u DAAE752301F6D3DF
touch2.2i E5C63610938D06CA
touch2.2d 34D840267C1E6110
touch2.2e 1CD27A1B711DC51A
touch2.3u 3B5EFFC3F9797CD6
touch2.3i E5C63610938D06CA
touch2.3d 34D840267C1E6110
touch2.3e 6D045FD881B31588
touch2.4u 60CE97CD704326A8
touch2.4i CCED7C5E64A36F4C
touch2.4d 62F9E8F3944D7BC2
touch2.4e 5FD809A89D4DA15A
touch2.5u AB1B8D36F097EA1F
touch2.5i F20CCF40416FCD4F
touch2.5d F20CCF40416FCD4F
touch2.5e EF435F9A6AF29D5B
touch2.6u 0F51E704C6941439
touch2.6i AAE089D9F23C1F81
touch2.6d 8912668445986848
touch2.6e 0338AE327E1894AD
touch2.7u D16582ED3782AA74
touch2.7i E5C63610938D06CA
touch2.7d 34D840267C1E6110
touch2.7e 6BF62DB3DFDBE3A6
touch2.8u 6DA30A3BF24ED321
touch2.8i E5C63610938D06CA
touch2.8d 34D840267C1E6110
touch2.8e BE70EA9001B29071
touch2.9u 34D840267C1E6110
touch2.9i 3D599D408E162C2B
touch2.9d 2A9B9132EA8EB941
touch2.9e E5C63610938D06CA
touch2.10u 7A869E94AF7C6E33
touch2.10i E5C63610938D06CA
touch2.10d 34D840267C1E6110
touch2.10e B9CB46838E552B1A
touch2.11u C33AC9030BC0E281
touch2.11i A1455A03DD63222E
touch2.11d 077A403D69E433A8
touch2.11e 02BD2A1B512E7D6F
touch2.chain0 34D840267C1E6110
touch2.chain1 34D840267C1E6110
touch2.chain2 DAAE752301F6D3DF
touch2.chain3 DAAE752301F6D3DF
touch2.chain4 56B51DFCB0DB9848
touch2.chain5 D7E80EDF37D8ACD1
touch2.chain6 BEAADCD973E38616
touch2.chain7 BEAADCD973E38616
touch2.chain8 2B5B6AF8F87EE580
touch2.chain9 9578312C73A27878
touch2.chain10 9578312C73A27878
touch2.chain11 040E4B7C0BDDAB3C
touch3.0u 915047F043351220
touch3.0i E5C63610938D06CA
touch3.0d A42562B6D9177937
touch3.0e 1018E0400C712F6F
touch3.1u 0BBC60069B8274D9
touch3.1i E5C63610938D06CA
touch3.1d A42562B6D9177937
touch3.1e 5DAD545AB46FEABB
touch3.2u 0921EE56C08DB000
touch3.2i E5C63610938D06CA
touch3.2d A42562B6D9177937
touch3.2e E83A94C77AAD9600
touch3.3u DB5B17823B4BB1AB
touch3.3i E5C63610938D06CA
touch3.3d A42562B6D9177937
touch3.3e F8A42206C5182ED0
touch3.4u 46EF219D7D95747D
touch3.4i 336F0E2A84F8A8AA
touch3.4d 84C0B3958A86006E
touch3.4e D12D46192FC87FC7
touch3.5u 24D132C56F6525A9
touch3.5i E5EA39126A598E14
touch3.5d E5EA39126A598E14
touch3.5e 3B2A55F349D3E602
touch3.6u D22A005DD4665839
touch3.6i 5BE25CC5D383B8B5
touch3.6d A9BA94DD6B72E367
touch3.6e F5AA90730D6E4F6C
touch3.7u 12D704F676057DC8
touch3.7i E5C63610938D06CA
touch3.7d A42562B6D9177937
touch3.7e D8E4FBF718AEB4A0
touch3.8u 3F6480CB57A31ECD
touch3.8i E5C63610938D06CA
touch3.8d A42562B6D9177937
touch3.8e 67E9BA4876222B8D
touch3.9u A42562B6D9177937
touch3.9i EA609883CF6CCB85
touch3.9d BEEA19F6DB05EB41
touch3.9e E5C63610938D06CA
touch3.10u 42120EE9E36A682B
touch3.10i E5C63610938D06CA
touch3.10d A42562B6D9177937
touch3.10e 8C55D48278E6572B
touch3.11u 62E4628D3691FA29
touch3.11i 162C3D9C79FBA3C0
touch3.11d 0BABBB16CD306C88
touch3.11e 13CF508628FC129D
touch3.chain0 A42562B6D9177937
touch3.chain1 A42562B6D9177937
touch3.chain2 0921EE56C08DB000
touch3.chain3 0921EE56C08DB000
touch3.chain4 362260FCBD631F08
touch3.chain5 A051B9F055375A2F
touch3.chain6 FF876A24AEBDD9CB
touch3.chain7 FF876A24AEBDD9CB
touch3.chain8 9C1D9E1559788B50
touch3.chain9 4E562F7C9791C68A
touch3.chain10 4E562F7C9791C68A
touch3.chain11 0C694A97C7F449E5
cc0.0r 62905F5CBE45AC8B
cc0.0m CB9CE5BDD58EF3B9
cc0.1r 84B6FF033037050D
cc0.1m D86D68D938AABDA9
cc0.2r C65ABFCD154AC98E
cc0.2m A4CFB5C8FDF9DDC1
cc0.3r 55D708B506508F93
cc0.3m 732EA9AB52EAAC3B
cc0.4r 1BF63876D538F466
cc0.4m 18B0A24808F7A979
cc0.5r 3C6F38D7D60CD12A
cc0.5m CB82C0F381B6DBC0
cc0.7r EAAA3EF33EA913AF
cc0.7m C8F360E7BCE9CA19
cc0.8r 789DAA09E06CE1F1
cc0.8m 24D396EF9CB5F809
cc0.9r 761CE148DE58A89C
cc0.9m A326294222DDCB90
cc0.final 761CE148DE58A89C
cc1.0r 8F8DB8A9829C9710
cc1.0m 768C77A9B4C02863
cc1.1r 6B5CF88600BD26C8
cc1.1m A001E0DA204A76FA
cc1.2r 34DD9234762E6DB7
cc1.2m F32FF71E0A5FD309
cc1.3r C88A18C084326746
cc1.3m 9595BA939A0145FC
cc1.4r 3061EB3877EB2198
cc1.4m 62164E962E6DE325
cc1.5r 4588877E9C7A89A0
cc1.5m F64D1DE9DF405EA0
cc1.6r 44E04CE3A0C07C44
cc1.6m 999B3C58DB4FC35A
cc1.7r C5C29D7C3BE9E323
cc1.7m 4C0BDD36CC13432A
cc1.8r 520458783B3C0F79
cc1.8m 71388926E953116A
cc1.9r 40B971A3FD04FA61
cc1.9m 6F1436560D0139F3
cc1.final 40B971A3FD04FA61
cc2.0r 8007D6FB659BDBC6
cc2.0m CB8E5511B7E885A0
cc2.1r 86DB3C3F01B33DF2
cc2.1m 89D5282E4B97B88A
cc2.2r 16C7ABDFF1C44399
cc2.2m F111C20C1B6D14CE
cc2.3r F272D55609E5E8C2
cc2.3m 1AD78F4F60683320
cc2.4r 947A65B91813994C
cc2.4m 885FAEE2B5AC82F4
cc2.5r 766A99A998F90EC4
cc2.5m 803DAFB1FA878951
cc2.6r 6F48E72D1458398C
cc2.6m E8D2CF3F5FB72D41
cc2.9r 78040F70BE07256F
cc2.9m 7B908E2EA9F30647
cc2.final 78040F70BE07256F
cc3.0r A73240E832B72750
cc3.0m BD4613098DA524C2
cc3.2r 29597B66D312F496
cc3.2m 62D888E507CB1DAB
cc3.3r D81A5EFEBE013824
cc3.3m BAFFDD7094E3EB11
cc3.4r 89F950D5233EADBF
cc3.4m 9196F2841ABFD86F
cc3.5r 813C22A9B2F2DDAE
cc3.5m 8FB724CD438C4BF2
cc3.6r F44EAF5BE6781C1C
cc3.6m 339D7A82962F3654
cc3.7r 599D1F26225C87FC
cc3.7m 32C0B780C09855FC
cc3.8r 773855C4D70DDB19
cc3.8m E3D4460F60E7703A
cc3.9r DFFC114543711722
cc3.9m F702DEA6CFAC06AF
cc3.final DFFC114543711722
cc4.0r D97F44A44587249A
cc4.0m D80A4C603C0695F2
cc4.1r EFD5954720782AED
cc4.1m 7848607FFC98EE69
cc4.2r 35420C16210C4E1C
cc4.2m 4B9B0E7B6C19CF1A
cc4.3r 5CBAC918E62A4A9E
cc4.3m 589172BA0D3952D8
cc4.4r 5D0B89FAF731D8EC
cc4.4m D0797545C23B44D8
cc4.5r D8AD665B45455463
cc4.5m 76F4A5EA69F7A51E
cc4.6r 990A03F0952B15F2
cc4.6m 915FEEABE89E6CFE
cc4.7r 75AD553452291ED7
cc4.7m BEFD6C22674EB98E
cc4.8r 7D71142CCF1F43F0
cc4.8m 615C7C271EB88EFD
cc4.9r 3EDFBA459311273F
cc4.9m A4EB7D09A7567129
cc4.final 3EDFBA459311273F
cc5.0r 568786D1224DB497
cc5.0m E5C63610938D06CA
cc5.1r 009D45F507E79365
cc5.1m E535C9DD5D480680
cc5.2r 30093F2888535801
cc5.2m 5E14001A5E903861
cc5.3r 68193D2EEB176DDF
cc5.3m 523FEAFE3305C166
cc5.4r B298012EE59FD14F
cc5.4m 74B45AF8271CB0B9
cc5.5r 7E4D68D3DEF81603
cc5.5m 7AD23D4E87894D83
cc5.6r 047676886F2012B0
cc5.6m 6B46A52E1E3D0DB7
cc5.7r A46119442E6F0EDF
cc5.7m C4AE353F9D39EAA6
cc5.8r 63FF2EB07B96E60B
cc5.8m A106F0529E96251B
cc5.9r 21E767B25584F3FF
cc5.9m B0BD40CB82CDB9FC
cc5.final 21E767B25584F3FF
cc6.0r 587C7AB1AABE2679
cc6.0m CAD54296A7EBBD47
cc6.1r E454A144009E1212
cc6.1m 9FCF467C04BCF091
cc6.2r ACEC2D1CEB80849C
cc6.2m DF1A5BC8F82DC395
cc6.3r 0423E9A68C0A6D59
cc6.3m E7B67981194B7C63
cc6.4r 1E463F1851648344
cc6.4m 75432368373C4CF7
cc6.5r 7E297237890AEB98
cc6.5m ACFF72DDD6CBC520
cc6.6r CA81873D07E6E5FE
cc6.6m 684227317E66E15E
cc6.7r DAA11A605DEC8485
cc6.7m 0003F7BC6A2CF73F
cc6.8r 1AB23E8FE30736CB
cc6.8m 5743747A99B5F320
cc6.9r 964656B832B0981A
cc6.9m 2F46AD7FFA444692
cc6.final 964656B832B0981A
cc7.0r 3E3537D5A723C771
cc7.0m A972A9709BBF418E
cc7.1r FA4AEEDBCF93ACE5
cc7.1m EC5B29FBEE7C8CEA
cc7.2r 0A9FD90723003A0E
cc7.2m A197C1D65B76FE78
cc7.3r EEE003A21440AC88
cc7.3m 0A399EB1FC1A122B
cc7.4r 327542F812A8CE9B
cc7.4m 744925AD4C8BA098
cc7.5r 8C67C826B190DC6E
cc7.5m 6951AB4E57658B69
cc7.6r 1713279017FD86A2
cc7.6m 2E22AE17695D1708
cc7.7r 332C5C0399EC4B64
cc7.7m 1D20817F6E97DC2D
cc7.8r F561A52C422A532E
cc7.8m C1043025B2C37097
cc7.9r 2CBE904B38CEF091
cc7.9m 90355413E82065AB
cc7.final 2CBE904B38CEF091
cc8.0r 7F5AC9924FB8F00E
cc8.0m E083C1DA46C8101A
cc8.1r BEA27EBF6F1F3000
cc8.1m A0A157599D10A0A9
cc8.2r AAEC98EEA8FB9647
cc8.2m 09942BB86FA1E840
cc8.3r A805CB6BAE955522
cc8.3m E25953A59F2B182B
cc8.5r EFACC5DAA5A22FD4
cc8.5m D6A7FA9C31E1614C
cc8.6r C870326989E1FB23
cc8.6m D3F4B3DD1E1F0E73
cc8.7r 73AB6EF6DEA06B8E
cc8.7m 8539E8958D5671A8
cc8.8r A426A155005A74F4
cc8.8m 75D3CD974AABFCC8
cc8.9r 70234A065E68D3BA
cc8.9m 0B4E0AEAB0871025
cc8.final 70234A065E68D3BA
cc9.0r 568786D1224DB497
cc9.0m E5C63610938D06CA
cc9.1r 5CE757EBFC401CB7
cc9.1m C4D6D426D722C772
cc9.2r 5CE757EBFC401CB7
cc9.2m E5C63610938D06CA
cc9.3r 5CE757EBFC401CB7
cc9.3m E5C63610938D06CA
cc9.4r 9632B117A7ADFAC6
cc9.4m 472219ACD10D672D
cc9.5r 3088A0E31C05EE77
cc9.5m F0D40C6DC223D9B9
cc9.6r CFA9FF8DA7D017E7
cc9.6m AD382A340BA5E21A
cc9.7r 6C06CF0CDC99C14D
cc9.7m DFE5D1845D553DE2
cc9.8r 7FC82A0F278F5F29
cc9.8m F7599C713D125BBE
cc9.9r E8D9B7E6D4BDF307
cc9.9m 00B581FD7A491BB9
cc9.final E8D9B7E6D4BDF307
cc10.0r CC368F4DDAF8C759
cc10.0m FC2037FB1C4308B9
cc10.1r F045880AAE744B63
cc10.1m 03839422F3FF0B3B
cc10.2r 876DF426A97A2CD7
cc10.2m DD42EA4582F6CAD3
cc10.3r 876DF426A97A2CD7
cc10.3m E5C63610938D06CA
cc10.4r CE8525FA8F992A06
cc10.4m D3B3463D21A08CC5
cc10.5r 3A20A3CD48F884CD
cc10.5m A4CBE6B71752AA5A
cc10.6r 618D9B3E1744D7AF
cc10.6m 2AF79517B1D897A4
cc10.7r DA830849E6885560
cc10.7m 750FE5ECED84A5C0
cc10.8r 9999353E5FC9358D
cc10.8m 24FF99E24B64C811
cc10.9r 9999353E5FC9358D
cc10.9m E5C63610938D06CA
cc10.final 9999353E5FC9358D
cc11.0r D529C2A5FC9F10E9
cc11.0m F60A1F719DB39E67
cc11.1r 0A0A974D22EBF2B8
cc11.1m 1F2BCF3C1735A553
cc11.2r 932D982871957F0E
cc11.2m 3DDC2751507FA20B
cc11.3r FB3646D8B3A438EC
cc11.3m 969A556440DCF3AC
cc11.4r FB3646D8B3A438EC
cc11.4m E5C63610938D06CA
cc11.5r E4E092E39EC259EC
cc11.5m 9EA97DE9DAE4C41E
cc11.6r AA55E5AA3D006A9F
cc11.6m 37B7F806F1BA5DD2
cc11.7r 4AF6839E32667992
cc11.7m 77FEE453D776257A
cc11.8r 35DB18F9F171BDAE
cc11.8m 43A3BCE78CF59045
cc11.9r 35DB18F9F171BDAE
cc11.9m E5C63610938D06CA
cc11.final 35DB18F9F171BDAE
cc12.0r 2491D7C548911A3D
cc12.0m 3AF0FA60176A4383
cc12.2r A8106A3932AD2655
cc12.2m 875484E7F5ED2205
cc12.3r 1C5A374FB45B18E6
cc12.3m F4DB7F4093D8EF67
cc12.4r 1C5A374FB45B18E6
cc12.4m E5C63610938D06CA
cc12.5r 1C5A374FB45B18E6
cc12.5m E5C63610938D06CA
cc12.6r 1C5A374FB45B18E6
cc12.6m E5C63610938D06CA
cc12.7r 5992AC356C3998FE
cc12.7m 2B2F48307F4024B2
cc12.8r 08C97F54760EBE0C
cc12.8m E52422A8DB6F8648
cc12.9r F6454E484F5193D5
cc12.9m A9D0039CB082933B
cc12.final F6454E484F5193D5
cc13.0r CCC30F1510CE26D5
cc13.0m 5705C9DF08FD02C8
cc13.1r F46D546816D5CDCB
cc13.1m FBD19B8D44B9ED32
cc13.2r 4983FEE550C63F19
cc13.2m 1BBE67FF5127D302
cc13.3r 45C5331A04D59D8C
cc13.3m 70BBC8A2EF2CC156
cc13.4r CF895EF6B5A5C95E
cc13.4m E1F1CB4CA184993D
cc13.6r CF895EF6B5A5C95E
cc13.6m E5C63610938D06CA
cc13.7r 1649BD2475E78C19
cc13.7m D0576C8F9E0BFF96
cc13.8r FECA023DE94AC7A2
cc13.8m 1885F7B2194015D3
cc13.9r 9A8B27BA1D350C47
cc13.9m 663B6C266A5B75F7
cc13.final 9A8B27BA1D350C47
cc14.0r 0E32E50B0E3278CE
cc14.0m 26C309758D09A907
cc14.1r 0E32E50B0E3278CE
cc14.1m E5C63610938D06CA
cc14.2r 5F540F571EF92A71
cc14.2m 0A0E0CC6960EB849
cc14.3r C17DD0A1A3B49343
cc14.3m CCBCFC2872485BDF
cc14.4r 4F69688A8D37D30A
cc14.4m E9BE9BA033F7FC05
cc14.5r 4380A515EF0AF11D
cc14.5m A7AED02CFF377A03
cc14.6r 24089A5EEC478FBD
cc14.6m 71BF7EFCB53556C4
cc14.7r 55624F8261C03837
cc14.7m D5E4CE676955E2D2
cc14.9r D138BFFDB217C214
cc14.9m E229CCFB8B57E826
cc14.final D138BFFDB217C214
cc15.0r 93C0D1278BA9F0C5
cc15.0m E86689B56C20BD48
cc15.1r FC8F74AEDF03D637
cc15.1m 3A29A0287177085A
cc15.2r 665F8715B3987AF8
cc15.2m BD6C0C30274F9F64
cc15.3r EC5B2A6EC05E1434
cc15.3m B41D513E9DBF17DB
cc15.4r EC5B2A6EC05E1434
cc15.4m E5C63610938D06CA
cc15.5r C4FD0E279CD2FF9E
cc15.5m A6D376FAF8A2071A
cc15.6r B8643BCE1B029328
cc15.6m 31775F9DF584EDF8
cc15.7r A5F616EEDB64611F
cc15.7m 05ED7A48090AC911
cc15.8r 389128EFA791B7EB
cc15.8m 1100978F89B94554
cc15.9r 461BFFF7F2EA004D
cc15.9m 6817A40049DB17F2
cc15.final 461BFFF7F2EA004D
plate0 119A2DB6F73C34FF
plate0d 141DD394D5416BC9
plate0i 7DA8E661A43EEB35
plate0x 21BC95D31CA1837F
plate1 FDF5B4A593D4337C
plate1d 5D3FB49E0F30411A
plate1i 32480E36A3948529
plate1x 1DC3357E88F06077
ball0.0 AFEDCB0B1410CDE7
ball0.0h 30B0B4CBA2D860C6
ball0.1 3E14188637A74494
ball0.1h CD6BBD0189871DFC
ball0.2 CD6BBD0189871DFC
ball0.2h F83BB03EF92557F9
ball0.3 F83BB03EF92557F9
ball0.3h A4F2A6F542DAD0BD
ball0.4 A4F2A6F542DAD0BD
ball0.4h CCDE2A094DC06248
ball0.5 CCDE2A094DC06248
ball0.5h 8FB7DE0ED0DCA0DE
ball1.0 C8C581A5378EC921
ball1.0h 4FC9D53C39CA7AB0
ball1.1 CCDDD40EE0C8DDE5
ball1.1h FE27EBD327C37FCB
ball1.2 FE27EBD327C37FCB
ball1.2h 2338A68179843C5F
ball1.3 2338A68179843C5F
ball1.3h 85CFDFDA21400376
ball1.4 85CFDFDA21400376
ball1.4h E4883CA5C64E9B1A
ball1.5 E4883CA5C64E9B1A
ball1.5h C7FCAD5B6257CC80
""";

    [Fact]
    public void WorkloadMomentsEqualReference()
    {
        var expected = ReferenceDigests.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var got = new List<string>();
        Rev6Workload.Run((name, s) => got.Add($"{name} {Rev6Workload.Digest(s)}"));
        Assert.Equal(expected.Length, got.Count);
        var diffs = new List<string>();
        for (int i = 0; i < got.Count; i++) if (expected[i] != got[i]) diffs.Add($"{expected[i]} -> {got[i]}");
        output.WriteLine($"{got.Count} results, {diffs.Count} differ");
        Assert.True(diffs.Count == 0, "geometry differs from b9a5faf: " + string.Join("; ", diffs.Take(10)));
    }

    /// <summary>
    /// Optional, stronger: with NANOCUT_R6_REF pointing at the .ncs dumps of the b9a5faf workload (written by the
    /// reference build), each result is compared with the reference solid by the exact volume of the symmetric
    /// difference (A − R) ∪ (R − A), which is zero only for equal point sets (up to measure zero). Without the variable
    /// the test returns immediately.
    /// </summary>
    [Fact]
    public void WorkloadSymmetricDifferenceWithReferenceIsEmpty()
    {
        var dir = Environment.GetEnvironmentVariable("NANOCUT_R6_REF");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        int n = 0;
        var bad = new List<string>();
        Rev6Workload.Run((name, s) =>
        {
            var r = Solid.Load(Path.Combine(dir, name + ".ncs"));
            var x1 = s - r;
            var x2 = r - s;
            if (!Volume6(x1).Num.IsZero || !Volume6(x2).Num.IsZero) bad.Add(name);
            n++;
        });
        output.WriteLine($"{n} results compared by symmetric difference, {bad.Count} differ");
        Assert.True(bad.Count == 0, "differs from reference: " + string.Join(", ", bad.Take(20)));
    }

    /// <summary>The workload's exact face structure (not only its geometry) is the same at parallelism 1 and 8.</summary>
    [Fact]
    public void WorkloadStructureIsIdenticalAtParallelism1And8()
    {
        List<string> RunAt(int p) => WithParallelism(p, () =>
        {
            var l = new List<string>();
            Rev6Workload.Run((name, s) => l.Add(name + " " + Rev4Workload.Hash(s)));
            return l;
        });
        var seq = RunAt(1);
        var par = RunAt(8);
        Assert.Equal(seq, par);
    }

    // ------------------------------------------------------------------------------------------------ (1) split skip

    /// <summary>
    /// Exact identities that hold for any correct Boolean, on configurations where the skipped planes matter most:
    /// a face of A coplanar with several faces of B (owners shared and distinct), faces of B touching A only along an
    /// edge or at a vertex, slivers one nanometre thick, and solids whose vertices are exact plane intersections near
    /// ±2^31. Checked: closure, V(A∪B) + V(A∩B) = V(A) + V(B), V(A−B) + V(A∩B) = V(A), and that A∩B, A−B are disjoint
    /// and cover A (exact volumes of (A−B)∩B and (A−B)∪(A∩B) − A).
    /// </summary>
    [Fact]
    public void InclusionExclusionHoldsExactlyOnTouchingAndCoplanarConfigurations()
    {
        var rng = new Random(6602);
        var pairs = new List<(string, Solid, Solid)>();
        for (int oi = 0; oi < Rev6Workload.Origins.Length; oi++)
        {
            var o = Rev6Workload.Origins[oi];
            // A: an exact-vertex solid (a box cut by a slanted lattice hull), B: lattice hulls and boxes sharing its planes.
            var cut = Rev6Workload.Hull([new(0, 0, 2), new(4, 0, 4), new(0, 4, 3), new(4, 4, 5), new(2, 2, 1), new(0, 0, 6), new(4, 4, 6)], Mm,
                new Vec3(o.X, o.Y, o.Z))!;
            var a = Rev6Workload.Box(o, 0, 0, 0, 4 * Mm, 4 * Mm, 3 * Mm) - cut;
            pairs.Add(($"cut{oi}", a, cut));
            for (int t = 0; t < 40; t++)
            {
                var b = Rev6Workload.Hull(Rev6Workload.LatticePoints(rng, 2, rng.Next(4, 9)), Mm,
                    new Vec3(o.X + rng.Next(-1, 4) * Mm, o.Y + rng.Next(-1, 4) * Mm, o.Z + rng.Next(-1, 4) * Mm));
                if (b is not null) pairs.Add(($"lat{oi}.{t}", a, b));
            }
            // Sliver: a 1 nm thick wedge lying on A's bottom face and crossing it.
            pairs.Add(($"sliver{oi}", a, Rev6Workload.Tet(new Vec3(o.X - Mm, o.Y + Mm, o.Z), new Vec3(o.X + 5 * Mm, o.Y + 2 * Mm, o.Z),
                new Vec3(o.X + 2 * Mm, o.Y + 3 * Mm + 7, o.Z), new Vec3(o.X + 2 * Mm, o.Y + 2 * Mm, o.Z + 1))));
            // Box meeting A only along the vertical edge x = 4 mm, y = 4 mm; and only at the corner (4, 4, 0).
            pairs.Add(($"edge{oi}", a, Rev6Workload.Box(o, 4 * Mm, 4 * Mm, 0, 5 * Mm, 5 * Mm, Mm)));
            pairs.Add(($"vertex{oi}", a, Rev6Workload.Box(o, 4 * Mm, 4 * Mm, -Mm, 5 * Mm, 5 * Mm, 0)));
            // Lattice translate of A (every face of A has coplanar partners of both orientations).
            pairs.Add(($"shift{oi}", a, Rev6Workload.Box(o, Mm, 0, 0, 5 * Mm, 4 * Mm, 3 * Mm) - Rev6Workload.Hull(
                [new(1, 0, 2), new(5, 0, 4), new(1, 4, 3), new(5, 4, 5), new(3, 2, 1), new(1, 0, 6), new(5, 4, 6)], Mm, new Vec3(o.X, o.Y, o.Z))!));
        }
        foreach (var (name, a, b) in pairs)
        {
            var u = a | b; var i = a & b; var d = a - b; var e = b - a;
            foreach (var (s, w) in new[] { (u, "u"), (i, "i"), (d, "d"), (e, "e") }) AssertClosed(s, $"{name}{w}");
            var va = Volume6(a); var vb = Volume6(b);
            Assert.True(Add(Volume6(u), Volume6(i)) == Add(va, vb), $"{name}: V(A∪B)+V(A∩B) ≠ V(A)+V(B)");
            Assert.True(Add(Volume6(d), Volume6(i)) == va, $"{name}: V(A−B)+V(A∩B) ≠ V(A)");
            Assert.True(Add(Volume6(e), Volume6(i)) == vb, $"{name}: V(B−A)+V(A∩B) ≠ V(B)");
            Assert.True(Volume6(d & b).Num.IsZero, $"{name}: (A−B)∩B not empty");
            Assert.True(Volume6((d | i) - a).Num.IsZero && Volume6(a - (d | i)).Num.IsZero, $"{name}: (A−B)∪(A∩B) ≠ A");
        }
        output.WriteLine($"{pairs.Count} pairs");
    }

    // ------------------------------------------------------------------------------------------------ (2) BVH select

    private static readonly MethodInfo SelectMethod =
        typeof(Bvh3).GetMethod("Select", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Bvh3.Select not found");

    /// <summary>
    /// Quickselect on degenerate key distributions (all equal, two values, sorted, reversed, organ pipe, few distinct,
    /// random) for every range length 1..300 and some up to 2000, with every k for short ranges: afterwards keys[k] is the
    /// k-th smallest, everything before is ≤ and everything after ≥, the order array is permuted alongside (key/index
    /// pairs preserved), and positions outside [lo, hi] are untouched.
    /// </summary>
    [Fact]
    public void BvhSelectPartitionsAroundTheKthKeyForDegenerateDistributions()
    {
        var rng = new Random(390);
        int checks = 0;
        for (int n = 1; n <= 2000; n += n < 300 ? 1 : 97)
        {
            for (int dist = 0; dist < 7; dist++)
            {
                var src = new double[n];
                for (int i = 0; i < n; i++)
                    src[i] = dist switch
                    {
                        0 => 5.0,
                        1 => rng.Next(2),
                        2 => i,
                        3 => n - i,
                        4 => Math.Min(i, n - 1 - i),
                        5 => rng.Next(3) * 1e9 - 1e9,
                        _ => rng.NextDouble() * 1e6 - 5e5,
                    };
                var ks = n <= 40 ? Enumerable.Range(0, n) : new[] { 0, n / 2, n - 1, rng.Next(n) };
                foreach (int k in ks)
                {
                    const int pad = 3;
                    var keys = new double[n + 2 * pad];
                    var order = new int[n + 2 * pad];
                    for (int i = 0; i < keys.Length; i++) { keys[i] = -7777; order[i] = -1; }
                    for (int i = 0; i < n; i++) { keys[pad + i] = src[i]; order[pad + i] = i; }
                    SelectMethod.Invoke(null, [keys, order, pad, pad + n - 1, pad + k]);
                    var sorted = (double[])src.Clone();
                    Array.Sort(sorted);
                    Assert.Equal(sorted[k], keys[pad + k]);
                    for (int i = 0; i < n; i++)
                    {
                        if (i < k) Assert.True(keys[pad + i] <= keys[pad + k]);
                        if (i > k) Assert.True(keys[pad + i] >= keys[pad + k]);
                        Assert.Equal(src[order[pad + i]], keys[pad + i]);
                    }
                    Assert.Equal(Enumerable.Range(0, n), order.Skip(pad).Take(n).Order());
                    for (int i = 0; i < pad; i++)
                        Assert.True(keys[i] == -7777 && keys[^(i + 1)] == -7777 && order[i] == -1 && order[^(i + 1)] == -1);
                    checks++;
                }
            }
        }
        output.WriteLine($"{checks} selections");
    }

    /// <summary>
    /// BVH box and six-direction ray queries equal brute force on degenerate face distributions (identical boxes, two
    /// clusters, all centres equal on the split axis, a diagonal line, nested boxes of one centre) for counts 9..2000;
    /// the node bound holds (the constructor throws otherwise).
    /// </summary>
    [Fact]
    public void BvhQueriesAreCompleteOnDegenerateKeyDistributions()
    {
        var rng = new Random(391);
        var result = new List<Face3>();
        int queries = 0;
        for (int n = 9; n <= 2000; n += n < 80 ? 1 : 61)
        {
            for (int dist = 0; dist < 5; dist++)
            {
                var faces = new List<Face3>(n);
                for (int i = 0; i < n; i++)
                {
                    long x, y, z, s = 10;
                    switch (dist)
                    {
                        case 0: x = y = z = 0; break;                                            // identical
                        case 1: x = rng.Next(2) * 100_000; y = 0; z = 0; break;                  // two clusters
                        case 2: x = 0; y = rng.Next(0, 50) * 1000; z = 0; s = 200_000; break;    // long in x: x keys all equal
                        case 3: x = i * 7; y = i * 7; z = i * 7; break;                          // diagonal line
                        default: x = -i * 5; y = -i * 5; z = 0; s = 10 * (i + 1); break;         // nested
                    }
                    faces.Add(Face3.FromGrid([new Vec3(x, y, z), new Vec3(x + s, y, z), new Vec3(x, y + s, z + s / 2)]));
                }
                using var bvh = new Bvh3(faces);
                if (n > 120 && n % 3 != 0) continue;
                for (int q = 0; q < 6; q++)
                {
                    double px = rng.Next(-2000, 210_000), py = rng.Next(-2000, 60_000), pz = rng.Next(-1000, 15_000);
                    if (q == 0) { var c = faces[rng.Next(n)].Box; px = c.MinX + 1; py = c.MinY + 1; pz = c.MinZ + 1; }
                    var box = new Box3(px, py, pz, px + rng.Next(0, 20_000), py + rng.Next(0, 20_000), pz + rng.Next(0, 5_000));
                    bvh.Query(box, result);
                    Assert.Equal(result.Count, result.Distinct().Count());
                    Assert.Equal(faces.Where(f => f.Box.Overlaps(box)).ToHashSet(), result.ToHashSet());
                    for (int axis = 0; axis < 3; axis++)
                        foreach (int sign in new[] { 1, -1 })
                        {
                            bvh.QueryRay(axis, sign, px, py, pz, result);
                            Assert.Equal(faces.Where(f => RayMeets(f.Box, axis, sign, px, py, pz)).ToHashSet(), result.ToHashSet());
                            queries++;
                        }
                }
            }
        }
        output.WriteLine($"{queries} ray queries");

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
}
#endif
