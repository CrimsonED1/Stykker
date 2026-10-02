using System.Diagnostics;
using Stykker.NanoCut.Cutting;
using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Geometry3D;
using Stykker.NanoCut.Testing;

namespace Stykker.NanoCut.Demo.Scenes;

public sealed class Example1Scene3D : DemoScene
{
    public override string Id => "example1-3d";
    public override string Title => "Example 1 – ball through block";
    public override string Category => "Reference cases";
    public override string Description =>
        "A ball tool moves straight across a 20 × 20 × 10 mm block. The removed volume, depth, groove width and the " +
        "distance of every groove vertex to the tool axis are compared with the analytic solution.";
    public override IReadOnlyList<Param> Params { get; } =
    [
        new("r", "Ball radius", 3, 0.5, 9, 0.5, "mm"),
        new("h", "Depth below top face", 1, 0.05, 5, 0.05, "mm"),
        new("chord", "Chord error", 1000, 50, 20000, 50, "nm"),
    ];

    public override SceneResult Run(IReadOnlyDictionary<string, double> p)
    {
        double r = p["r"], h = Math.Min(p["h"], p["r"] * 0.999);
        var tol = Tol(p["chord"]);
        var res = new SceneResult { View = "iso" };
        var stock = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(20, 20, 10), tol);
        double zc = 10 - h + r;
        var sw = Stopwatch.StartNew();
        var cut = Cutter.Cut(stock, Tool.Ball(r), ToolPath.Linear(Vec3.Mm(-r - 2, 10, zc), Vec3.Mm(22 + r, 10, zc)), tol);
        sw.Stop();
        double seg = r * r * Math.Acos((r - h) / r) - (r - h) * Math.Sqrt(2 * r * h - h * h);
        double width = 2 * Math.Sqrt(2 * r * h - h * h);
        double wetted = 2 * r * Math.Acos((r - h) / r) * 20;
        res.Checks.Add(new("removed volume", cut.RemovedVolumeMm3, seg * 20, wetted * tol.TotalMm, "mm³"));
        res.Checks.Add(new("max depth", cut.MaxDepthMm, h, tol.TotalMm, "mm"));
        var top = cut.Removed.Vertices.Where(v => Math.Abs(v.Z - 10e6) < 1e-3).Select(v => v.Y).ToList();
        res.Checks.Add(new("groove width at top", top.Count > 1 ? (top.Max() - top.Min()) * 1e-6 : 0, width, 2 * tol.TotalMm, "mm"));
        double worst = r;
        foreach (var v in cut.Removed.Vertices.Where(v => v.Z < 10e6 - 1))
        {
            double d = Math.Sqrt(Math.Pow(v.Y * 1e-6 - 10, 2) + Math.Pow(v.Z * 1e-6 - zc, 2));
            if (Math.Abs(d - r) > Math.Abs(worst - r)) worst = d;
        }
        res.Checks.Add(new("groove vertex to axis (worst)", worst, r, tol.TotalMm, "mm"));
        res.Metric("Removed volume", Mm3(cut.RemovedVolumeMm3));
        res.Metric("Exact", Mm3(seg * 20));
        res.Metric("Faces (remaining)", cut.Remaining.FaceCount.ToString());
        res.Metric("Tool faces", cut.Swept.FaceCount.ToString());
        res.Metric("Compute time", $"{sw.ElapsedMilliseconds} ms");
        res.Add("Remaining stock", cut.Remaining, "#9fb4c8");
        res.Add("Tool path", Solid.Capsule(Vec3.Mm(-r - 2, 10, zc), Vec3.Mm(22 + r, 10, zc), r, Tol(Math.Max(2000, p["chord"]))), "#ff8a3d", 0.2);
        res.Result = cut.Remaining;
        return res;
    }
}

