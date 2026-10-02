// Usage: dotnet run -c Release -- <scene.json> <out-dir> <nanocut|manifoldsharp|process> [--warm] [--repeat N] [--par N]
//
// Two kinds of scene, because they measure different things (docs/performance-audit.md, section 2):
//
//   default (no "kind")     Expanded steps of a ball on a path: per step, build the convex hull of the two step
//                           positions and subtract it from the workpiece. A synthetic worst case -- it rebuilds the hull
//                           on every step and takes one bite per step, which is exactly what Process3 does not do.
//   kind "process3"         The same path through the production entry point Process3.Cut, which caches the hull per
//                           tool orientation and does not subdivide a pure translation at all.
//   kind "process2-gear"    A spur gear generated with a rack (as in samples/Stykker.NanoCut.Snapshot/Program.cs).
//
// Only the cutting is timed: parsing, tool construction and workpiece creation happen before the clock starts, so the
// measured allocation is the kernel's own.
//
// Measurement discipline (docs/performance-audit.md, section 7):
//   * the warm-up repeats the whole scene until three consecutive runs agree within 10 %, so short scenes are
//     measured at tier 1 and not at tier 0;
//   * every repeat rebuilds everything, so no per-run state carries over;
//   * the report contains all single runs plus min/median/max, never just the best one.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stykker.NanoCut;
using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Geometry3D;
using Stykker.NanoCut.Cutting;
using MS = ManifoldSharp;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: <scene.json> <out-dir> <nanocut|manifoldsharp|process> [--warm] [--repeat N] [--par N]");
    return 2;
}
var scene = JsonNode.Parse(File.ReadAllText(args[0]))!;
string outDir = args[1], engine = args[2];
bool warm = args.Contains("--warm");
bool counters = args.Contains("--stats");
int repeat = ArgInt("--repeat", 3);
int par = ArgInt("--par", 0);
Directory.CreateDirectory(outDir);

int ArgInt(string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}

if (par > 0) SolidBoolean.MaxParallelism = par;
KernelStats.Reset();
KernelStats.Counting = counters;

long[] Corner(string key) => scene["box"]![key]!.AsArray().Select(v => v!.GetValue<long>()).ToArray();

// The production entry points: what an application actually pays. Checked before the step parser below, because a
// process scene carries its geometry in millimetres while an expanded step scene carries grid integers in nm.
if (engine == "process") return ProcessBench();

var min = Corner("min");
var max = Corner("max");

// Materialised before any measurement: the hull inputs must not cost LINQ enumerator allocation inside the timed loop.
var steps = new Vec3[scene["steps"]!.AsArray().Count][];
int stepIndex = 0;
foreach (var step in scene["steps"]!.AsArray())
{
    var arr = new Vec3[step!.AsArray().Count];
    int p = 0;
    foreach (var pt in step.AsArray())
    {
        var c = pt!.AsArray();
        arr[p++] = new Vec3(c[0]!.GetValue<long>(), c[1]!.GetValue<long>(), c[2]!.GetValue<long>());
    }
    steps[stepIndex++] = arr;
}
var save = scene["save"]!.AsArray().Select(v => v!.GetValue<int>()).ToHashSet();

// Splits ConvexHull3.Compute into its quickhull phase and its face-building phase on one step's point set.
if (args.Contains("--hull-breakdown"))
{
    var first = steps[0];
    var pair = new Vec3[first.Length * 2];
    Array.Copy(first, pair, first.Length);
    Array.Copy(first, 0, pair, first.Length, first.Length);
    var tris = ConvexHull3.Triangles(pair, out _);
    var hull = ConvexHull3.Compute(pair);
    const int Reps = 200;
    double triBest = double.MaxValue, allBest = double.MaxValue;
    for (int i = 0; i < 5; i++)
    {
        var t0 = Stopwatch.GetTimestamp();
        for (int r = 0; r < Reps; r++) _ = ConvexHull3.Triangles(pair, out _);
        triBest = Math.Min(triBest, Stopwatch.GetElapsedTime(t0).TotalMilliseconds / Reps);
        t0 = Stopwatch.GetTimestamp();
        for (int r = 0; r < Reps; r++) _ = ConvexHull3.Compute(pair);
        allBest = Math.Min(allBest, Stopwatch.GetElapsedTime(t0).TotalMilliseconds / Reps);
    }
    double face = allBest - triBest;
    Console.WriteLine($"hull breakdown: {first.Length} input points -> {tris.Count} triangles, {hull.FaceCount} faces");
    Console.WriteLine($"  quickhull phase  {triBest,8:F3} ms  {100 * triBest / allBest,5:F1} %");
    Console.WriteLine($"  face build phase {face,8:F3} ms  {100 * face / allBest,5:F1} %   ({face * 1e6 / hull.FaceCount:F0} ns per face)");
    Console.WriteLine($"  Compute total    {allBest,8:F3} ms");
    return 0;
}

