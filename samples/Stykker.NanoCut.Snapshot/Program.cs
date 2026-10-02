using System.Diagnostics;
using System.Globalization;
using Stykker.NanoCut;
using Stykker.NanoCut.Cutting;
using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Geometry3D;

// Computes example scenes and writes mesh buffers (JSON) for tools/snapshot.
// Usage: dotnet run -- <out-dir> [example1|gear|lathe|mill|all]
string outRoot = args.Length > 0 ? args[0] : "snapshot-out";
string which = args.Length > 1 ? args[1] : "all";
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var tol = Tolerance.Default;

if (which is "example1" or "all") Example1();
if (which is "gear" or "all") Gear();
if (which is "lathe" or "all") LatheScene();
if (which is "mill" or "all") Mill();

void Example1()
{
    var stock = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(20, 20, 10), tol);
    var sw = Stopwatch.StartNew();
    var result = Cutter.Cut(stock, Tool.Ball(3), ToolPath.Linear(Vec3.Mm(-5, 10, 12), Vec3.Mm(25, 10, 12)), tol);
    sw.Stop();
    double exact = (9 * Math.Acos(2.0 / 3) - 2 * Math.Sqrt(5)) * 20;
    var ghost = Solid.Capsule(Vec3.Mm(-5, 10, 12), Vec3.Mm(25, 10, 12), 3, Coarse());
    Write("example1", (10, 10, 5), result.Remaining, ghost,
        "Example 1 – ball r = 3 mm, 1 mm deep",
        $"removed volume {result.RemovedVolumeMm3:0.000000} mm³ (exact {exact:0.000000})",
        $"depth {result.MaxDepthMm:0.000000} mm · {result.Remaining.FaceCount} exact faces · {sw.ElapsedMilliseconds} ms");
}

void Gear()
{
    // Generating a spur gear (m = 2 mm, z = 20) with a rack: the blank rolls on the rack's pitch line.
    double m = 2;
    int z = 20;
    double rp = m * z / 2 * Units.NmPerMm, p = Math.PI * m * Units.NmPerMm;
    var blank = Region2.Circle(default, (m * z / 2 + m), tol);
    var rack = GearProfile.Rack(m, 7, 20, bodyMm: 0.5);
    var motion = Motion2.Sequence(Enumerable.Range(0, z).Select(k => Motion2.Custom(t =>
    {
        double dphi = 2 * Math.PI / z * t, phi = 2 * Math.PI * k / z + dphi;
        return Pose2.Rotation(-phi).Compose(new Pose2(0, p / 2 - rp * dphi, rp));
    })).ToArray());
    var sw = Stopwatch.StartNew();
    var gear2D = Process2.Cut([blank], rack, motion, tol, out var stats)[0];
    var gear = Solid.Extrude(gear2D, 0, 10);
    sw.Stop();
    // Flank deviation from the ideal involute.
    double alpha = 20 * Math.PI / 180, rb = rp * Math.Cos(alpha), ra = rp + m * Units.NmPerMm;
    double psiB = Math.PI / (2 * z) + GearProfile.Inv(alpha), worst = 0;
    foreach (var c in gear2D.Contours)
        foreach (var v in c.Points)
        {
            double r = Math.Sqrt((double)v.X * v.X + (double)v.Y * v.Y);
            if (r < rb + 0.4 * Units.NmPerMm || r > ra - 0.2 * Units.NmPerMm) continue;
            double ar = Math.Acos(rb / r), ideal = psiB - GearProfile.Inv(ar), th = Math.Atan2(v.Y, v.X);
            double rel = Math.Abs(Math.IEEERemainder(th, 2 * Math.PI / z));
            worst = Math.Max(worst, r * Math.Abs(rel - ideal) * Math.Sin(ar));
        }
    var rackSolid = Solid.Extrude(rack, -1, 11, Pose3.TranslationMm(0, rp / Units.NmPerMm, 0));
    Write("gear", (0, 0, 5), gear, rackSolid,
        "Gear generation – rack, m = 2 mm, z = 20",
        $"flank deviation from ideal involute ≤ {worst:0.0} nm · {stats.Intervals} roll steps",
        $"{gear2D.Contours.Sum(c => c.Count)} profile vertices · {sw.ElapsedMilliseconds} ms");
}