public sealed class Example1Scene2D : DemoScene
{
    public override string Id => "example1-2d";
    public override string Title => "Example 1 – 2D cross-section";
    public override string Category => "Reference cases";
    public override string Description =>
        "Cross-section of example 1: rectangle 20 × 10 mm and a circle around (10, 10 − h + r). Intersection and " +
        "difference with the exact 2D kernel, compared with the circular-segment formula.";
    public override IReadOnlyList<Param> Params { get; } =
    [
        new("r", "Circle radius", 3, 0.5, 9, 0.5, "mm"),
        new("h", "Depth", 1, 0.05, 5, 0.05, "mm"),
        new("chord", "Chord error", 50, 1, 20000, 10, "nm"),
    ];

    public override SceneResult Run(IReadOnlyDictionary<string, double> p)
    {
        double r = p["r"], h = Math.Min(p["h"], p["r"] * 0.999);
        var tol = Tol(p["chord"]);
        var res = new SceneResult { View = "top" };
        var sw = Stopwatch.StartNew();
        var rect = Region2.Rectangle(Vec2.Mm(0, 0), Vec2.Mm(20, 10), tol);
        var circ = Region2.Circle(Vec2.Mm(10, 10 - h + r), r, tol);
        var cut = rect & circ;
        var rest = rect - circ;
        sw.Stop();
        double seg = r * r * Math.Acos((r - h) / r) - (r - h) * Math.Sqrt(2 * r * h - h * h);
        double arc = 2 * r * Math.Acos((r - h) / r);
        res.Checks.Add(new("cut area", cut.AreaMm2, seg, arc * tol.TotalMm, "mm²"));
        res.Checks.Add(new("remaining area", rest.AreaMm2, 200 - seg, arc * tol.TotalMm, "mm²"));
        res.Checks.Add(new("depth", Penetration2.Analyze(rect, circ).DepthMm, h, tol.TotalMm, "mm"));
        res.Metric("Cut area", Mm2(cut.AreaMm2));
        res.Metric("Exact", Mm2(seg));
        res.Metric("Circle segments", circ.Contours[0].Count.ToString());
        res.Metric("Compute time", $"{sw.ElapsedMilliseconds} ms");
        res.Add("Remaining", rest, "#9fb4c8");
        res.Add("Cut", cut, "#ff8a3d");
        return res;
    }
}

public sealed class Booleans3DScene : DemoScene
{
    private static readonly string[] Shapes = ["Box", "Sphere", "Cylinder", "Cone"];
    private static readonly string[] Ops = ["A − B", "A ∪ B", "A ∩ B"];

    public override string Id => "booleans-3d";
    public override string Title => "3D Booleans";
    public override string Category => "Kernels";
    public override string Description =>
        "Exact plane-based Booleans. New vertices are three-plane intersections stored in Int384 – never rounded. " +
        "The check verifies the volume identity V(A∪B) + V(A∩B) = V(A) + V(B).";
    public override IReadOnlyList<Param> Params { get; } =
    [
        new("a", "Shape A", 0, Choices: Shapes),
        new("b", "Shape B", 1, Choices: Shapes),
        new("op", "Operation", 0, Choices: Ops),
        new("dx", "Offset of B in x", 4, -10, 10, 0.5, "mm"),
        new("dz", "Offset of B in z", 4, -10, 10, 0.5, "mm"),
        new("chord", "Chord error", 2000, 50, 50000, 50, "nm"),
    ];

