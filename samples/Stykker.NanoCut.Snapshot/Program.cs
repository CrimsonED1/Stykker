using System.Diagnostics;
using System.Globalization;
using Stykker.NanoCut;
using Stykker.NanoCut.Cutting;
using Stykker.NanoCut.Geometry3D;

// Example 1: ball tool r = 3 mm, 1 mm deep, straight across a 20 × 20 × 10 mm block.
string outDir = args.Length > 0 ? args[0] : "snapshot-out";
Directory.CreateDirectory(outDir);
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

var tol = Tolerance.Budget(totalUm: 0.1, chordNm: 50);
var stock = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(20, 20, 10), tol);
var path = ToolPath.Linear(Vec3.Mm(-5, 10, 12), Vec3.Mm(25, 10, 12));

var sw = Stopwatch.StartNew();
var result = Cutter.Cut(stock, Tool.Ball(3), path, tol);
sw.Stop();

double r = 3, h = 1;
double exact = (r * r * Math.Acos((r - h) / r) - (r - h) * Math.Sqrt(2 * r * h - h * h)) * 20;
Console.WriteLine($"cut time:        {sw.ElapsedMilliseconds} ms");
Console.WriteLine($"faces remaining: {result.Remaining.FaceCount}, swept tool faces: {result.Swept.FaceCount}");
Console.WriteLine($"removed volume:  {result.RemovedVolumeMm3:0.000000000} mm³ (exact {exact:0.000000000}, |Δ| {Math.Abs(result.RemovedVolumeMm3 - exact):0.###e+0})");
Console.WriteLine($"max depth:       {result.MaxDepthMm:0.000000000} mm");

// All buffers share one origin (centre of the block) so the scene lines up.
var origin = (10.0, 10.0, 5.0);
File.WriteAllText(Path.Combine(outDir, "remaining.json"), Shift(result.Remaining.ToMeshBuffers(OriginMode.Absolute), origin));
// Display-only ghost of the tool path: coarse capsule (chord 5 µm) to keep the file small.
var ghost = Solid.Capsule(Vec3.Mm(-5, 10, 12), Vec3.Mm(25, 10, 12), 3, Tolerance.Budget(totalUm: 10, chordNm: 5000, sweepNm: 3000));
File.WriteAllText(Path.Combine(outDir, "tool.json"), Shift(ghost.ToMeshBuffers(OriginMode.Absolute), origin));
File.WriteAllText(Path.Combine(outDir, "info.json"),
    $"{{\"title\":\"Example 1 – ball r = 3 mm, 1 mm deep\",\"removedVolume\":{result.RemovedVolumeMm3:0.000000},\"exactVolume\":{exact:0.000000},\"depth\":{result.MaxDepthMm:0.000000},\"faces\":{result.Remaining.FaceCount},\"ms\":{sw.ElapsedMilliseconds}}}");
Console.WriteLine($"written to {Path.GetFullPath(outDir)}");

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
