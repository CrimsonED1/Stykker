// Usage: dotnet GpuBench.dll <expanded.json> [options]
// Measures the Z-map preview backends on an expanded bench scene (bench/out/<scene>/expanded.json from bench/run.py).
//
//   --grids 128,256,1024     cells in x; cells in y follow the aspect ratio of the box
//   --backends cpu,cuda      which backends to run
//   --dexel K                measure the dexel preview (K intervals per column) instead of the Z-map
//   --repeat 3               runs per cell (and per query call), the best wall time is reported
//   --steps N                only the first N steps (0 = all)
//   --chunk N                apply the steps in calls of N steps instead of one call (N = 1 means one launch per step)
//   --reference <mm3>        remaining volume of the exact kernel, for the deviation column
//   --diff                   compare every backend against the cpu one at the same resolution
//   --stl <path>             write the height field of the finest grid as binary STL
//   --out <dir>              write zmap-results.json and zmap-results.md into <dir>
//   --cold                   time the very first call instead of warming up on a separate map
//   --queries                measure the batch queries at the finest grid: height sampling, the same sampling from
//                            a point set the backend keeps, tool probing and the read-back into pinned and into
//                            pageable memory
//   --points N               sample points for the query phase (default 1000000)
//   --no-pin                 let the CUDA backend read back into pageable memory, to measure what pinning is worth
//
// Times are split the way the plan asks for: kernel (device kernel or parallel host loop), upload (host to device),
// download (device to host), first call (CUDA context creation and device allocation) and wall (the whole call).
// By default one warm-up run on a separate map happens first, so JIT compilation, context creation and the device
// allocation are not measured as time per step.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stykker.NanoCut.Gpu;

var inv = CultureInfo.InvariantCulture;
var opt = Options.Parse(args);
var scene = ExpandedScene.Load(opt.ExpandedJson);
string sceneName = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(opt.ExpandedJson)))
                   ?? Path.GetFileNameWithoutExtension(opt.ExpandedJson);

BallStep[] steps = opt.StepLimit > 0 && opt.StepLimit < scene.Steps.Count
    ? [.. scene.Steps.Take(opt.StepLimit)]
    : [.. scene.Steps];

Console.WriteLine($"scene {sceneName}: box {scene.BoxMinMm.X:F0},{scene.BoxMinMm.Y:F0},{scene.BoxMinMm.Z:F0} .. " +
                  $"{scene.BoxMaxMm.X:F0},{scene.BoxMaxMm.Y:F0},{scene.BoxMaxMm.Z:F0} mm, " +
                  $"{scene.Steps.Count} steps (using {steps.Length}), ball r = {scene.RadiusMm:F3} mm, " +
                  $"{scene.PointsPerStep} points per step");
// One-off cost, paid before anything is measured: loading nanocut_gpu and letting the CUDA runtime initialise.
// cudaGetDeviceProperties already creates the context, so nc_gpu_init afterwards costs almost nothing and the first
// kernel launch is not the slow one either. Reporting this separately keeps it out of the time per step.
var probe = Stopwatch.StartNew();
bool cudaAvailable = CudaRuntime.IsAvailable;
string? cudaReason = CudaRuntime.UnavailableReason;
var devices = CudaRuntime.Devices;
probe.Stop();

Console.WriteLine($"stock {scene.BoxVolumeMm3:F3} mm3, {Environment.ProcessorCount} logical processors, " +
                  (opt.Cold ? "cold (no warm-up)" : "warm") +
                  (opt.Chunk > 0 ? $", {opt.Chunk} step(s) per call" : ", one call for all steps"));
foreach (var d in devices)
    Console.WriteLine($"cuda device {d.Index}: {d.Name}, sm_{d.Major}{d.Minor}, {d.MemoryBytes / (1024 * 1024)} MiB");
Console.WriteLine(cudaAvailable
    ? $"cuda probe (library load + runtime init, once per process): {probe.Elapsed.TotalMilliseconds:F1} ms"
    : $"cuda unavailable after {probe.Elapsed.TotalMilliseconds:F1} ms: {cudaReason}");