    public override SceneResult Run(IReadOnlyDictionary<string, double> p)
    {
        var tol = Tol(p["chord"]);
        var res = new SceneResult { View = "iso" };
        var a = Make((int)p["a"], 0, 0, tol);
        var b = Make((int)p["b"], p["dx"], p["dz"], tol);
        var sw = Stopwatch.StartNew();
        var result = (int)p["op"] switch { 1 => a | b, 2 => a & b, _ => a - b };
        sw.Stop();
        var u = a | b;
        var i = a & b;
        res.Checks.Add(new("V(A∪B) + V(A∩B) − V(A) − V(B)", u.VolumeMm3 + i.VolumeMm3 - a.VolumeMm3 - b.VolumeMm3, 0, 1e-9 * (a.VolumeMm3 + b.VolumeMm3), "mm³"));
        res.Metric("Volume A", Mm3(a.VolumeMm3));
        res.Metric("Volume B", Mm3(b.VolumeMm3));
        res.Metric("Result volume", Mm3(result.VolumeMm3));
        res.Metric("Result faces", result.FaceCount.ToString());
        res.Metric("Exact (non-grid) vertices", result.Vertices.Count(v => !v.IsGrid).ToString());
        res.Metric("Compute time", $"{sw.ElapsedMilliseconds} ms");
        res.Add("Result", result, "#9fb4c8");
        res.Add("A", a, "#3d8bff", 0.12);
        res.Add("B", b, "#ff8a3d", 0.12);
        res.Result = result;
        return res;
    }

    private static Solid Make(int kind, double dx, double dz, Tolerance tol) => kind switch
    {
        1 => Solid.Sphere(Vec3.Mm(dx, 0, dz), 6, tol),
        2 => Solid.Cylinder(Vec3.Mm(dx, -8, dz), Vec3.Mm(dx, 8, dz), 4, tol),
        3 => Solid.Cone(Vec3.Mm(dx, 0, dz - 6), Vec3.Mm(dx, 0, dz + 6), 6, 0, tol),
        _ => Solid.Box(Vec3.Mm(dx - 5, -5, dz - 5), Vec3.Mm(dx + 5, 5, dz + 5), tol),
    };
}

public sealed class Booleans2DScene : DemoScene
{
    private static readonly string[] Ops = ["A − B", "A ∪ B", "A ∩ B", "A ⊕ B (xor)"];

    public override string Id => "booleans-2d";
    public override string Title => "2D Booleans & offset";
    public override string Category => "Kernels";
    public override string Description =>
        "Exact 2D kernel with hot-pixel snap rounding: a plate with holes against a gear profile, plus an optional " +
        "offset of the result. The check verifies A(A∪B) + A(A∩B) = A(A) + A(B) up to the grid rounding bound.";
    public override IReadOnlyList<Param> Params { get; } =
    [
        new("op", "Operation", 0, Choices: Ops),
        new("dx", "Offset of the gear in x", 6, -20, 20, 0.5, "mm"),
        new("offset", "Offset of the result", 0, -3, 3, 0.25, "mm"),
        new("chord", "Chord error", 500, 1, 20000, 10, "nm"),
    ];

    public override SceneResult Run(IReadOnlyDictionary<string, double> p)
    {
        var tol = Tol(p["chord"]);
        var res = new SceneResult { View = "top" };
        var a = Region2.Rectangle(Vec2.Mm(-15, -12), Vec2.Mm(15, 12)) - Region2.Circle(Vec2.Mm(-9, -6), 3, tol)
                - Region2.Circle(Vec2.Mm(9, 6), 2.5, tol);
        var b = GearProfile.Involute(1.5, 14, 20, tol).Translate(Vec2.Mm(p["dx"], 0));
        var sw = Stopwatch.StartNew();
        var r = (int)p["op"] switch { 1 => a | b, 2 => a & b, 3 => a ^ b, _ => a - b };
        if (p["offset"] != 0) r = r.Offset(p["offset"], tol);
        sw.Stop();
        double lhs = (a | b).AreaMm2 + (a & b).AreaMm2, rhs = a.AreaMm2 + b.AreaMm2;
        res.Checks.Add(new("A(A∪B) + A(A∩B) − A(A) − A(B)", lhs - rhs, 0, (a.PerimeterMm + b.PerimeterMm) * 2e-6, "mm²"));
        res.Metric("Result area", Mm2(r.AreaMm2));
        res.Metric("Contours", r.Contours.Count.ToString());
        res.Metric("Vertices", r.Contours.Sum(c => c.Count).ToString());
        res.Metric("Convex parts", r.IsEmpty ? "0" : r.ConvexParts().Count.ToString());
        res.Metric("Compute time", $"{sw.ElapsedMilliseconds} ms");
        res.Add("Result", r, "#9fb4c8");
        return res;
    }
}