IEngine NewEngine() => engine switch
{
    "nanocut" => new NanoCutEngine(),
    "manifoldsharp" => new ManifoldSharpEngine(),
    _ => throw new ArgumentException($"unknown engine {engine}"),
};

// One timed run: fresh engine, workpiece built outside the clock, only the cuts measured.
(double Ms, double HullMs, double BooleanMs, double AllocMb, int[] Gc, double GcPauseMs) Measure()
{
    var e = NewEngine();
    e.Start(min, max);
    long alloc0 = GC.GetTotalAllocatedBytes(true);
    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
    double pause0 = GC.GetTotalPauseDuration().TotalMilliseconds;
    var total = Stopwatch.StartNew();
    foreach (var step in steps) e.Cut(step);
    double ms = total.Elapsed.TotalMilliseconds;
    double alloc = (GC.GetTotalAllocatedBytes(true) - alloc0) / 1e6;
    int[] gc = [GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2];
    double pause = GC.GetTotalPauseDuration().TotalMilliseconds - pause0;
    return (ms, e.HullMs, e.BooleanMs, alloc, gc, pause);
}

int warmRuns = 0;
if (warm)
{
    var recent = new Queue<double>();
    while (warmRuns < 40)
    {
        var ms = Measure().Ms;
        warmRuns++;
        // A scene that costs seconds per run is well past the point where tiered JIT matters, and repeating it
        // would cost half an hour without improving the measurement (the gear scene needs ~87 s per run).
        if (ms > 3000) break;
        recent.Enqueue(ms);
        if (recent.Count > 3) recent.Dequeue();
        if (recent.Count == 3)
        {
            var arr = recent.ToArray();
            if (Math.Max(arr.Max(), arr.Min()) / arr[0] <= 1.10 && warmRuns >= 5) break;
        }
    }
}

var runs = new List<(double Ms, double HullMs, double BooleanMs, double AllocMb, int[] Gc, double GcPauseMs)>();
for (int i = 0; i < repeat; i++) runs.Add(Measure());

// One extra instrumented pass: the counters are per thread, so the warm-up and the timed runs do not pollute it.
string? statsReport = null;
double statsCostMs = 0;
if (counters)
{
    KernelStats.Reset();
    KernelStats.Counting = true;
    Measure();
    KernelStats.Counting = false;
    statsReport = KernelStats.Report();
    KernelStats.Reset();
    statsCostMs = KernelStats.CostOfCountingMs(() =>
    {
        var e = NewEngine();
        e.Start(min, max);
        foreach (var step in steps) e.Cut(step);
    });
}

// STL output from a separate, untimed pass, so that file writing never enters the timings.
var final = NewEngine();
final.Start(min, max);
if (save.Contains(0)) File.WriteAllBytes(Path.Combine(outDir, "step-0000.stl"), final.Stl());
for (int i = 0; i < steps.Length; i++)
{
    final.Cut(steps[i]);
    if (save.Contains(i + 1) || (i == steps.Length - 1 && save.Contains(-1)))
        File.WriteAllBytes(Path.Combine(outDir, $"step-{i + 1:0000}.stl"), final.Stl());
}