Console.WriteLine();

var rows = new List<Row>();
var cpuHeights = new Dictionary<int, float[]>();

if (opt.Dexel > 0)
{
    // Dexel preview instead of the Z-map: up to K material intervals per column. The steps are applied in one call
    // with the intervals left on the device, and the volume is reduced there, so no read-back of the (large) interval
    // array enters the time.
    foreach (int cellsX in opt.Grids)
    {
        int cellsY = CellsY(cellsX);
        foreach (string name in opt.Backends)
        {
            if (Make(name, opt) is not IDexelBackend backend) { Console.Error.WriteLine($"unknown backend {name}"); return 1; }
            if (!backend.IsAvailable) { Console.WriteLine($"{name,-5} {cellsX,5} x {cellsY,-5} skipped: {backend.UnavailableReason}"); continue; }
            double bestWall = double.MaxValue, bestKernel = 0, remaining = 0;
            long overflows = 0;
            for (int run = 0; run < opt.Repeat; run++)
            {
                if (!opt.Cold) DexelMap.FromScene(scene, cellsX, cellsY, opt.Dexel, backend).ApplySteps(steps, ZMapReadBack.Never);
                var map = DexelMap.FromScene(scene, cellsX, cellsY, opt.Dexel, backend);
                var sw = Stopwatch.StartNew();
                map.ApplySteps(steps, ZMapReadBack.Never);
                double removed = map.BackendRemovedVolumeMm3;
                sw.Stop();
                if (sw.Elapsed.TotalMilliseconds < bestWall)
                {
                    bestWall = sw.Elapsed.TotalMilliseconds;
                    bestKernel = map.TotalTiming.KernelMs;
                    remaining = map.BoxVolumeMm3 - removed;
                    overflows = map.Overflows;
                }
            }
            Console.WriteLine($"dexel {name,-5} k={opt.Dexel} {cellsX,5} x {cellsY,-5} wall {bestWall,9:F1} ms  kernel {bestKernel,9:F1} ms  " +
                              $"remaining {remaining,14:F6} mm3" +
                              (opt.Reference is { } re ? $"  ({(remaining - re) / re * 100,+7:F3} %)" : "") +
                              $"  overflows {overflows}");
        }
    }
    if (opt.Reference is { } exactRef)
        Console.WriteLine($"\nreference remaining volume {exactRef.ToString("F9", inv)} mm3");
    return 0;
}

foreach (int cellsX in opt.Grids)
{
    int cellsY = CellsY(cellsX);

    foreach (string name in opt.Backends)
    {
        IZMapBackend? backend = Make(name, opt);
        if (backend is null)
        {
            Console.Error.WriteLine($"unknown backend {name}");
            return 1;
        }
        if (!backend.IsAvailable)
        {
            Console.WriteLine($"{name,-5} {cellsX,5} x {cellsY,-5} skipped: {backend.UnavailableReason}");
            continue;
        }

        Row? best = null;
        for (int run = 0; run < opt.Repeat; run++)
        {
            if (!opt.Cold)
            {
                var warm = ZMap.FromScene(scene, cellsX, cellsY, backend);
                Runner.Apply(warm, steps, opt.Chunk);
            }
            var map = ZMap.FromScene(scene, cellsX, cellsY, backend);
            Runner.Apply(map, steps, opt.Chunk);
            var row = new Row(name, cellsX, cellsY, steps.Length, map.TotalTiming, map.RemovedVolumeMm3,
                map.RemainingVolumeMm3, opt.Diff ? map.Heights : null);
            if (best is null || row.Timing.WallMs < best.Timing.WallMs) best = row;
        }

        if (best!.Heights is { } heights)
        {
            if (name == "cpu") cpuHeights[cellsX] = heights;
            else if (cpuHeights.TryGetValue(cellsX, out float[]? reference) && reference.Length == heights.Length)
            {
                float worst = 0;
                for (int i = 0; i < reference.Length; i++)
                    worst = Math.Max(worst, Math.Abs(reference[i] - heights[i]));
                best.MaxHeightDifferenceMm = worst;
            }
        }

        rows.Add(best);
        Console.WriteLine($"{best.Backend,-5} {best.CellsX,5} x {best.CellsY,-5} " +
                          $"wall {best.Timing.WallMs,9:F1} ms  kernel {best.Timing.KernelMs,9:F1} ms  " +
                          $"up {best.Timing.UploadMs,7:F2}  down {best.Timing.DownloadMs,7:F2}  " +
                          $"first {best.Timing.FirstCallMs,8:F1}  per step {best.Timing.KernelMs / best.Steps,7:F4} ms  " +
                          $"remaining {best.RemainingMm3,14:F6} mm3" +
                          (opt.Reference is { } re ? $"  ({(best.RemainingMm3 - re) / re * 100,+7:F3} %)" : "") +
                          (best.MaxHeightDifferenceMm is { } dh ? $"  max dh {dh:E2} mm" : ""));
    }
}

