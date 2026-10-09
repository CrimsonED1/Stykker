using System.Globalization;
using Stykker.NanoCut;
using Stykker.NanoCut.Cutting;
using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Geometry3D;

/// <summary>
/// Frame sequences for animated GIFs: each process is cut in small steps and the state after every step is
/// written as binary mesh buffers (tools/snapshot/animate.mjs renders and encodes them).
/// </summary>
internal static class Animations
{
    public static void Run(string outRoot, string name)
    {
        switch (name)
        {
            case "anim-mill": Mill(outRoot); break;
            case "anim-lathe": LatheAnim(outRoot); break;
            case "anim-gear": Gear(outRoot); break;
            case "anim-cubes": Cubes(outRoot); break;
            default: throw new ArgumentException($"Unknown animation {name}");
        }
    }

    private sealed class Writer(string dir, string view, string title)
    {
        private readonly List<string> _frames = [];

        public void Frame(string caption, params (string Name, Solid Solid, string Color, double Opacity)[] objects)
        {
            int i = _frames.Count;
            var parts = new List<string>();
            for (int k = 0; k < objects.Length; k++)
            {
                var (n, s, c, o) = objects[k];
                string file = $"f{i:000}-{k}.bin";
                WriteBin(Path.Combine(dir, file), s.ToMeshBuffers(OriginMode.Absolute));
                parts.Add($"{{\"file\":\"{file}\",\"color\":\"{c}\",\"opacity\":{o.ToString(CultureInfo.InvariantCulture)}}}");
            }
            _frames.Add($"{{\"caption\":\"{caption}\",\"objects\":[{string.Join(',', parts)}]}}");
            Console.Write($"\r[{Path.GetFileName(dir)}] frame {i + 1}  ");
        }

        public void Finish()
        {
            File.WriteAllText(Path.Combine(dir, "scene.json"),
                $"{{\"view\":\"{view}\",\"title\":\"{title}\",\"frames\":[{string.Join(',', _frames)}]}}");
            Console.WriteLine($"\r[{Path.GetFileName(dir)}] {_frames.Count} frames written");
        }