public sealed class GearProfileScene : DemoScene
{
    public override string Id => "gear-profile";
    public override string Title => "Gear generator";
    public override string Category => "Shapes";
    public override string Description =>
        "Involute spur gear profile with controlled chord error, split exactly into convex parts and extruded to a solid.";
    public override IReadOnlyList<Param> Params { get; } =
    [
        new("m", "Module", 2, 0.5, 5, 0.25, "mm"),
        new("z", "Teeth", 20, 8, 80, 1),
        new("alpha", "Pressure angle", 20, 14.5, 25, 0.5, "°"),
        new("width", "Face width", 10, 1, 40, 1, "mm"),
        new("chord", "Chord error", 500, 10, 20000, 10, "nm"),
    ];

    public override SceneResult Run(IReadOnlyDictionary<string, double> p)
    {
        var tol = Tol(p["chord"]);
        var res = new SceneResult { View = "gear" };
        var sw = Stopwatch.StartNew();
        var profile = GearProfile.Involute(p["m"], (int)p["z"], p["alpha"], tol);
        var solid = Solid.Extrude(profile, 0, p["width"]);
        sw.Stop();
        res.Checks.Add(new("volume = area × width", solid.VolumeMm3, profile.AreaMm2 * p["width"], 1e-9 * solid.VolumeMm3, "mm³"));
        res.Metric("Pitch diameter", Mm(p["m"] * p["z"], 3));
        res.Metric("Profile vertices", profile.Contours.Sum(c => c.Count).ToString());
        res.Metric("Convex parts", profile.ConvexParts().Count.ToString());
        res.Metric("Faces", solid.FaceCount.ToString());
        res.Metric("Compute time", $"{sw.ElapsedMilliseconds} ms");
        res.Add("Gear", solid, "#9fb4c8");
        res.Result = solid;
        return res;
    }
}

public sealed class GearGenerationScene : DemoScene
{
    public override string Id => "gear-generation";
    public override string Title => "Gear generation (rack)";
    public override string Category => "Processes";
    public override string Description =>
        "A rack cutter rolls on the pitch circle of a blank (relative motion: blank rotation + rack translation, " +
        "periodic segments). The generated flanks are compared with the ideal involute; the deviation scales with the " +
        "sweep step. The interpreter is slow – keep the sweep coarse in the browser (or publish with AOT).";
    public override IReadOnlyList<Param> Params { get; } =
    [
        new("m", "Module", 2, 0.5, 5, 0.25, "mm"),
        new("z", "Teeth", 16, 10, 40, 1),
        new("sweep", "Sweep step (path chord error)", 3000, 30, 20000, 10, "nm"),
        new("width", "Face width", 10, 1, 40, 1, "mm"),
    ];