if (opt.Reference is { } exact)
    Console.WriteLine($"\nreference remaining volume {exact.ToString("F9", inv)} mm3, " +
                      $"stock {scene.BoxVolumeMm3.ToString("F3", inv)} mm3");

if (opt.Queries)
{
    Console.WriteLine();
    int cellsX = opt.Grids[^1];
    int cellsY = CellsY(cellsX);
    var samplePoints = Queries.Points(scene, opt.Points);
    var probePoses = new ToolPose[steps.Length];
    for (int i = 0; i < steps.Length; i++) probePoses[i] = ToolPose.From(steps[i]);
    Console.WriteLine($"queries at {cellsX} x {cellsY}: {samplePoints.Length} points to sample, " +
                      $"{probePoses.Length} poses to probe");

    float[] referenceHeights = [];
    var referenceDepths = new float[probePoses.Length];
    foreach (string name in opt.Backends)
    {
        IZMapBackend? backend = Make(name, opt);
        if (backend is null || !backend.IsAvailable) continue;

        var map = ZMap.FromScene(scene, cellsX, cellsY, backend);
        Runner.Apply(map, steps, opt.Chunk);
        if (map.Backend is not IZMapQueryBackend queries)
        {
            Console.WriteLine($"{name,-5} skipped: the backend cannot answer queries");
            continue;
        }

        // Same warm-up as the preview phase: a single cold query is dominated by one-off costs (the query buffers
        // and the first touch of the pageable point and result arrays, ~1000 page faults for a 4 MB result) and
        // says nothing about the steady state. Report the best of --repeat, and keep the cold number beside it.
        //
        // The stopwatch around the call is a second number and not a duplicate: a backend-reported wall time only
        // covers what the backend considers the call. The CPU backend times its parallel loop alone, with the 16 MB
        // staging copy of a span and the answer copy outside it, so "call" is what the caller actually waits for and
        // the only figure that can be compared with the CUDA one, whose wall time does include its transfers.
        var got = new float[samplePoints.Length];
        var depths = new float[probePoses.Length];
        var call = Stopwatch.StartNew();
        ZMapTiming sampleCold = queries.SampleHeights(map, samplePoints, got);
        call.Stop();
        double sampleCall = call.Elapsed.TotalMilliseconds;
        ZMapTiming probeCold = queries.ProbeMaterial(map, probePoses, depths);
        ZMapTiming sampleTiming = sampleCold;
        ZMapTiming probeTiming = probeCold;
        for (int run = 1; run < opt.Repeat; run++)
        {
            call.Restart();
            var s = queries.SampleHeights(map, samplePoints, got);
            call.Stop();
            sampleCall = Math.Min(sampleCall, call.Elapsed.TotalMilliseconds);
            var p = queries.ProbeMaterial(map, probePoses, depths);
            if (s.WallMs < sampleTiming.WallMs) sampleTiming = s;
            if (p.WallMs < probeTiming.WallMs) probeTiming = p;
        }
        if (name == "cpu") referenceHeights = (float[])got.Clone();

        float worstHeight = 0, worstDepth = 0;
        bool haveHeightReference = name != "cpu" && referenceHeights.Length == got.Length;
        bool haveDepthReference = name != "cpu";
        if (haveHeightReference)
            for (int i = 0; i < got.Length; i++)
                worstHeight = Math.Max(worstHeight, Math.Abs(referenceHeights[i] - got[i]));
        if (name != "cpu")
            for (int i = 0; i < depths.Length; i++)
                worstDepth = Math.Max(worstDepth, Math.Abs(referenceDepths[i] - depths[i]));
        else referenceDepths = depths;

        Console.WriteLine($"{name,-5} sample {samplePoints.Length} points: kernel {sampleTiming.KernelMs,8:F3} ms  " +
                          $"up {sampleTiming.UploadMs,7:F3}  down {sampleTiming.DownloadMs,7:F3}  " +
                          $"wall {sampleTiming.WallMs,8:F3} ms (call {sampleCall,7:F3})" +
                          ColdSuffix(sampleTiming, sampleCold) +
                          $"  max dh {(haveHeightReference ? worstHeight.ToString("E2", inv) : "-")} mm");
        Console.WriteLine($"{name,-5} probe  {probePoses.Length} poses:  kernel {probeTiming.KernelMs,8:F3} ms  " +
                          $"up {probeTiming.UploadMs,7:F3}  down {probeTiming.DownloadMs,7:F3}  " +
                          $"wall {probeTiming.WallMs,8:F3} ms" + ColdSuffix(probeTiming, probeCold) +
                          $"  max dd {(haveDepthReference ? worstDepth.ToString("E2", inv) : "-")} mm");

        // The query a viewer runs after every batch of steps: the same points, but already where the backend answers
        // from, so only the answer travels. The one-time upload is reported next to it, because that is what the
        // caller has to weigh against the number of times it will ask.
        var keptWatch = Stopwatch.StartNew();
        using PointSet kept = queries.UploadPoints(samplePoints);
        keptWatch.Stop();
        var keptHeights = new float[samplePoints.Length];
        call.Restart();
        ZMapTiming keptTiming = queries.SampleHeights(map, kept, keptHeights);
        call.Stop();
        double keptCall = call.Elapsed.TotalMilliseconds;
        for (int run = 1; run < opt.Repeat; run++)
        {
            call.Restart();
            ZMapTiming k = queries.SampleHeights(map, kept, keptHeights);
            call.Stop();
            keptCall = Math.Min(keptCall, call.Elapsed.TotalMilliseconds);
            if (k.WallMs < keptTiming.WallMs) keptTiming = k;
        }
        float worstKept = 0;
        if (haveHeightReference)
            for (int i = 0; i < keptHeights.Length; i++)
                worstKept = Math.Max(worstKept, Math.Abs(referenceHeights[i] - keptHeights[i]));
        Console.WriteLine($"{name,-5} sample {samplePoints.Length} points from a kept set: " +
                          $"kernel {keptTiming.KernelMs,8:F3} ms  up {keptTiming.UploadMs,7:F3}  " +
                          $"down {keptTiming.DownloadMs,7:F3}  wall {keptTiming.WallMs,8:F3} ms " +
                          $"(call {keptCall,7:F3})  set uploaded once in {keptWatch.Elapsed.TotalMilliseconds,7:F3} ms  " +
                          $"max dh {(haveHeightReference ? worstKept.ToString("E2", inv) : "-")} mm");

        // Worth printing only when the warm-up actually bought something.
        static string ColdSuffix(ZMapTiming best, ZMapTiming cold) =>
            best.WallMs < cold.WallMs * 0.98 ? $"  (cold {cold.WallMs,7:F3})" : "";

        // What the read-back costs with and without pinned memory, on the same field and the same device, and what
        // a batch costs when the field is left on the device and the volume is reduced there instead.
        if (backend is not CudaBackend || opt.PinnedReadBack is false) continue;
        int bytes = cellsX * cellsY * sizeof(float);
        foreach (bool pin in new[] { true, false })
        {
            var plain = new CudaBackend { PinnedReadBack = pin };
            var plainMap = ZMap.FromScene(scene, cellsX, cellsY, plain);
            Runner.Apply(plainMap, steps, opt.Chunk);
            double best = double.MaxValue;
            for (int run = 0; run < opt.Repeat; run++) best = Math.Min(best, plainMap.ReadHeights());
            Console.WriteLine($"cuda  read-back {(pin ? "pinned  " : "pageable")}: {best,8:F3} ms  " +
                              $"{bytes / best / 1e6,7:F2} GB/s");
        }

        var never = ZMap.FromScene(scene, cellsX, cellsY, backend);
        double neverKernel = double.MaxValue, neverWall = double.MaxValue, volumeMs = double.MaxValue;
        for (int run = 0; run <= opt.Repeat; run++)
        {
            Runner.Apply(never, steps, opt.Chunk, ZMapReadBack.Never);
            if (run == 0) continue;   // the first call also creates the device field
            neverKernel = Math.Min(neverKernel, never.LastTiming.KernelMs);
            neverWall = Math.Min(neverWall, never.LastTiming.WallMs);
            volumeMs = Math.Min(volumeMs, Measure(() => never.BackendRemovedVolumeMm3));
        }
        Console.WriteLine($"cuda  batch without read-back: kernel {neverKernel,7:F3} ms  wall {neverWall,7:F3} ms  " +
                          $"device volume {volumeMs:F4} ms, {never.BackendRemovedVolumeMm3:F6} mm3");
    }
}