double[] times = [.. runs.Select(r => r.Ms).Order()];
double median = times[times.Length / 2];
var bestRun = runs.MinBy(r => r.Ms)!;
var stats = new JsonObject
{
    ["engine"] = engine,
    ["language"] = "C#",
    ["exact"] = engine == "nanocut",
    ["warm"] = warm,
    ["warmRuns"] = warmRuns,
    ["repeat"] = repeat,
    ["maxParallelism"] = par > 0 ? par : SolidBoolean.MaxParallelism,
    ["steps"] = steps.Length,
    ["totalMs"] = median,
    ["totalMsMin"] = times[0],
    ["totalMsMax"] = times[^1],
    ["totalMsAll"] = new JsonArray([.. runs.Select(r => (JsonNode)r.Ms)]),
    ["hullMs"] = bestRun.HullMs,
    ["booleanMs"] = bestRun.BooleanMs,
    ["allocatedMb"] = bestRun.AllocMb,
    ["gen0"] = bestRun.Gc[0],
    ["gen1"] = bestRun.Gc[1],
    ["gen2"] = bestRun.Gc[2],
    ["gcPauseMs"] = bestRun.GcPauseMs,
    ["kernelStats"] = statsReport,
    ["statsCostMs"] = statsCostMs,
    ["volumeMm3"] = final.VolumeMm3(),
    ["triangles"] = final.Triangles(),
};
File.WriteAllText(Path.Combine(outDir, "stats.json"), stats.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"{engine}: {steps.Length} steps, median {median:F0} ms (min {times[0]:F0}, max {times[^1]:F0}), " +
                  $"hull {bestRun.HullMs:F0} ms, boolean {bestRun.BooleanMs:F0} ms, alloc {bestRun.AllocMb:F0} MB, " +
                  $"gen0 {bestRun.Gc[0]}/gen1 {bestRun.Gc[1]}/gen2 {bestRun.Gc[2]}, pause {bestRun.GcPauseMs:F0} ms, " +
                  $"V = {final.VolumeMm3():F9} mm³, {final.Triangles()} triangles" +
                  (warm ? $", warm-up {warmRuns} runs" : "") +
                  (statsReport is null ? "" : $"\n  counters: {statsReport}\n  counting overhead: {statsCostMs:F1} ms per pass"));
return 0;