    public override SceneResult Run(IReadOnlyDictionary<string, double> p)
    {
        double m = p["m"];
        int z = (int)p["z"];
        var tol = Tol(200, p["sweep"]);
        var res = new SceneResult { View = "gear" };
        double rp = m * z / 2 * Units.NmPerMm, pitch = Math.PI * m * Units.NmPerMm;
        var blank = Region2.Circle(default, m * z / 2 + m, tol);
        var rack = GearProfile.Rack(m, 7, 20, bodyMm: 0.3 * m);
        var motion = Motion2.Sequence(Enumerable.Range(0, z).Select(k => Motion2.Custom(t =>
        {
            double dphi = 2 * Math.PI / z * t, phi = 2 * Math.PI * k / z + dphi;
            return Pose2.Rotation(-phi).Compose(new Pose2(0, pitch / 2 - rp * dphi, rp));
        })).ToArray());
        var sw = Stopwatch.StartNew();
        var gear2D = Process2.Cut([blank], rack, motion, tol, out var stats)[0];
        var gear = Solid.Extrude(gear2D, 0, p["width"]);
        sw.Stop();

        double alpha = 20 * Math.PI / 180, rb = rp * Math.Cos(alpha), ra = rp + m * Units.NmPerMm;
        double psiB = Math.PI / (2 * z) + GearProfile.Inv(alpha), worst = 0;
        foreach (var c in gear2D.Contours)
            foreach (var v in c.Points)
            {
                double r = Math.Sqrt((double)v.X * v.X + (double)v.Y * v.Y);
                if (r < rb + 0.3 * m * Units.NmPerMm || r > ra - 0.1 * m * Units.NmPerMm) continue;
                double ar = Math.Acos(rb / r), ideal = psiB - GearProfile.Inv(ar);
                double rel = Math.Abs(Math.IEEERemainder(Math.Atan2(v.Y, v.X), 2 * Math.PI / z));
                worst = Math.Max(worst, r * Math.Abs(rel - ideal) * Math.Sin(ar));
            }
        res.Checks.Add(new("flank deviation from involute (worst)", worst * 1e-6, 0, p["sweep"] * 1e-6, "mm"));
        res.Metric("Flank deviation", Nm(worst));
        res.Metric("Roll steps", stats.Intervals.ToString());
        res.Metric("Swept pieces", stats.Pieces.ToString());
        res.Metric("Profile vertices", gear2D.Contours.Sum(c => c.Count).ToString());
        res.Metric("Compute time", $"{sw.ElapsedMilliseconds} ms");
        res.Add("Generated gear", gear, "#9fb4c8");
        res.Add("Rack", Solid.Extrude(rack, -1, p["width"] + 1, Pose3.TranslationMm(0, rp / Units.NmPerMm, 0)), "#ff8a3d", 0.25);
        res.Result = gear;
        return res;
    }
}

public sealed class TurningScene : DemoScene
{
    public override string Id => "turning";
    public override string Title => "Turning (lathe)";
    public override string Category => "Processes";
    public override string Description =>
        "A bar spins about z while an insert with nose radius follows a contour in the r–z plane. Computed exactly in the " +
        "r–z half-plane and revolved. The check compares the solid's volume with Pappus' theorem on the exact profile.";
    public override IReadOnlyList<Param> Params { get; } =
    [
        new("d", "Bar diameter", 20, 6, 60, 1, "mm"),
        new("nose", "Insert nose radius", 0.4, 0.1, 2, 0.1, "mm"),
        new("chord", "Chord error", 1000, 50, 20000, 50, "nm"),
    ];

    public override SceneResult Run(IReadOnlyDictionary<string, double> p)
    {
        double R = p["d"] / 2, k = R / 10, nose = p["nose"];
        var tol = Tol(p["chord"]);
        var res = new SceneResult { View = "lathe" };
        var bar = Lathe.BarProfile(R, 0, 40);
        var tip = Shapes2.CirclePoints(Vec2.Mm(nose, 0), nose * Units.NmPerMm, tol.ChordNm);
        var insert = Region2.Polygon(ConvexHull.Compute(tip.Concat([Vec2.Mm(6, 2.5), Vec2.Mm(6, -2.5)])));
        var path = Motion2.Polyline(
            Vec2.Mm(R + 2, 42), Vec2.Mm(8 * k, 42), Vec2.Mm(8 * k, 28), Vec2.Mm(5 * k, 22), Vec2.Mm(5 * k, 12),
            Vec2.Mm(9 * k, 8), Vec2.Mm(9 * k, 3), Vec2.Mm(R + 2, 3));
        var sw = Stopwatch.StartNew();
        var turned = Lathe.Turn(bar, insert, path, tol);
        sw.Stop();
        double pappus = 0; // V = 2π Σ (signed area · centroid r) over the exact polygon profile
        foreach (var c in turned.Profile.Contours)
            for (int i = 0; i < c.Count; i++)
            {
                Vec2 a = c[i], b = c[(i + 1) % c.Count];
                double cross = (a.XMm * b.YMm - b.XMm * a.YMm);
                pappus += cross * (a.XMm + b.XMm) / 6;
            }
        pappus *= 2 * Math.PI;
        double surface = 2 * Math.PI * R * 40 * 2;
        res.Checks.Add(new("volume vs. Pappus on exact profile", turned.Part.VolumeMm3, pappus, surface * tol.ChordNm * 1e-6, "mm³"));
        res.Metric("Part volume", Mm3(turned.Part.VolumeMm3));
        res.Metric("Bar volume", Mm3(Math.PI * R * R * 40));
        res.Metric("Profile vertices", turned.Profile.Contours.Sum(c => c.Count).ToString());
        res.Metric("Faces", turned.Part.FaceCount.ToString());
        res.Metric("Compute time", $"{sw.ElapsedMilliseconds} ms");
        res.Add("Turned part", turned.Part, "#9fb4c8");
        res.Add("Insert", Solid.Extrude(insert, -1, 1, Pose3.TranslationMm(9 * k, 0, 3).Compose(Pose3.Rotation(Math.PI / 2, 1, 0, 0))), "#ff8a3d", 0.6);
        res.Result = turned.Part;
        return res;
    }
}