if (opt.Stl is { } stlPath)
{
    int cellsX = opt.Grids[^1];
    int cellsY = rows.First(r => r.CellsX == cellsX).CellsY;
    var map = ZMap.FromScene(scene, cellsX, cellsY, CpuBackend.Instance);
    Runner.Apply(map, steps, opt.Chunk);
    string? dir = Path.GetDirectoryName(Path.GetFullPath(stlPath));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    var mesh = map.ToMesh();
    File.WriteAllBytes(stlPath, Report.ToStl(mesh));
    Console.WriteLine($"wrote {stlPath} ({mesh.TriangleCount} triangles, {cellsX} x {cellsY} cells)");
}

if (opt.Out is { } outDir)
{
    Directory.CreateDirectory(outDir);
    var json = new JsonObject
    {
        ["scene"] = sceneName,
        ["steps"] = steps.Length,
        ["boxVolumeMm3"] = scene.BoxVolumeMm3,
        ["referenceRemainingMm3"] = opt.Reference,
        ["logicalProcessors"] = Environment.ProcessorCount,
        ["cold"] = opt.Cold,
        ["chunk"] = opt.Chunk,
        ["cudaAvailable"] = cudaAvailable,
        ["cudaReason"] = cudaReason,
        ["cudaProbeMs"] = probe.Elapsed.TotalMilliseconds,
        ["devices"] = new JsonArray([.. devices.Select(d => (JsonNode)new JsonObject
        {
            ["index"] = d.Index,
            ["name"] = d.Name,
            ["computeCapability"] = d.ComputeCapability,
            ["memoryBytes"] = d.MemoryBytes,
        })]),
        ["rows"] = new JsonArray([.. rows.Select(r => (JsonNode)new JsonObject
        {
            ["backend"] = r.Backend,
            ["cellsX"] = r.CellsX,
            ["cellsY"] = r.CellsY,
            ["steps"] = r.Steps,
            ["kernelMs"] = r.Timing.KernelMs,
            ["uploadMs"] = r.Timing.UploadMs,
            ["downloadMs"] = r.Timing.DownloadMs,
            ["firstCallMs"] = r.Timing.FirstCallMs,
            ["wallMs"] = r.Timing.WallMs,
            ["removedMm3"] = r.RemovedMm3,
            ["remainingMm3"] = r.RemainingMm3,
            ["maxHeightDifferenceMm"] = r.MaxHeightDifferenceMm,
        })]),
    };
    File.WriteAllText(Path.Combine(outDir, "zmap-results.json"),
        json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    File.WriteAllText(Path.Combine(outDir, "zmap-results.md"),
        Report.Markdown(sceneName, scene, steps.Length, rows, opt.Reference), Encoding.UTF8);
    Console.WriteLine($"wrote {Path.Combine(outDir, "zmap-results.json")} and zmap-results.md");
}

return 0;

/// <summary>Cells in y for a given width in x, from the aspect ratio of the box.</summary>
int CellsY(int cellsX)
{
    double aspect = (scene.BoxMaxMm.Y - scene.BoxMinMm.Y) / (scene.BoxMaxMm.X - scene.BoxMinMm.X);
    int cellsY = Math.Max(2, (int)Math.Round(cellsX * aspect));
    return cellsY % 2 == 0 ? cellsY : cellsY + 1;
}

/// <summary>The backend for a name, honouring the pinning option.</summary>
IZMapBackend? Make(string name, Options opt) => name switch
{
    "cpu" => CpuBackend.Instance,
    "cuda" => new CudaBackend { PinnedReadBack = opt.PinnedReadBack ?? true },
    _ => null,
};

/// <summary>Wall time of one call, in milliseconds.</summary>
double Measure(Func<double> call)
{
    var sw = Stopwatch.StartNew();
    call();
    sw.Stop();
    return sw.Elapsed.TotalMilliseconds;
}

sealed record Row(string Backend, int CellsX, int CellsY, int Steps, ZMapTiming Timing,
    double RemovedMm3, double RemainingMm3, float[]? Heights)
{
    public double? MaxHeightDifferenceMm { get; set; }
}

static class Queries
{
    /// <summary>
    /// A regular grid of <paramref name="count"/> points over the box of the scene, in absolute mm, one row per
    /// grid line so that the access pattern of the host loop is the same as the one of the device.
    /// </summary>
    internal static SamplePoint[] Points(ExpandedScene scene, int count)
    {
        double x0 = scene.BoxMinMm.X, x1 = scene.BoxMaxMm.X;
        double y0 = scene.BoxMinMm.Y, y1 = scene.BoxMaxMm.Y;
        int rows = Math.Max(1, (int)Math.Sqrt(count));
        int columns = Math.Max(1, count / rows);
        var points = new SamplePoint[rows * columns];
        int p = 0;
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < columns; i++)
                points[p++] = new SamplePoint(x0 + (x1 - x0) * i / columns, y0 + (y1 - y0) * j / rows);
        return points;
    }
}

