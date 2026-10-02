// Usage: dotnet run -c Release -- <expanded.json> <out-dir> <nanocut|manifoldsharp> [--warm]
// Runs the steps of an expanded bench scene (workpiece box minus the convex hull of each step's points, in order) and
// writes stats.json plus binary STL files of the states listed in "save". Only the cutting is timed.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stykker.NanoCut;
using Stykker.NanoCut.Geometry3D;
using MS = ManifoldSharp;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: <expanded.json> <out-dir> <nanocut|manifoldsharp>");
    return 2;
}
var scene = JsonNode.Parse(File.ReadAllText(args[0]))!;
string outDir = args[1], engine = args[2];
bool warm = args.Contains("--warm");
// --batch k: cut k consecutive steps at once (hulls of the group computed in parallel, united, then subtracted once).
int batch = Array.IndexOf(args, "--batch") is int bi and >= 0 ? int.Parse(args[bi + 1]) : 1;
// --pipeline (nanocut): the hull of the next step is computed on other cores while the current step is subtracted.
bool pipeline = args.Contains("--pipeline");
Directory.CreateDirectory(outDir);

long[] Corner(string key) => scene["box"]![key]!.AsArray().Select(v => v!.GetValue<long>()).ToArray();
var min = Corner("min");
var max = Corner("max");
var steps = scene["steps"]!.AsArray()
    .Select(s => s!.AsArray().Select(p => p!.AsArray().Select(v => v!.GetValue<long>()).ToArray()).ToArray())
    .ToArray();
var save = scene["save"]!.AsArray().Select(v => v!.GetValue<int>()).ToHashSet();

IEngine run = engine switch
{
    "nanocut" => new NanoCutEngine(),
    "manifoldsharp" => new ManifoldSharpEngine(),
    _ => throw new ArgumentException($"unknown engine {engine}"),
};

// Optional warm-up: the whole scene once, untimed, so that JIT compilation (tiered + PGO) is not measured.
if (warm)
{
    var w = engine == "nanocut" ? (IEngine)new NanoCutEngine() : new ManifoldSharpEngine();
    w.Start(min, max);
    if (pipeline && w is NanoCutEngine wn) wn.SubtractInOrder(steps.Select(st => (Func<Solid>)(() => NanoCutEngine.Hull(st))));
    else
        for (int i = 0; i < steps.Length; i += batch)
            if (Math.Min(batch, steps.Length - i) == 1) w.Cut(steps[i]);
            else w.CutBatch(steps[i..Math.Min(steps.Length, i + batch)]);
}

var stepMs = new List<double>();
var total = Stopwatch.StartNew();
run.Start(min, max);
if (save.Contains(0)) File.WriteAllBytes(Path.Combine(outDir, "step-0000.stl"), run.Stl());
total.Restart();
long alloc0 = GC.GetTotalAllocatedBytes(true);
int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
var gcPause0 = GC.GetTotalPauseDuration();
double saveMs = 0;
if (pipeline && run is NanoCutEngine nce)
{
    // Library cut chain: the next hull is built on another core while the current one is subtracted.
    var sw = Stopwatch.StartNew();
    nce.SubtractInOrder(steps.Select(st => (Func<Solid>)(() => NanoCutEngine.Hull(st))));
    double ms = sw.Elapsed.TotalMilliseconds;
    for (int i = 0; i < steps.Length; i++) stepMs.Add(ms / steps.Length);
    if (save.Contains(-1) || save.Contains(steps.Length))
    {
        var s = Stopwatch.StartNew();
        File.WriteAllBytes(Path.Combine(outDir, $"step-{steps.Length:0000}.stl"), run.Stl());
        saveMs += s.Elapsed.TotalMilliseconds;
    }
}
else
for (int i = 0; i < steps.Length; i += batch)
{
    int n = Math.Min(batch, steps.Length - i);
    var sw = Stopwatch.StartNew();
    if (n == 1) run.Cut(steps[i]);
    else run.CutBatch(steps[i..(i + n)]);
    double ms = sw.Elapsed.TotalMilliseconds;
    for (int k = 0; k < n; k++) stepMs.Add(ms / n);
    int last = i + n;
    if (save.Any(sv => sv > i && sv <= last) || (last == steps.Length && save.Contains(-1)))
    {
        var s = Stopwatch.StartNew();
        File.WriteAllBytes(Path.Combine(outDir, $"step-{last:0000}.stl"), run.Stl());
        saveMs += s.Elapsed.TotalMilliseconds;
    }
}
double totalMs = total.Elapsed.TotalMilliseconds - saveMs;
double allocMb = (GC.GetTotalAllocatedBytes(true) - alloc0) / 1e6;
string phases = engine == "nanocut" ? $"hull {NanoCutEngine.HullMs:F0} ms, boolean {NanoCutEngine.BooleanMs:F0} ms; " : "";
string gcInfo = phases + $"gen0 {GC.CollectionCount(0) - gc0}, gen1 {GC.CollectionCount(1) - gc1}, gen2 {GC.CollectionCount(2) - gc2}, " +
                $"pause {(GC.GetTotalPauseDuration() - gcPause0).TotalMilliseconds:F0} ms, allocated {allocMb:F0} MB";