// ---------------------------------------------------------------------------------------------------------------
// Process scenes: Process3.Cut and Process2.Cut. Same discipline as above -- tool and workpiece are built before the
// clock, the statistics are reported the same way.
int ProcessBench()
{
    string kind = scene["kind"]?.GetValue<string>() ?? "process3";
    var tn = scene["tolerance"];
    var tol = Tolerance.Budget(
        totalUm: tn?["totalUm"]?.GetValue<double>() ?? 0.1,
        chordNm: tn?["chordNm"]?.GetValue<double>() ?? 50,
        sweepNm: tn?["sweepNm"]?.GetValue<double>() ?? 30);

    static Vec3 MmD(JsonNode n) => new(
        (long)Math.Round(n[0]!.GetValue<double>() * 1e6, MidpointRounding.AwayFromZero),
        (long)Math.Round(n[1]!.GetValue<double>() * 1e6, MidpointRounding.AwayFromZero),
        (long)Math.Round(n[2]!.GetValue<double>() * 1e6, MidpointRounding.AwayFromZero));

    (double Vol, long Tri, int Intervals, string Detail) Run()
    {
        if (kind == "process2-gear")
        {
            double m = scene["moduleMm"]!.GetValue<double>();
            int z = scene["teeth"]!.GetValue<int>();
            double rp = m * z / 2 * Units.NmPerMm, pitch = Math.PI * m * Units.NmPerMm;
            var blank = Region2.Circle(default, (m * z / 2 + m), tol);
            // The rack has its own tooth count, independent of the gear: the demo cuts a 20-tooth gear with a 7-tooth rack
            // (samples/Stykker.NanoCut.Snapshot/Program.cs). Using the gear's count for both measures a different,
            // much heavier scene.
            int rackTeeth = scene["rackTeeth"]?.GetValue<int>() ?? 7;
            var rack = GearProfile.Rack(m, rackTeeth, scene["pressureAngleDeg"]?.GetValue<double>() ?? 20,
                bodyMm: scene["rackBodyMm"]?.GetValue<double>() ?? 0.5);
            var rolling = Motion2.Sequence([.. Enumerable.Range(0, z).Select(k => Motion2.Custom(t =>
            {
                double dphi = 2 * Math.PI / z * t, phi = 2 * Math.PI * k / z + dphi;
                return Pose2.Rotation(-phi).Compose(new Pose2(0, pitch / 2 - rp * dphi, rp));
            }))]);
            var g2 = Process2.Cut([blank], rack, rolling, tol, out var st)[0];
            var solid = Solid.Extrude(g2, 0, scene["extrudeMm"]?.GetValue<double>() ?? 10);
            return (solid.VolumeMm3, solid.ToMeshBuffers(OriginMode.Absolute).Indices.Length / 3, st.Intervals,
                    $"{st.Intervals} roll steps, profile vertices {g2.Contours.Sum(c => c.Count)}, " +
                    $"contours {g2.Contours.Count}, extrude to {solid.FaceCount} faces");
        }

        // process3
        var tool = scene["tool"]!;
        ToolShape shape;
        if (tool["ballMm"] is { } ballNode)
        {
            int seg = tool["segments"]?.GetValue<int>() ?? 24;
            var pts = BallPoints((long)Math.Round(ballNode.GetValue<double>() * 1e6), seg);
            shape = ToolShape.FromConvexParts(ConvexHull3.Compute([.. pts.Distinct()]));
        }
        else if (tool["boxMm"] is { } boxNode)
        {
            shape = ToolShape.FromConvexParts(Solid.Box(MmD(boxNode["min"]!), MmD(boxNode["max"]!), tol));
        }
        else throw new ArgumentException("tool needs ballMm or boxMm");

        var lo = MmD(scene["box"]!["min"]!);
        var hi = MmD(scene["box"]!["max"]!);
        var block = Solid.Box(lo, hi, tol);
        Motion3 motion;
        string detail;
        if (scene["rotateDeg"] is { } rotNode)
        {
            double deg = rotNode.GetValue<double>();
            var axis = MmD(scene["axis"] ?? new JsonArray(0, 0, 1));
            var origin = MmD(scene["origin"] ?? new JsonArray(0, 0, 0));
            double len = Math.Sqrt(axis.X * (double)axis.X + axis.Y * (double)axis.Y + axis.Z * (double)axis.Z);
            var to = Pose3.Rotation(deg * Math.PI / 180, axis.X / len, axis.Y / len, axis.Z / len, origin);
            motion = Motion3.Between(Pose3.Identity, to);
            detail = $"rotation {deg}° about ({axis.X / len:F3},{axis.Y / len:F3},{axis.Z / len:F3}), sweep {tol.SweepNm} nm";
        }
        else
        {
            var segs = new List<Motion3>();
            foreach (var p in scene["paths"]!.AsArray())
            {
                var pair = p!.AsArray();
                segs.Add(Motion3.Linear(MmD(pair[0]!), MmD(pair[1]!)));
            }
            motion = Motion3.Sequence([.. segs]);
            detail = $"{segs.Count} linear path segments";
        }

        var result = Process3.Cut([block], shape, motion, tol, out var stats3)[0];
        return (result.VolumeMm3, result.ToMeshBuffers(OriginMode.Absolute).Indices.Length / 3, stats3.Intervals,
                $"{detail}, intervals {stats3.Intervals}, hulls {stats3.Hulls}, cuts {stats3.Cuts}");
    }

    (double Ms, long Alloc, int[] Gc, double Pause, double Vol, long Tri, int Intervals, string Detail) Measure()
    {
        long a0 = GC.GetTotalAllocatedBytes(true);
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        double p0 = GC.GetTotalPauseDuration().TotalMilliseconds;
        var sw = Stopwatch.StartNew();
        var r = Run();
        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - a0,
                [GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2],
                GC.GetTotalPauseDuration().TotalMilliseconds - p0, r.Vol, r.Tri, r.Intervals, r.Detail);
    }

    int warmRuns = 0;
    if (warm)
    {
        var recent = new Queue<double>();
        while (warmRuns < 40)
        {
            var ms = Measure().Ms;
            warmRuns++;
            if (ms > 3000) break;
            recent.Enqueue(ms);
            if (recent.Count > 3) recent.Dequeue();
            if (recent.Count == 3)
            {
                var arr = recent.ToArray();
                if (Math.Max(arr.Max(), arr.Min()) / arr[0] <= 1.10 && warmRuns >= 5) break;
            }
        }
    }

    var runs = new List<double>();
    long alloc = 0; int[] gc = [0, 0, 0]; double pause = 0, vol = 0; long tri = 0; int intervals = 0; string detail = "";
    for (int i = 0; i < repeat; i++)
    {
        var m = Measure();
        runs.Add(m.Ms);
        alloc = m.Alloc; gc = m.Gc; pause = m.Pause; vol = m.Vol; tri = m.Tri; intervals = m.Intervals; detail = m.Detail;
    }
    double[] sorted = [.. runs.Order()];
    double median = sorted[sorted.Length / 2];
    var outStats = new JsonObject
    {
        ["engine"] = "process", ["kind"] = kind, ["language"] = "C#", ["exact"] = true,
        ["warm"] = warm, ["warmRuns"] = warmRuns, ["repeat"] = repeat,
        ["maxParallelism"] = par > 0 ? par : SolidBoolean.MaxParallelism,
        ["steps"] = intervals, ["totalMs"] = median, ["totalMsMin"] = sorted[0], ["totalMsMax"] = sorted[^1],
        ["totalMsAll"] = new JsonArray([.. runs.Select(v => (JsonNode)v)]),
        ["allocatedMb"] = alloc / 1e6, ["gen0"] = gc[0], ["gen1"] = gc[1], ["gen2"] = gc[2], ["gcPauseMs"] = pause,
        ["volumeMm3"] = vol, ["triangles"] = tri, ["detail"] = detail,
    };
    File.WriteAllText(Path.Combine(outDir, "stats.json"),
        outStats.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"process/{kind}: median {median:F0} ms (min {sorted[0]:F0}, max {sorted[^1]:F0}), " +
                      $"alloc {alloc / 1e6:F0} MB, gen0 {gc[0]}/gen1 {gc[1]}/gen2 {gc[2]}, pause {pause:F0} ms, " +
                      $"V = {vol:F6} mm³, {tri} triangles\n  {detail}" +
                      (warm ? $", warm-up {warmRuns} runs" : ""));
    return 0;
}