static class Runner
{
    /// <summary>
    /// Applies the steps in one call, or in chunks of <paramref name="chunk"/> steps. Chunking is what a page does to
    /// report progress, and it shows the price of one launch per step instead of one for the whole batch.
    /// </summary>
    internal static void Apply(ZMap map, BallStep[] steps, int chunk, ZMapReadBack readBack = ZMapReadBack.Always)
    {
        if (chunk <= 0 || chunk >= steps.Length)
        {
            map.ApplySteps(steps, readBack);
            return;
        }
        for (int i = 0; i < steps.Length; i += chunk)
            map.ApplySteps(steps.AsSpan(i, Math.Min(chunk, steps.Length - i)), readBack);
    }
}

static class Report
{
    internal static string Markdown(string sceneName, ExpandedScene scene, int steps, List<Row> rows, double? reference)
    {
        var inv = CultureInfo.InvariantCulture;
        string N(double v, string format) => v.ToString(format, inv);
        var lines = new List<string>
        {
            $"# Z-map preview of `{sceneName}`",
            "",
            $"{steps} steps, stock {N(scene.BoxVolumeMm3, "F3")} mm³, ball r = {N(scene.RadiusMm, "F3")} mm" +
            (reference is { } exactVolume ? $", exact remaining volume {N(exactVolume, "F9")} mm³." : "."),
            "",
            "| Backend | Cells | Wall (ms) | Kernel (ms) | Upload (ms) | Download (ms) | First call (ms) | " +
            "per step (ms) | Remaining (mm³) | Δ vs exact | max Δh (mm) |",
            "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
        };
        foreach (var r in rows)
        {
            string deviation = reference is { } re ? $"{(r.RemainingMm3 - re) / re * 100:+0.000;-0.000} %" : "-";
            lines.Add($"| {r.Backend} | {r.CellsX} × {r.CellsY} | {N(r.Timing.WallMs, "F1")} | " +
                      $"{N(r.Timing.KernelMs, "F1")} | {N(r.Timing.UploadMs, "F2")} | {N(r.Timing.DownloadMs, "F2")} | " +
                      $"{N(r.Timing.FirstCallMs, "F1")} | {N(r.Timing.KernelMs / r.Steps, "F4")} | " +
                      $"{N(r.RemainingMm3, "F6")} | {deviation} | {r.MaxHeightDifferenceMm?.ToString("E2", inv) ?? "-"} |");
        }
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>Binary STL in absolute mm, the same layout the bench writes.</summary>
    internal static byte[] ToStl(ZMapMesh mesh)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var (ox, oy, oz) = mesh.OriginMm;
        w.Write(new byte[80]);
        w.Write((uint)mesh.TriangleCount);
        for (int t = 0; t < mesh.Indices.Length; t += 3)
        {
            uint a = mesh.Indices[t];
            w.Write(mesh.Normals[3 * a]); w.Write(mesh.Normals[3 * a + 1]); w.Write(mesh.Normals[3 * a + 2]);
            for (int k = 0; k < 3; k++)
            {
                uint i = mesh.Indices[t + k];
                w.Write((float)(mesh.Positions[3 * i] + ox));
                w.Write((float)(mesh.Positions[3 * i + 1] + oy));
                w.Write((float)(mesh.Positions[3 * i + 2] + oz));
            }
            w.Write((ushort)0);
        }
        w.Flush();
        return ms.ToArray();
    }
}