        private static void WriteBin(string path, MeshBuffers b)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write((uint)b.Positions.Length);
            w.Write((uint)b.Indices.Length);
            foreach (var v in b.Positions) w.Write(v);
            foreach (var v in b.Normals) w.Write(v);
            foreach (var v in b.Indices) w.Write(v);
        }
    }

    private static Writer Open(string root, string name, string view, string title)
    {
        string dir = Path.Combine(root, name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        return new Writer(dir, view, title);
    }

    private static IEnumerable<(T A, T B)> Pairs<T>(IReadOnlyList<T> p)
    {
        for (int i = 0; i + 1 < p.Count; i++) yield return (p[i], p[i + 1]);
    }

    /// <summary>Splits a move into steps of at most <paramref name="maxStepMm"/>.</summary>
    private static IEnumerable<(double[] From, double[] To)> Steps(double[] a, double[] b, double maxStepMm)
    {
        double len = Math.Sqrt(a.Zip(b, (x, y) => (y - x) * (y - x)).Sum());
        int n = Math.Max(1, (int)Math.Ceiling(len / maxStepMm));
        for (int i = 0; i < n; i++)
            yield return (a.Zip(b, (x, y) => x + (y - x) * i / n).ToArray(), a.Zip(b, (x, y) => x + (y - x) * (i + 1) / n).ToArray());
    }

    private static void Mill(string root)
    {
        var tol = Tolerance.Budget(totalUm: 2.1, chordNm: 1000);
        var w = Open(root, "anim-mill", "mill", "Milling – ball-nose end mill r = 3 mm");
        var part = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(40, 30, 15), tol);
        var mill = ToolShape.BallNoseMill(3, 25, tol);
        var shown = ToolShape.BallNoseMill(3, 25, Tolerance.Budget(totalUm: 10, chordNm: 5000, sweepNm: 3000)).ToSolid();
        double[][] path = [[8, 8, 22], [8, 8, 12], [32, 8, 12], [32, 12.5, 12], [8, 12.5, 12], [8, 17, 12], [32, 17, 12], [32, 21.5, 12], [8, 21.5, 12], [8, 21.5, 22]];
        w.Frame("start", ("part", part, "#9fb4c8", 1), ("tool", shown.Transform(Pose3.TranslationMm(8, 8, 22)), "#ff8a3d", 0.55));
        foreach (var (a, b) in Pairs(path))
            foreach (var (f, t) in Steps(a, b, 2.5))
            {
                part = Process3.Cut(part, mill, Motion3.Linear(Vec3.Mm(f[0], f[1], f[2]), Vec3.Mm(t[0], t[1], t[2])), tol);
                w.Frame($"G1 X{t[0]:0.0} Y{t[1]:0.0} Z{t[2]:0.0}", ("part", part, "#9fb4c8", 1),
                    ("tool", shown.Transform(Pose3.TranslationMm(t[0], t[1], t[2])), "#ff8a3d", 0.55));
            }
        w.Finish();
    }

    private static void LatheAnim(string root)
    {
        var tol = Tolerance.Budget(totalUm: 5.1, chordNm: 5000);
        var w = Open(root, "anim-lathe", "lathe", "Turning – Ø30 bar, insert with 0.4 mm nose radius");
        var profile = Lathe.BarProfile(15, 0, 40);
        var tip = Shapes2.CirclePoints(Vec2.Mm(0.4, 0), 0.4e6, 50);
        var insert = Region2.Polygon(ConvexHull.Compute(tip.Concat([Vec2.Mm(7, 2.5), Vec2.Mm(7, -2.5)])));
        var insertSolid = Solid.Extrude(insert, -1, 1);
        double[][] path = [[17, 42], [13, 42], [13, 10], [17, 10], [17, 42], [11, 42], [11, 20], [13, 14], [17, 14], [17, 42],
                           [9, 42], [9, 30], [11, 26], [17, 26], [17, 8], [8, 8], [17, 8]];
        Solid Insert(double r, double z) => insertSolid.Transform(Pose3.TranslationMm(r, 0, z).Compose(Pose3.Rotation(Math.PI / 2, 1, 0, 0)));
        w.Frame("start", ("part", Solid.Revolve(profile, tol), "#9fb4c8", 1), ("tool", Insert(17, 42), "#ff8a3d", 0.9));
        foreach (var (a, b) in Pairs(path))
            foreach (var (f, t) in Steps(a, b, 2))
            {
                profile = Process2.Cut(profile, insert, Motion2.Linear(Vec2.Mm(f[0], f[1]), Vec2.Mm(t[0], t[1])), tol);
                w.Frame($"X{2 * t[0]:0.0} Z{t[1]:0.0}", ("part", Solid.Revolve(profile, tol), "#9fb4c8", 1), ("tool", Insert(t[0], t[1]), "#ff8a3d", 0.9));
            }
        w.Finish();
    }

    private static void Gear(string root)
    {
        double m = 2;
        int z = 16;
        var tol = Tolerance.Budget(totalUm: 3.3, chordNm: 200, sweepNm: 3000);
        var w = Open(root, "anim-gear", "gear", "Gear generation – rack rolling, m = 2 mm, z = 16");
        double rp = m * z / 2 * Units.NmPerMm, p = Math.PI * m * Units.NmPerMm;
        var gear = Region2.Circle(default, m * z / 2 + m, tol);
        var rack = GearProfile.Rack(m, 7, 20, bodyMm: 0.8);
        int framesPerSegment = 3;
        for (int k = 0; k < z; k++)
            for (int f = 0; f < framesPerSegment; f++)
            {
                double t0 = (double)f / framesPerSegment, t1 = (double)(f + 1) / framesPerSegment;
                var motion = Motion2.Custom(t =>
                {
                    double tt = t0 + (t1 - t0) * t, dphi = 2 * Math.PI / z * tt, phi = 2 * Math.PI * k / z + dphi;
                    return Pose2.Rotation(-phi).Compose(new Pose2(0, p / 2 - rp * dphi, rp));
                });
                gear = Process2.Cut(gear, rack, motion, tol);
                // World view: the blank turns by φ, the rack slides along its pitch line.
                double dphiEnd = 2 * Math.PI / z * t1, phiEnd = 2 * Math.PI * k / z + dphiEnd;
                var gearSolid = Solid.Extrude(gear, 0, 8, Pose3.Rotation(phiEnd, 0, 0, 1));
                var rackSolid = Solid.Extrude(rack, -1, 9, Pose3.TranslationMm((p / 2 - rp * dphiEnd) / 1e6, rp / 1e6, 0));
                w.Frame($"φ = {phiEnd * 180 / Math.PI:0}°", ("gear", gearSolid, "#9fb4c8", 1), ("rack", rackSolid, "#ff8a3d", 0.6));
            }
        w.Finish();
    }

    private static void Cubes(string root)
    {
        var tol = Tolerance.Budget(totalUm: 50.1, chordNm: 50, sweepNm: 50000);
        var w = Open(root, "anim-cubes", "cubes", "Free-form – a cube moving and turning through a cube");
        var work = Solid.Box(Vec3.Mm(-10, -10, -10), Vec3.Mm(10, 10, 10));
        var cube = Solid.Box(Vec3.Mm(-4, -4, -4), Vec3.Mm(4, 4, 4));
        var tool = ToolShape.FromConvexParts(cube);
        Pose3 P(double x, double y, double z, double deg, double ax, double ay, double az) =>
            Pose3.Rotation(deg * Math.PI / 180, ax, ay, az) with { TxNm = x * 1e6, TyNm = y * 1e6, TzNm = z * 1e6 };
        Pose3[] keys = [P(-18, -4, 9, 0, 0, 0, 1), P(0, -2, 7, 45, 0, 0, 1), P(18, 0, 9, 90, 1, 1, 0), P(8, 12, 2, 150, 0, 1, 1), P(-8, 2, -2, 200, 1, 0, 1), P(-20, -10, -8, 240, 1, 1, 1)];
        w.Frame("start", ("work", work, "#9fb4c8", 1), ("tool", cube.Transform(keys[0]), "#ff8a3d", 0.55));
        foreach (var (a, b) in Pairs(keys))
        {
            const int n = 8;
            for (int i = 0; i < n; i++)
            {
                var pa = Pose3.Interpolate(a, b, (double)i / n);
                var pb = Pose3.Interpolate(a, b, (double)(i + 1) / n);
                work = Process3.Cut(work, tool, Motion3.Between(pa, pb), tol);
                w.Frame($"{work.FaceCount} exact faces", ("work", work, "#9fb4c8", 1), ("tool", cube.Transform(pb), "#ff8a3d", 0.55));
            }
        }
        w.Finish();
    }
}