var stats = new JsonObject
{
    ["engine"] = engine,
    ["language"] = "C#",
    ["exact"] = engine == "nanocut",
    ["warm"] = warm,
    ["batch"] = batch,
    ["pipeline"] = pipeline,
    ["steps"] = steps.Length,
    ["totalMs"] = totalMs,
    ["stepMs"] = new JsonArray(stepMs.Select(v => (JsonNode)v).ToArray()),
    ["volumeMm3"] = run.VolumeMm3(),
    ["triangles"] = run.Triangles(),
    ["allocatedMb"] = allocMb,
    ["gc"] = gcInfo,
};
File.WriteAllText(Path.Combine(outDir, "stats.json"), stats.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{engine}: {steps.Length} steps in {totalMs:F0} ms, V = {run.VolumeMm3():F9} mm³, {run.Triangles()} triangles; GC: {gcInfo}");
return 0;

interface IEngine
{
    void Start(long[] min, long[] max);
    void Cut(long[][] points);
    void CutBatch(long[][][] group);
    double VolumeMm3();
    long Triangles();
    byte[] Stl();
}

sealed class NanoCutEngine : IEngine
{
    private Solid _work = Solid.Empty;

    public void Start(long[] min, long[] max) =>
        _work = Solid.Box(new Vec3(min[0], min[1], min[2]), new Vec3(max[0], max[1], max[2]));

    public static double HullMs, BooleanMs;

    public void Cut(long[][] points)
    {
        var t = Stopwatch.GetTimestamp();
        var hull = ConvexHull3.Compute(points.Select(p => new Vec3(p[0], p[1], p[2])));
        var t1 = Stopwatch.GetTimestamp();
        _work -= hull;
        HullMs += Stopwatch.GetElapsedTime(t, t1).TotalMilliseconds;
        BooleanMs += Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
    }

    public static Solid Hull(long[][] points) => ConvexHull3.Compute(points.Select(p => new Vec3(p[0], p[1], p[2])));

    public void SubtractInOrder(IEnumerable<Func<Solid>> tools) => _work = Solid.SubtractInOrder(_work, tools);

    public void CutBatch(long[][][] group)
    {
        var t = Stopwatch.GetTimestamp();
        var hulls = new Solid[group.Length];
        Parallel.For(0, group.Length, k => hulls[k] = ConvexHull3.Compute(group[k].Select(p => new Vec3(p[0], p[1], p[2]))));
        var t1 = Stopwatch.GetTimestamp();
        _work -= Solid.UnionAll(hulls);
        HullMs += Stopwatch.GetElapsedTime(t, t1).TotalMilliseconds;
        BooleanMs += Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
    }

    public double VolumeMm3() => _work.VolumeMm3;

    public long Triangles() => _work.ToMeshBuffers(OriginMode.Absolute).Indices.Length / 3;

    public byte[] Stl() => _work.ToMeshBuffers(OriginMode.Absolute).ToStl();
}

// Coordinates are given to Manifold in nm (as doubles, exact for |c| < 2^53); volumes are converted to mm³.
sealed class ManifoldSharpEngine : IEngine
{
    private MS.Manifold _work = MS.Manifold.Empty();

    public void Start(long[] min, long[] max) =>
        _work = MS.Manifold.Cube(new MS.Linalg.Vec3(max[0] - min[0], max[1] - min[1], max[2] - min[2]), false)
            .Translate(new MS.Linalg.Vec3(min[0], min[1], min[2]));

    public void Cut(long[][] points) =>
        _work = _work.Difference(MS.Manifold.Hull(points.Select(p => new MS.Linalg.Vec3(p[0], p[1], p[2])).ToList()));

    public void CutBatch(long[][][] group)
    {
        var hulls = new MS.Manifold[group.Length];
        Parallel.For(0, group.Length, k => hulls[k] = MS.Manifold.Hull(group[k].Select(p => new MS.Linalg.Vec3(p[0], p[1], p[2])).ToList()));
        _work = _work.Difference(MS.Manifold.BatchBoolean(hulls, MS.OpType.Add));
    }

    public double VolumeMm3() => _work.Volume() * 1e-18;

    public long Triangles() => _work.NumTri();

    public byte[] Stl()
    {
        var m = _work.GetMeshGL64(0);
        var v = m.VertProperties;
        int np = (int)m.NumProp;
        return StlWriter.Write(m.TriVerts.Count / 3, (t, k) =>
        {
            int i = (int)m.TriVerts[3 * t + k] * np;
            return ((float)(v[i] * 1e-6), (float)(v[i + 1] * 1e-6), (float)(v[i + 2] * 1e-6));
        });
    }
}

static class StlWriter
{
    public static byte[] Write(int triangles, Func<int, int, (float X, float Y, float Z)> vertex)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(new byte[80]);
        w.Write((uint)triangles);
        for (int t = 0; t < triangles; t++)
        {
            w.Write(0f); w.Write(0f); w.Write(0f);
            for (int k = 0; k < 3; k++)
            {
                var (x, y, z) = vertex(t, k);
                w.Write(x); w.Write(y); w.Write(z);
            }
            w.Write((ushort)0);
        }
        return ms.ToArray();
    }
}