sealed class Options
{
    public required string ExpandedJson { get; init; }
    public required int[] Grids { get; init; }
    public required string[] Backends { get; init; }
    public required int Repeat { get; init; }
    public required int StepLimit { get; init; }
    public required int Chunk { get; init; }
    public required bool Cold { get; init; }
    public required bool Diff { get; init; }
    public required bool Queries { get; init; }
    public required int Points { get; init; }
    public bool? PinnedReadBack { get; init; }
    public double? Reference { get; init; }
    public int Dexel { get; init; }
    public string? Stl { get; init; }
    public string? Out { get; init; }

    public static Options Parse(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
            throw new ArgumentException("usage: <expanded.json> [--grids 128,256,1024] [--backends cpu,cuda] " +
                                        "[--repeat 3] [--steps N] [--chunk N] [--reference mm3] [--diff] " +
                                        "[--stl path] [--out dir] [--cold] [--queries] [--points N] [--no-pin] [--dexel K]");

        string? Value(string name)
        {
            for (int i = 1; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return null;
        }

        return new Options
        {
            ExpandedJson = args[0],
            Grids = (Value("--grids") ?? "128,512,1024").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse).ToArray(),
            Backends = (Value("--backends") ?? "cpu,cuda").Split(',', StringSplitOptions.RemoveEmptyEntries),
            Repeat = int.Parse(Value("--repeat") ?? "3"),
            StepLimit = int.Parse(Value("--steps") ?? "0"),
            Chunk = int.Parse(Value("--chunk") ?? "0"),
            Cold = args.Contains("--cold"),
            Diff = args.Contains("--diff"),
            Queries = args.Contains("--queries"),
            Points = int.Parse(Value("--points") ?? "1000000"),
            PinnedReadBack = args.Contains("--no-pin") ? false : null,
            Reference = Value("--reference") is { } r ? double.Parse(r, CultureInfo.InvariantCulture) : null,
            Dexel = int.Parse(Value("--dexel") ?? "0"),
            Stl = Value("--stl"),
            Out = Value("--out"),
        };
    }
}