public sealed class MillingScene : DemoScene
{
    public override string Id => "milling";
    public override string Title => "Milling (ball-nose pocket)";
    public override string Category => "Processes";
    public override string Description =>
        "A ball-nose end mill plunges and clears a pocket on a zig-zag path. Each move is an exact Minkowski sum of the " +
        "convex tool with the move, subtracted from the block.";
    public override IReadOnlyList<Param> Params { get; } =
    [
        new("r", "Tool radius", 3, 1, 6, 0.5, "mm"),
        new("depth", "Pocket depth", 3, 0.5, 8, 0.5, "mm"),
        new("step", "Step-over", 4.5, 1, 10, 0.5, "mm"),
        new("chord", "Chord error", 2000, 200, 20000, 100, "nm"),
    ];

    public override SceneResult Run(IReadOnlyDictionary<string, double> p)
    {
        double r = p["r"], depth = p["depth"], step = p["step"];
        var tol = Tol(p["chord"]);
        var res = new SceneResult { View = "mill" };
        var block = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(40, 30, 15), tol);
        var mill = ToolShape.BallNoseMill(r, 25, tol);
        double z = 15 - depth, x0 = 4 + r, x1 = 36 - r;
        var pts = new List<Vec3> { Vec3.Mm(x0, 4 + r, 25), Vec3.Mm(x0, 4 + r, z) };
        int lines = 0;
        for (double y = 4 + r; y <= 26 - r + 1e-9; y += step, lines++)
        {
            pts.Add(Vec3.Mm(lines % 2 == 0 ? x1 : x0, y, z));
            if (y + step <= 26 - r + 1e-9) pts.Add(Vec3.Mm(lines % 2 == 0 ? x1 : x0, y + step, z));
        }
        var sw = Stopwatch.StartNew();
        var part = Process3.Cut([block], mill, Motion3.Polyline([.. pts]), tol, out var stats)[0];
        sw.Stop();
        double floor = part.Vertices.Where(v => v.Z > 1e3).Min(v => v.Z); // ignore the block's bottom face (z = 0)
        res.Checks.Add(new("pocket floor depth", (15e6 - floor) * 1e-6, depth, tol.TotalMm, "mm"));
        res.Metric("Removed volume", Mm3(block.VolumeMm3 - part.VolumeMm3));
        res.Metric("Moves", stats.Intervals.ToString());
        res.Metric("Faces", part.FaceCount.ToString());
        res.Metric("Compute time", $"{sw.ElapsedMilliseconds} ms");
        res.Add("Part", part, "#9fb4c8");
        res.Add("Tool", mill.ToSolid().Transform(Pose3.TranslationMm(pts[^1].XMm, pts[^1].YMm, z)), "#ff8a3d", 0.3);
        res.Result = part;
        return res;
    }
}