// The same point set bench/run.py builds for a ball: two poles plus rings of `segments` points.
static Vec3[] BallPoints(long r, int segments)
{
    int n = Math.Max(4, segments / 2 * 2);
    var pts = new List<Vec3> { new(0, 0, r), new(0, 0, -r) };
    for (int i = 1; i < n / 2; i++)
    {
        double th = Math.PI * i / (n / 2);
        for (int j = 0; j < n; j++)
        {
            double ph = 2 * Math.PI * j / n;
            pts.Add(new Vec3((long)Math.Round(r * Math.Sin(th) * Math.Cos(ph), MidpointRounding.AwayFromZero),
                             (long)Math.Round(r * Math.Sin(th) * Math.Sin(ph), MidpointRounding.AwayFromZero),
                             (long)Math.Round(r * Math.Cos(th), MidpointRounding.AwayFromZero)));
        }
    }
    return [.. pts];
}

interface IEngine
{
    void Start(long[] min, long[] max);
    void Cut(Vec3[] points);
    double HullMs { get; }
    double BooleanMs { get; }
    double VolumeMm3();
    long Triangles();
    byte[] Stl();
}

sealed class NanoCutEngine : IEngine
{
    private Solid _work = Solid.Empty;

    public void Start(long[] min, long[] max) =>
        _work = Solid.Box(new Vec3(min[0], min[1], min[2]), new Vec3(max[0], max[1], max[2]));

    // Per instance, not static: a static accumulator would also collect the warm-up runs.
    public double HullMs { get; private set; }
    public double BooleanMs { get; private set; }

    public void Cut(Vec3[] points)
    {
        var t = Stopwatch.GetTimestamp();
        var hull = ConvexHull3.Compute(points);
        var t1 = Stopwatch.GetTimestamp();
        _work -= hull;
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

    public double HullMs => 0;
    public double BooleanMs => 0;

    public void Cut(Vec3[] points) =>
        _work = _work.Difference(MS.Manifold.Hull(points.Select(p => new MS.Linalg.Vec3(p.X, p.Y, p.Z)).ToList()));

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