void LatheScene()
{
    // Turning a Ø20 bar: insert with 0.4 mm nose radius following a profile in the r–z plane.
    var bar = Lathe.BarProfile(10, 0, 40);
    var nose = Shapes2.CirclePoints(Vec2.Mm(0.4, 0), 0.4 * Units.NmPerMm, tol.ChordNm);
    var insert = Region2.Polygon(ConvexHull.Compute(nose.Concat([Vec2.Mm(6, 2.5), Vec2.Mm(6, -2.5)])));
    var path = Motion2.Polyline(
        Vec2.Mm(12, 42), Vec2.Mm(8, 42), Vec2.Mm(8, 28), Vec2.Mm(5, 22), Vec2.Mm(5, 12), Vec2.Mm(9, 8), Vec2.Mm(9, 3), Vec2.Mm(12, 3));
    var sw = Stopwatch.StartNew();
    var result = Lathe.Turn(bar, insert, path, tol);
    sw.Stop();
    double v0 = Math.PI * 100 * 40;
    var tool = Solid.Extrude(insert, -1, 1, Pose3.Rotation(Math.PI / 2, 1, 0, 0).Compose(Pose3.TranslationMm(0, 0, 0)))
        .Transform(Pose3.TranslationMm(9, 0, 3));
    Write("lathe", (0, 0, 20), result.Part, tool,
        "Turning – Ø20 bar, insert with 0.4 mm nose radius",
        $"turned volume {result.Part.VolumeMm3:0.000} mm³ (bar {v0:0.000} mm³)",
        $"{result.Profile.Contours.Sum(c => c.Count)} profile vertices · {sw.ElapsedMilliseconds} ms");
}

void Mill()
{
    // Pocket with a ball-nose end mill (r = 3 mm) on a zig-zag path, 3 mm deep.
    // Display scene: 1 µm chord error keeps the mesh small (at 50 nm the pocket alone has ~10⁶ facets).
    var tolMill = Tolerance.Budget(totalUm: 2.1, chordNm: 1000);
    var block = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(40, 30, 15), tolMill);
    var mill = ToolShape.BallNoseMill(3, 25, tolMill);
    var pts = new List<Vec3> { Vec3.Mm(8, 8, 25), Vec3.Mm(8, 8, 12) };
    for (int i = 0; i < 4; i++)
    {
        double y = 8 + i * 4.5;
        pts.Add(Vec3.Mm(i % 2 == 0 ? 32 : 8, y, 12));
        if (i < 3) pts.Add(Vec3.Mm(i % 2 == 0 ? 32 : 8, y + 4.5, 12));
    }
    var motion = Motion3.Polyline([.. pts]);
    var sw = Stopwatch.StartNew();
    var part = Process3.Cut([block], mill, motion, tolMill, out var stats)[0];
    sw.Stop();
    var ghost = ToolShape.BallNoseMill(3, 25, Coarse()).ToSolid().Transform(Pose3.TranslationMm(8, 21.5, 12));
    Write("mill", (20, 15, 7.5), part, ghost,
        "Milling – ball-nose end mill r = 3 mm, zig-zag pocket (chord 1 µm)",
        $"removed volume {block.VolumeMm3 - part.VolumeMm3:0.000000} mm³ · {stats.Intervals} moves",
        $"{part.FaceCount} exact faces · {sw.ElapsedMilliseconds} ms");
}

static Tolerance Coarse() => Tolerance.Budget(totalUm: 10, chordNm: 5000, sweepNm: 3000);

void Write(string name, (double X, double Y, double Z) origin, Solid part, Solid tool, params string[] info)
{
    string dir = Path.Combine(outRoot, name);
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "remaining.json"), Shift(part.ToMeshBuffers(OriginMode.Absolute), origin));
    File.WriteAllText(Path.Combine(dir, "tool.json"), Shift(tool.ToMeshBuffers(OriginMode.Absolute), origin));
    File.WriteAllText(Path.Combine(dir, "info.json"),
        "{\"view\":\"" + name + "\",\"lines\":[" + string.Join(',', info.Select(l => "\"" + l.Replace("\"", "'") + "\"")) + "]}");
    part.Save(Path.Combine(dir, "part.ncs"));
    Console.WriteLine($"[{name}] " + string.Join(" | ", info));
}

static string Shift(MeshBuffers b, (double X, double Y, double Z) o)
{
    var p = (float[])b.Positions.Clone();
    for (int i = 0; i < p.Length; i += 3)
    {
        p[i] = (float)(p[i] + b.OriginMm.X - o.X);
        p[i + 1] = (float)(p[i + 1] + b.OriginMm.Y - o.Y);
        p[i + 2] = (float)(p[i + 2] + b.OriginMm.Z - o.Z);
    }
    var inv = CultureInfo.InvariantCulture;
    return "{\"origin\":[" + string.Join(',', new[] { o.X, o.Y, o.Z }.Select(v => v.ToString(inv))) + "],\"positions\":[" +
           string.Join(',', p.Select(v => v.ToString("R", inv))) + "],\"normals\":[" +
           string.Join(',', b.Normals.Select(v => v.ToString("R", inv))) + "],\"indices\":[" +
           string.Join(',', b.Indices) + "]}";
}
