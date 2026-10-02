// Usage: dotnet run -c Release -- <expanded.json> <out-dir> <nanocut|manifoldsharp> [--warm] [--repeat N] [--par N]
//
// Runs the steps of an expanded bench scene (workpiece box minus the convex hull of each step's points, in order) and
// writes stats.json plus binary STL files of the states listed in "save". Only the cutting is timed -- the scene is
// parsed, the hull inputs materialised and the workpiece created before the clock starts, so the measured allocation
// is the kernel's own.
//
// Measurement discipline (see docs/performance-audit.md, section 7):
//   * the warm-up repeats the whole scene until two consecutive runs are within 10 %, so that short scenes are also
//     measured at tier 1 and not at tier 0;
//   * every repeat runs on a fresh engine instance, so no per-run state carries over;
//   * the report contains all single runs plus min/median/max, never just the best one.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stykker.NanoCut;
using Stykker.NanoCut.Geometry3D;
using MS = ManifoldSharp;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: <expanded.json> <out-dir> <nanocut|manifoldsharp> [--warm] [--repeat N] [--par N]");
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

long[] Corner(string key) => scene["box"]![key]!.AsArray().Select(v => v!.GetValue<long>()).ToArray();
var min = Corner("min");
var max = Corner("max");

// Materialised before any measurement: the hull inputs must not cost LINQ enumerator allocs inside the timed loop.
var steps = new Vec3[scene["steps"]!.AsArray().Count][];
int s = 0;
foreach (var step in scene["steps"]!.AsArray())
{
    var arr = new Vec3[step!.AsArray().Count];
    int p = 0;
    foreach (var pt in step.AsArray())
    {
        var c = pt!.AsArray();
        arr[p++] = new Vec3(c[0]!.GetValue<long>(), c[1]!.GetValue<long>(), c[2]!.GetValue<long>());
    }
    steps[s++] = arr;
}
var save = scene["save"]!.AsArray().Select(v => v!.GetValue<int>()).ToHashSet();

// --hull-breakdown splits ConvexHull3.Compute into its quickhull phase and the phase that turns triangles into
// coplanar polygon faces, on one step's point set. Which of the two dominates decides where the hull work goes.
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

// MaxParallelism is process-global; set it once so every run in this process sees the same setting.
if (par > 0) SolidBoolean.MaxParallelism = par;

// Counters are off for the timings and on only for a dedicated instrumentation pass.
KernelStats.Reset();
KernelStats.Counting = counters;

IEngine NewEngine() => engine switch
{
    "nanocut" => new NanoCutEngine(),
    "manifoldsharp" => new ManifoldSharpEngine(),
    _ => throw new ArgumentException($"unknown engine {engine}"),
};

// One timed run: fresh engine, workpiece created outside the clock, only the cuts measured.
(double Ms, double HullMs, double BooleanMs, double AllocMb, int[] Gc, double GcPauseMs, int MaxStepMs) Measure()
{
    var e = NewEngine();
    e.Start(min, max);

    long alloc0 = GC.GetTotalAllocatedBytes(true);
    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
    double pause0 = GC.GetTotalPauseDuration().TotalMilliseconds;

    var stepMs = new double[steps.Length];
    var total = Stopwatch.StartNew();
    for (int i = 0; i < steps.Length; i++)
    {
        var sw = Stopwatch.StartNew();
        e.Cut(steps[i]);
        stepMs[i] = sw.Elapsed.TotalMilliseconds;
    }
    double ms = total.Elapsed.TotalMilliseconds;

    double alloc = (GC.GetTotalAllocatedBytes(true) - alloc0) / 1e6;
    int[] gc = [GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2];
    double pause = GC.GetTotalPauseDuration().TotalMilliseconds - pause0;
    return (ms, e.HullMs, e.BooleanMs, alloc, gc, pause, stepMs.Length > 0 ? (int)stepMs.Max() : 0);
}

// Warm-up: at least 5 passes so short scenes also reach tier 1, then repeat until three consecutive runs lie
// within 10 % of each other -- a single early-slow pass must not stop the warm-up.
int warmRuns = 0;
if (warm)
{
    const int MinWarmRuns = 5, MaxWarmRuns = 40;
    var recent = new Queue<double>();
    while (warmRuns < MaxWarmRuns)
    {
        var (ms, _, _, _, _, _, _) = Measure();
        warmRuns++;
        recent.Enqueue(ms);
        if (recent.Count > 3) recent.Dequeue();
        if (recent.Count == 3)
        {
            var arr = recent.ToArray();
            if (Math.Max(arr.Max(), arr.Min()) / arr[0] <= 1.10 && warmRuns >= MinWarmRuns) break;
        }
    }
}

var runs = new List<(double Ms, double HullMs, double BooleanMs, double AllocMb, int[] Gc, double GcPauseMs, int MaxStepMs)>();
var final = NewEngine();
for (int i = 0; i < repeat; i++) runs.Add(Measure());

// One extra instrumented pass: kernel counters are per thread, so the warm-up and the timed runs above do not pollute
// this. It also measures what the counting itself costs, so it can be compared against the clean timings.
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
    KernelStats.Counting = counters;
}

// STL output from a separate, untimed pass so that file writing never enters the timings.
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
    ["maxStepMs"] = bestRun.MaxStepMs,
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