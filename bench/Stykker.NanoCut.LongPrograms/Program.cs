// Usage: dotnet LongPrograms.dll [gear|grinding|dexel|all] [options]
//
// Step 1 of docs/long-programs.md: the baselines of the two long programs on the exact kernel. Every later GPU preview
// is checked against what this bench writes, so a row carries the *result* (area, flank deviation, removed volume,
// surface profile) next to the time, not only the time.
//
//   --teeth 20,40        gear: teeth z to generate (module --module, rack --rack-teeth)
//   --module 2           gear: module in mm
//   --rack-teeth 7       gear: teeth of the rack cutter
//   --sweep 30           gear: sweep error in nm (chord --chord 50, total 0.1 um)
//   --grains 60,240      grinding: grain counts of a random wheel (--seed)
//   --seed 1             grinding: seed of the wheel
//   --length 0.8         grinding: feed length in mm (otherwise the demo default: 20 mm/s, 3000 rpm)
//   --steps 25000,...    dexel: step counts of the finishing pass (--map-mm, --cell-mm, --step-mm, --radius-mm)
//   --map-mm 20          dexel: edge of the square map in mm
//   --cell-mm 0.05       dexel: cell size in mm; the map has (map / cell)² columns
//   --step-mm 0.05       dexel: distance between two steps in mm
//   --radius-mm 0.2      dexel: ball radius in mm
//   --intervals 4        dexel: intervals per column (1 to 16)
//   --repeat 1           runs per case, the best wall time is reported (a gear case is already minutes)
//   --cold                do not warm up (a grinding case is short enough to measure the JIT otherwise)
//   --out <dir>          write long-programs-results.json, -results.md and the reference profiles into <dir>
//
// What is timed: the whole production call, as a user of the library pays it (for the gear the planar cut and the
// extrude separately, since a preview replaces the planar part only). The gear case is the one of
// bench/scenes/gear-m2-z20.json and docs/processes.md: m = 2 mm, z = 20, 1231.252941475 mm2, flank 5.4 nm.
// Both programs run in one process, grinding first: the 2D gear kernel leaves the 3D one measurably slower in the
// same process (see the note at the call site), so the order is part of the measurement.
// A grinding run is warmed up first, on the smallest wheel, until three consecutive runs agree within 10 % and at
// least five have run (bench/Stykker.NanoCut.Bench, --warm: round 6's lesson is that one pass still runs tier-0 code,
// which is worth a factor of 4.7 on the 60-grain case); the gear case runs for minutes and needs no warm-up.
// The dexel mode runs on the CUDA device and needs none of that: the same kernel serves every case, and the first
// call's context creation is reported apart (FirstCallMs).
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stykker.NanoCut;
using Stykker.NanoCut.Cutting;
using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Geometry3D;
using Stykker.NanoCut.Gpu;

var opt = Options.Parse(args);

Console.WriteLine($"long programs: {Environment.ProcessorCount} logical processors, " +
                  $"{RuntimeInformation.FrameworkDescription}, " +
                  $"{(opt.Repeat > 1 ? $"best of {opt.Repeat}" : "single run")}, mode {opt.Mode}");
Console.WriteLine();

// Grinding before gear, and that order is part of the measurement: after the 2D gear kernel has run, the same 3D
// grinding case costs 885 ms in that process against 202 ms in a fresh one (352 ms after a single 30 s gear case). It
// is not the GC mode (server GC: 355 ms), not tiered compilation (TieredCompilation=0: 411 ms), not the machine (a
// fresh process right after the same load reads 198 ms) and not the process running long (5.8 s of 3D work first:
// 185 ms). Gear after grinding is unaffected, so the order below measures both in their own steady state.
var grindRows = opt.Mode is "gear" or "dexel" ? new List<GrindingRow>() : GrindingCases.Run(opt);
var gearRows = opt.Mode is "grinding" or "dexel" ? new List<GearRow>() : GearCases.Run(opt);
// The dexel mode last: it is the only one on the device, and a CUDA context built after minutes of CPU work would
// make its first call read worse than it is.
var dexelRows = opt.Mode is "gear" or "grinding" ? new List<DexelRow>() : DexelCases.Run(opt);

if (opt.Out is { } outDir)
{
    Directory.CreateDirectory(outDir);
    var json = new JsonObject
    {
        ["machine"] = $"{Environment.ProcessorCount} logical processors",
        ["runtime"] = RuntimeInformation.FrameworkDescription,
        ["repeat"] = opt.Repeat,
        ["gear"] = new JsonArray([.. gearRows.Select(r => (JsonNode)new JsonObject
        {
            ["moduleMm"] = r.ModuleMm,
            ["teeth"] = r.Teeth,
            ["rackTeeth"] = r.RackTeeth,
            ["sweepNm"] = r.SweepNm,
            ["chordNm"] = r.ChordNm,
            ["cutMs"] = r.CutMs,
            ["extrudeMs"] = r.ExtrudeMs,
            ["blankAreaMm2"] = r.BlankAreaMm2,
            ["areaMm2"] = r.AreaMm2,
            ["removedMm2"] = r.RemovedMm2,
            ["idealAreaMm2"] = r.IdealAreaMm2,
            ["areaDeviationPct"] = r.AreaDeviationPct,
            ["undercut"] = r.Undercut,
            ["contours"] = r.Contours,
            ["profileVertices"] = r.ProfileVertices,
            ["rollSteps"] = r.RollSteps,
            ["sweptPieces"] = r.SweptPieces,
            ["flankDeviationNm"] = r.FlankDeviationNm,
            ["extrudedMm3"] = r.ExtrudedMm3,
        })]),
        ["grinding"] = new JsonArray([.. grindRows.Select(r => (JsonNode)new JsonObject
        {
            ["grains"] = r.Grains,
            ["seed"] = r.Seed,
            ["wallMs"] = r.WallMs,
            ["stockMm3"] = r.StockMm3,
            ["removedMm3"] = r.RemovedMm3,
            ["activeGrains"] = r.ActiveGrains,
            ["passes"] = r.Passes,
            ["cuttingPasses"] = r.CuttingPasses,
            ["revolutions"] = r.Revolutions,
            ["hulls"] = r.Hulls,
            ["maxChipMm"] = r.MaxChipMm,
            ["raMm"] = r.RaMm,
            ["rzMm"] = r.RzMm,
            ["profileXmm"] = r.ProfileXmm,
            ["cells"] = r.Cells,
        })]),
        ["dexel"] = new JsonArray([.. dexelRows.Select(r => (JsonNode)new JsonObject
        {
            ["steps"] = r.Steps,
            ["rows"] = r.Rows,
            ["mapMm"] = r.MapMm,
            ["cellMm"] = r.CellMm,
            ["stepMm"] = r.StepMm,
            ["radiusMm"] = r.RadiusMm,
            ["columns"] = r.Columns,
            ["maxIntervals"] = r.MaxIntervals,
            ["firstCallMs"] = r.FirstCallMs,
            ["binnedKernelMs"] = r.BinnedKernelMs,
            ["binnedUploadMs"] = r.BinnedUploadMs,
            ["binnedBinMs"] = r.BinnedBinMs,
            ["binnedWallMs"] = r.BinnedWallMs,
            ["unbinnedKernelMs"] = r.UnbinnedKernelMs,
            ["unbinnedUploadMs"] = r.UnbinnedUploadMs,
            ["unbinnedWallMs"] = r.UnbinnedWallMs,
            ["removedMm3"] = r.RemovedMm3,
            ["volumeAgreement"] = r.VolumeAgreement,
            ["overflows"] = r.Overflows,
        })]),
    };
    File.WriteAllText(Path.Combine(outDir, "long-programs-results.json"),
        json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    File.WriteAllText(Path.Combine(outDir, "long-programs-results.md"),
        Report.Markdown(opt, gearRows, grindRows, dexelRows), Encoding.UTF8);
    foreach (var r in gearRows) GearCases.WriteProfile(outDir, r);
    foreach (var r in grindRows) GrindingCases.WriteProfile(outDir, r);
    Console.WriteLine($"\nwrote {Path.Combine(outDir, "long-programs-results.json")}, -results.md and the reference profiles");
}

return 0;

/// <summary>One gear generation on the exact kernel (the same case as <c>bench/scenes/gear-m2-z20.json</c>).</summary>
sealed record GearRow(double ModuleMm, int Teeth, int RackTeeth, double SweepNm, double ChordNm, double CutMs,
    double ExtrudeMs, double BlankAreaMm2, double AreaMm2, double RemovedMm2, double IdealAreaMm2, int Contours,
    int ProfileVertices, int RollSteps, int SweptPieces, double FlankDeviationNm, double ExtrudedMm3, bool Undercut)
{
    /// <summary>The generated profile, kept so that it can be written as the reference of a later preview.</summary>
    public Region2? Profile { get; init; }

    /// <summary>Generated area against the involute gear of the same module (percent).</summary>
    public double AreaDeviationPct => (AreaMm2 - IdealAreaMm2) / IdealAreaMm2 * 100;
}

/// <summary>One grinding run on the exact kernel (<c>GrindingSimulation</c> with the demo's parameters).</summary>
sealed record GrindingRow(int Grains, int Seed, double WallMs, double StockMm3, double RemovedMm3, int ActiveGrains,
    int Passes, int CuttingPasses, double Revolutions, long Hulls, double MaxChipMm, double RaMm, double RzMm,
    double ProfileXmm, int Cells)
{
    /// <summary>Surface across the ground width at <see cref="ProfileXmm"/> (mm), kept for the same reason.</summary>
    public double[]? Surface { get; init; }
}

/// <summary>
/// One length of the synthetic finishing pass on the CUDA device, run twice: once with the steps binned into tiles
/// of columns (<c>CudaBackend.BinSteps</c>) and once with every column looking at every step. Both runs get the same
/// program on the same map, and the removed volume says whether the binning changed anything -- it must not.
/// </summary>
sealed record DexelRow(int Steps, int Rows, double MapMm, double CellMm, double StepMm, double RadiusMm,
    int Columns, int MaxIntervals, double FirstCallMs,
    double BinnedKernelMs, double BinnedUploadMs, double BinnedBinMs, double BinnedWallMs,
    double UnbinnedKernelMs, double UnbinnedUploadMs, double UnbinnedWallMs,
    double RemovedMm3, bool VolumeAgreement, long Overflows)
{
    /// <summary>What the binned launch saves on the kernel alone.</summary>
    public double SpeedUp => UnbinnedKernelMs / BinnedKernelMs;

    /// <summary>What it saves on the whole call, the host-side binning included.</summary>
    public double WallSpeedUp => UnbinnedWallMs / BinnedWallMs;
}

static class GearCases
{
    internal const double ExtrudeMm = 10;
    internal const double PressureAngleDeg = 20;

    internal static List<GearRow> Run(Options opt)
    {
        var rows = new List<GearRow>();
        double m = opt.ModuleMm;
        var tol = Tolerance.Budget(chordNm: opt.ChordNm, sweepNm: opt.SweepNm);
        foreach (int z in opt.Teeth)
        {
            double rp = m * z / 2 * Units.NmPerMm, pitch = Math.PI * m * Units.NmPerMm;
            var blank = Region2.Circle(default, m * z / 2 + m, tol);
            var rack = GearProfile.Rack(m, opt.RackTeeth, 20, bodyMm: 0.5);
            // The rack rolls on the pitch circle: one segment per tooth, each a rotation of the blank plus the
            // translation of the rack along its own line (relative motion of the workpiece).
            var rolling = Motion2.Sequence([.. Enumerable.Range(0, z).Select(k => Motion2.Custom(t =>
            {
                double dphi = 2 * Math.PI / z * t, phi = 2 * Math.PI * k / z + dphi;
                return Pose2.Rotation(-phi).Compose(new Pose2(0, pitch / 2 - rp * dphi, rp));
            }))]);
            // The involute gear of the same module, as the yardstick for the generated area. Below 2/sin²α teeth a
            // generated gear is undercut, so its flank is no longer the involute and the deviation column says nothing.
            double idealArea = GearProfile.Involute(m, z, PressureAngleDeg, tol).AreaMm2;
            bool undercut = z < 2 / Math.Pow(Math.Sin(PressureAngleDeg * Math.PI / 180), 2);
            Console.WriteLine($"gear m = {m} mm, z = {z}, rack {opt.RackTeeth} teeth, sweep {opt.SweepNm} nm, " +
                              $"tolerance {tol.TotalNm} nm{(undercut ? $", undercut (z < 17.1 at {PressureAngleDeg}°)" : "")} ...");

            GearRow? best = null;
            for (int run = 0; run < opt.Repeat; run++)
            {
                var sw = Stopwatch.StartNew();
                var gear = Process2.Cut([blank], rack, rolling, tol, out var stats)[0];
                sw.Stop();
                var ex = Stopwatch.StartNew();
                var solid = Solid.Extrude(gear, 0, ExtrudeMm);
                ex.Stop();
                double flank = FlankDeviationNm(gear, m, z, PressureAngleDeg);
                var row = new GearRow(m, z, opt.RackTeeth, opt.SweepNm, opt.ChordNm, sw.Elapsed.TotalMilliseconds,
                    ex.Elapsed.TotalMilliseconds, blank.AreaMm2, gear.AreaMm2, blank.AreaMm2 - gear.AreaMm2, idealArea,
                    gear.Contours.Count, gear.Contours.Sum(c => c.Count), stats.Intervals, stats.Pieces, flank,
                    solid.VolumeMm3, undercut) { Profile = opt.Out is null ? null : gear };
                if (best is null || row.CutMs < best.CutMs) best = row;
            }
            Console.WriteLine($"  cut {best!.CutMs / 1000,8:F2} s   extrude {best.ExtrudeMs,7:F0} ms   " +
                              $"area {best.AreaMm2,12:F6} mm2 (blank {best.BlankAreaMm2:F3}, ideal {best.IdealAreaMm2:F3}, " +
                              $"{best.AreaDeviationPct:+0.0;-0.0} %)   " +
                              $"contours {best.Contours,4}   vertices {best.ProfileVertices,6}   " +
                              $"roll steps {best.RollSteps,5}   pieces {best.SweptPieces,7}   " +
                              $"flank {best.FlankDeviationNm / 1000,7:F3} um");
            rows.Add(best);
        }
        return rows;
    }

    /// <summary>
    /// Worst deviation of the generated flank from the ideal involute in nm, over the vertices in the flank band --
    /// the same metric the demo scene and tests/Stykker.NanoCut.Tests/ProcessTests.cs assert.
    /// </summary>
    internal static double FlankDeviationNm(Region2 gear, double m, int z, double pressureAngleDeg = PressureAngleDeg)
    {
        double rp = m * z / 2 * Units.NmPerMm, alpha = pressureAngleDeg * Math.PI / 180;
        double rb = rp * Math.Cos(alpha), ra = rp + m * Units.NmPerMm;
        double psiB = Math.PI / (2 * z) + GearProfile.Inv(alpha);
        double worst = 0;
        foreach (var c in gear.Contours)
            foreach (var v in c.Points)
            {
                double r = Math.Sqrt((double)v.X * v.X + (double)v.Y * v.Y);
                if (r < rb + 0.3 * m * Units.NmPerMm || r > ra - 0.1 * m * Units.NmPerMm) continue;
                double ar = Math.Acos(rb / r), ideal = psiB - GearProfile.Inv(ar);
                double rel = Math.Abs(Math.IEEERemainder(Math.Atan2(v.Y, v.X), 2 * Math.PI / z));
                worst = Math.Max(worst, r * Math.Abs(rel - ideal) * Math.Sin(ar));
            }
        return worst;
    }

    internal static void WriteProfile(string dir, GearRow r)
    {
        if (r.Profile is not { } gear) return;
        var sb = new StringBuilder("contour,index,x_mm,y_mm\n");
        for (int c = 0; c < gear.Contours.Count; c++)
        {
            var contour = gear.Contours[c];
            for (int i = 0; i < contour.Count; i++)
                sb.Append(c).Append(',').Append(i).Append(',')
                  .Append((contour[i].X / Units.NmPerMm).ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                  .Append((contour[i].Y / Units.NmPerMm).ToString("F6", CultureInfo.InvariantCulture)).Append('\n');
        }
        string name = $"gear-m{r.ModuleMm:0.##}-z{r.Teeth}-profile.csv";
        File.WriteAllText(Path.Combine(dir, name), sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"  wrote {name} ({r.Contours} contours, {r.ProfileVertices} vertices)");
    }
}

static class GrindingCases
{
    // The demo's grinding page, so that the numbers of docs/long-programs.md and bench/README.md ("Grinding, demo
    // default") describe the same program: 20 mm wheel, 150 um grains, 40 um mean protrusion, 3000 rpm, 20 mm/s.
    internal const double WheelRadiusMm = 10, WheelWidthMm = 1, GrainSizeMm = 0.150;
    internal const double ProtrusionMeanMm = 0.040, ProtrusionSigmaMm = 0.015;
    internal const double Rpm = 3000, FeedMmPerS = 20, DepthUm = 20, SweepUm = 2;
    internal const double BlockLengthMm = 1.5, HalfWidthMm = 0.4, X0Mm = -0.3;
    internal const int ProfileSamples = 301;

    internal static List<GrindingRow> Run(Options opt)
    {
        var rows = new List<GrindingRow>();
        double length = opt.LengthMm, zCenter = WheelRadiusMm + (ProtrusionMeanMm - DepthUm / 1000);
        var block = Solid.Box(Vec3.Mm(0, -HalfWidthMm, -0.3), Vec3.Mm(BlockLengthMm, HalfWidthMm, 0));
        double stock = block.VolumeMm3;
        var feed = Motion3.Compose(
            Motion3.Linear(Vec3.Mm(X0Mm, 0, zCenter), Vec3.Mm(X0Mm + length, 0, zCenter)),
            Motion3.Fixed(Pose3.Rotation(-Math.PI / 2, 1, 0, 0)));
        var durations = SpinningTool.Durations(feed, FeedMmPerS);
        // Same budget as the demo page: the sweep error must stay small against the chip thickness (20 um).
        var tol = Tolerance.Budget(totalUm: SweepUm + 0.051, chordNm: 50, sweepNm: SweepUm * 1000);
        double profileX = Math.Clamp(X0Mm + length - 0.1, 0.05, BlockLengthMm - 0.05);
        double revolutions = Rpm / 60 * durations.Sum();

        // Warm up on the smallest wheel: the same code paths, a fraction of the time. The rule of bench/Stykker.NanoCut.Bench
        // (--warm, round 6): repeat until three consecutive runs agree within 10 % and at least five have run. One pass
        // is not enough -- the 60-grain case then still runs tier-0 code and reads 938 ms against 200 ms warm.
        if (!opt.Cold)
        {
            int warm = opt.Grains.Min();
            var recent = new Queue<double>();
            var total = Stopwatch.StartNew();
            int runs = 0;
            while (runs < 40)
            {
                var sim = new GrindingSimulation(block, GrindingWheel.Random(WheelRadiusMm, WheelWidthMm, warm,
                    GrainSizeMm, ProtrusionMeanMm, ProtrusionSigmaMm, Rpm, opt.Seed), feed, durations, tol);
                var one = Stopwatch.StartNew();
                sim.Run();
                one.Stop();
                runs++;
                double ms = one.Elapsed.TotalMilliseconds;
                if (ms > 3000) break;
                recent.Enqueue(ms);
                if (recent.Count > 3) recent.Dequeue();
                if (recent.Count == 3)
                {
                    double[] last = recent.ToArray();
                    if (last.Max() / last[0] <= 1.10 && runs >= 5) break;
                }
            }
            Console.WriteLine($"warm-up on {warm} grains, not measured: {runs} runs in {total.Elapsed.TotalSeconds:F1} s");
        }

        foreach (int grains in opt.Grains)
        {
            Console.WriteLine($"grinding {grains} grains, wheel Ø{2 * WheelRadiusMm} mm, {GrainSizeMm * 1000} um grains, " +
                              $"{Rpm} rpm, {FeedMmPerS} mm/s over {length} mm ...");
            var wheel = GrindingWheel.Random(WheelRadiusMm, WheelWidthMm, grains, GrainSizeMm, ProtrusionMeanMm,
                ProtrusionSigmaMm, Rpm, opt.Seed);

            GrindingRow? best = null;
            for (int run = 0; run < opt.Repeat; run++)
            {
                var sim = new GrindingSimulation(block, wheel, feed, durations, tol);
                var sw = Stopwatch.StartNew();
                sim.Run();
                sw.Stop();
                double[] surface = new SurfaceProfile(sim.Cells).Line(profileX, -HalfWidthMm, profileX, HalfWidthMm, ProfileSamples);
                (double ra, double rz) = SurfaceProfile.Roughness(surface);
                var row = new GrindingRow(grains, opt.Seed, sw.Elapsed.TotalMilliseconds, stock, sim.RemovedMm3,
                    sim.ActiveGrains, sim.Passes.Count, sim.Passes.Count(p => p.RemovedMm3 > 0), revolutions, sim.Hulls,
                    sim.Grains.Max(g => g.MaxChipThicknessMm), ra, rz, profileX, sim.Cells.Count)
                { Surface = opt.Out is null ? null : surface };
                if (best is null || row.WallMs < best.WallMs) best = row;
            }
            var b = best!;
            Console.WriteLine($"  {b.WallMs,9:F0} ms   removed {b.RemovedMm3,10:F6} mm3 of {b.StockMm3:F2} mm3 stock   " +
                              $"active grains {b.ActiveGrains,5}/{grains,-5}   passes {b.CuttingPasses,5}/{b.Passes,-5} cutting   " +
                              $"hulls {b.Hulls,7}   chip {b.MaxChipMm * 1000,5:F2} um   Ra {b.RaMm * 1000,5:F2} um   Rz {b.RzMm * 1000,6:F2} um");
            rows.Add(b);
        }
        return rows;
    }

    internal static void WriteProfile(string dir, GrindingRow r)
    {
        if (r.Surface is not { } surface) return;
        var sb = new StringBuilder("y_mm,z_mm\n");
        for (int i = 0; i < surface.Length; i++)
        {
            double y = -HalfWidthMm + 2 * HalfWidthMm * i / (surface.Length - 1);
            sb.Append(y.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
              .Append(surface[i].ToString("F9", CultureInfo.InvariantCulture)).Append('\n');
        }
        string name = $"grinding-{r.Grains}-grains-surface.csv";
        File.WriteAllText(Path.Combine(dir, name), sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"  wrote {name} (x = {r.ProfileXmm:F2} mm)");
    }
}

/// <summary>
/// Step 2 of docs/long-programs.md: a finishing pass -- the long ball program a preview is for -- on the CUDA dexel
/// kernel, with and without the tile binning. Map, cell and step distance stay fixed while the program grows, so the
/// unbinned launch's work (every column against every step) grows with the program and the binned launch's grows only
/// with what the tool actually touches.
/// </summary>
static class DexelCases
{
    internal const double StockMm = 2.0;

    /// <summary>
    /// A serpentine finishing pass: rows along x at a fixed height, <paramref name="perRow"/> steps of
    /// <paramref name="stepMm"/> each. The rows are spread over the map, so a longer program is a finer y pitch
    /// rather than a longer trail -- the shape a finishing pass has.
    /// </summary>
    private static BallStep[] Pass(int rows, int perRow, double mapMm, double stepMm, double radiusMm, double z)
    {
        var steps = new BallStep[rows * perRow];
        for (int j = 0; j < rows; j++)
        {
            double y = mapMm * (j + 0.5) / rows;
            bool forward = (j & 1) == 0;
            for (int i = 0; i < perRow; i++)
            {
                double x = stepMm * (0.5 + (forward ? i : perRow - 1 - i));
                steps[j * perRow + i] = new BallStep((x, y, z), (x + stepMm, y, z), radiusMm);
            }
        }
        return steps;
    }

    private readonly record struct Launch(double KernelMs, double UploadMs, double BinMs, double WallMs,
        double VolumeMm3, long Overflows);

    /// <summary>One launch of the whole program on a map of its own, timed by the map itself.</summary>
    private static Launch Measure(Options opt, CudaBackend backend, BallStep[] steps, int cells)
    {
        var map = new DexelMap(0, 0, 0, opt.MapMm, opt.MapMm, StockMm, cells, cells, opt.Intervals, backend);
        map.ApplySteps(steps, ZMapReadBack.Never);
        double volume = map.BackendRemovedVolumeMm3;
        return new Launch(map.LastTiming.KernelMs, map.LastTiming.UploadMs, map.LastTiming.BinMs,
            map.LastTiming.WallMs, volume, map.Overflows);
    }

    internal static List<DexelRow> Run(Options opt)
    {
        var rows = new List<DexelRow>();
        var binned = new CudaBackend { BinSteps = true };
        var plain = new CudaBackend { BinSteps = false };
        if (!binned.IsAvailable)
        {
            Console.WriteLine($"dexel not run: {binned.UnavailableReason}");
            return rows;
        }

        int cells = (int)Math.Round(opt.MapMm / opt.CellMm);
        int perRow = (int)Math.Round(opt.MapMm / opt.StepMm);
        // The ball's centre just under the top: a finishing pass takes off about one radius, not the whole stock.
        double z = StockMm - 0.25 * opt.RadiusMm;
        Console.WriteLine($"finishing pass on the dexel kernel: {cells}x{cells} columns over {opt.MapMm:F1} mm " +
                          $"({opt.CellMm:F3} mm cells, {opt.Intervals} intervals per column), ball r {opt.RadiusMm:F2} mm, " +
                          $"steps {opt.StepMm:F3} mm apart, device {binned.Name}");

        // The context and the module are created before the first measured case, so its FirstCallMs is what the
        // cases read and not a one-off that belongs to no case.
        var warm = new DexelMap(0, 0, 0, opt.MapMm, opt.MapMm, StockMm, 16, 16, opt.Intervals, binned);
        warm.ApplySteps(Pass(2, 4, opt.MapMm, opt.StepMm, opt.RadiusMm, z), ZMapReadBack.Never);
        Console.WriteLine($"context and module ready in {warm.LastTiming.FirstCallMs:F0} ms, not measured");
        Console.WriteLine();

        foreach (int want in opt.Steps)
        {
            int caseRows = Math.Max(1, want / perRow), count = caseRows * perRow;
            BallStep[] steps = Pass(caseRows, perRow, opt.MapMm, opt.StepMm, opt.RadiusMm, z);
            Console.WriteLine($"{count,7} steps over {caseRows,4} rows ({count * cells,10:N0} column steps unbinned) ...");

            Launch bestBinned = default, bestPlain = default;
            bool haveBinned = false, havePlain = false;
            for (int run = 0; run < opt.Repeat; run++)
            {
                Launch b = Measure(opt, binned, steps, cells);
                if (!haveBinned || b.WallMs < bestBinned.WallMs) { bestBinned = b; haveBinned = true; }
                Launch p = Measure(opt, plain, steps, cells);
                if (!havePlain || p.WallMs < bestPlain.WallMs) { bestPlain = p; havePlain = true; }
            }

            var row = new DexelRow(count, caseRows, opt.MapMm, opt.CellMm, opt.StepMm, opt.RadiusMm, cells * cells,
                opt.Intervals, warm.LastTiming.FirstCallMs,
                bestBinned.KernelMs, bestBinned.UploadMs, bestBinned.BinMs, bestBinned.WallMs,
                bestPlain.KernelMs, bestPlain.UploadMs, bestPlain.WallMs,
                bestBinned.VolumeMm3, bestBinned.VolumeMm3 == bestPlain.VolumeMm3, bestBinned.Overflows);
            Console.WriteLine($"  binned    kernel {row.BinnedKernelMs,9:F1} ms   bin {row.BinnedBinMs,8:F1} ms   " +
                              $"upload {row.BinnedUploadMs,6:F1} ms   wall {row.BinnedWallMs,9:F1} ms");
            Console.WriteLine($"  unbinned  kernel {row.UnbinnedKernelMs,9:F1} ms   {new string(' ', 11)}   " +
                              $"upload {row.UnbinnedUploadMs,6:F1} ms   wall {row.UnbinnedWallMs,9:F1} ms");
            Console.WriteLine($"  kernel {row.SpeedUp,6:F1}x   wall {row.WallSpeedUp,5:F1}x   removed {row.RemovedMm3:F6} mm3 " +
                              $"({(row.VolumeAgreement ? "identical" : "DIFFERENT")} in both launches)   " +
                              $"overflows {row.Overflows}");
            Console.WriteLine();
            rows.Add(row);
        }
        return rows;
    }
}

static class Report
{
    internal static string Markdown(Options opt, List<GearRow> gear, List<GrindingRow> grind, List<DexelRow> dexel)
    {
        var inv = CultureInfo.InvariantCulture;
        string N(double v, string format) => v.ToString(format, inv);
        var lines = new List<string>
        {
            "# Long programs on the exact kernel",
            "",
            $"{Environment.ProcessorCount} logical processors, {RuntimeInformation.FrameworkDescription}, " +
            $"{(opt.Repeat > 1 ? $"best of {opt.Repeat}" : "single run")}.",
            "",
        };
        if (gear.Count > 0)
        {
            lines.Add("## Gear generation (Process2.Cut, 2D)");
            lines.Add("");
            lines.Add("The flank column compares the generated flank with the ideal involute over the band the demo scene " +
                      "and tests/Stykker.NanoCut.Tests/ProcessTests.cs use; below z = 2/sin²α = 17.1 a generated gear " +
                      "is undercut and that comparison does not apply.");
            lines.Add("");
            lines.Add("| m (mm) | z | Cut (s) | Extrude (ms) | Area (mm²) | Ideal (mm²) | Δ | Contours | Vertices | " +
                      "Roll steps | Pieces | Flank (nm) |");
            lines.Add("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            foreach (var r in gear)
                lines.Add($"| {N(r.ModuleMm, "0.##")} | {r.Teeth}{(r.Undercut ? " †" : "")} | {N(r.CutMs / 1000, "F2")} | " +
                          $"{N(r.ExtrudeMs, "F0")} | {N(r.AreaMm2, "F6")} | {N(r.IdealAreaMm2, "F3")} | " +
                          $"{N(r.AreaDeviationPct, "+0.0;-0.0")} % | {r.Contours} | {r.ProfileVertices} | " +
                          $"{r.RollSteps} | {r.SweptPieces} | {(r.Undercut ? "–" : N(r.FlankDeviationNm, "F1"))} |");
            lines.Add("");
        }
        if (grind.Count > 0)
        {
            lines.Add("## Grinding (GrindingSimulation, 3D)");
            lines.Add("");
            lines.Add("| Grains | Wall (ms) | Removed (mm³) | Active grains | Cutting passes | Passes | Hulls | " +
                      "Chip (µm) | Ra (µm) | Rz (µm) |");
            lines.Add("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            foreach (var r in grind)
                lines.Add($"| {r.Grains} | {N(r.WallMs, "F0")} | {N(r.RemovedMm3, "F6")} | " +
                          $"{r.ActiveGrains}/{r.Grains} | {r.CuttingPasses} | {r.Passes} | {r.Hulls} | " +
                          $"{N(r.MaxChipMm * 1000, "F2")} | {N(r.RaMm * 1000, "F2")} | {N(r.RzMm * 1000, "F2")} |");
            lines.Add("");
        }
        if (dexel.Count > 0)
        {
            lines.Add("## Step binning on a finishing pass (CUDA dexel kernel)");
            lines.Add("");
            lines.Add($"A serpentine finishing pass of a {dexel[0].RadiusMm:0.##} mm ball over a {dexel[0].MapMm:0.##} mm " +
                      $"square map of {dexel[0].Columns:N0} columns at {dexel[0].StepMm:0.###} mm steps. The binned launch " +
                      "gives every block of 16x16 columns only the steps that reach it; the unbinned one has every " +
                      "column test every step. Both run the same program on the same map, so the removed volume must " +
                      "come out the same -- that is what the test " +
                      "`DexelMapTests.BinnedLaunchGivesTheSameBitsAsTheUnbinnedOne` pins down.");
            lines.Add("");
            lines.Add("| Steps | Rows | Binned kernel (ms) | Bin (ms) | Upload (ms) | Binned wall (ms) | " +
                      "Unbinned kernel (ms) | Upload (ms) | Unbinned wall (ms) | Kernel × | Wall × | Removed (mm³) |");
            lines.Add("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            foreach (var r in dexel)
                lines.Add($"| {r.Steps} | {r.Rows} | {N(r.BinnedKernelMs, "F1")} | {N(r.BinnedBinMs, "F1")} | " +
                          $"{N(r.BinnedUploadMs, "F1")} | {N(r.BinnedWallMs, "F1")} | {N(r.UnbinnedKernelMs, "F1")} | " +
                          $"{N(r.UnbinnedUploadMs, "F1")} | {N(r.UnbinnedWallMs, "F1")} | {N(r.SpeedUp, "F1")} | " +
                          $"{N(r.WallSpeedUp, "F2")} | {N(r.RemovedMm3, "F6")} |");
            lines.Add("");
        }
        return string.Join('\n', lines) + "\n";
    }
}

sealed class Options
{
    public required string Mode { get; init; }
    public required int[] Teeth { get; init; }
    public required double ModuleMm { get; init; }
    public required int RackTeeth { get; init; }
    public required double SweepNm { get; init; }
    public required double ChordNm { get; init; }
    public required int[] Grains { get; init; }
    public required int Seed { get; init; }
    public required double LengthMm { get; init; }
    public required int[] Steps { get; init; }
    public required double MapMm { get; init; }
    public required double CellMm { get; init; }
    public required double StepMm { get; init; }
    public required double RadiusMm { get; init; }
    public required int Intervals { get; init; }
    public required int Repeat { get; init; }
    public required bool Cold { get; init; }
    public string? Out { get; init; }

    public static Options Parse(string[] args)
    {
        string? Value(string name)
        {
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return null;
        }

        string mode = "all";
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            mode = args[0];
            if (mode is not ("gear" or "grinding" or "dexel" or "all"))
                throw new ArgumentException($"unknown mode {mode}: use gear, grinding, dexel or all. Options: " +
                                            "[--teeth 20,40] [--module 2] [--rack-teeth 7] [--sweep 30] " +
                                            "[--grains 60,240] [--seed 1] [--length 0.8] [--steps 25000] " +
                                            "[--map-mm 20] [--cell-mm 0.05] [--step-mm 0.05] [--radius-mm 0.2] " +
                                            "[--intervals 4] [--repeat 1] [--cold] [--out dir]");
        }
        return new Options
        {
            Mode = mode,
            Teeth = Ints(Value("--teeth") ?? "20"),
            ModuleMm = double.Parse(Value("--module") ?? "2", CultureInfo.InvariantCulture),
            RackTeeth = int.Parse(Value("--rack-teeth") ?? "7", CultureInfo.InvariantCulture),
            SweepNm = double.Parse(Value("--sweep") ?? "30", CultureInfo.InvariantCulture),
            ChordNm = double.Parse(Value("--chord") ?? "50", CultureInfo.InvariantCulture),
            Grains = Ints(Value("--grains") ?? "60,240,960,1920"),
            Seed = int.Parse(Value("--seed") ?? "1", CultureInfo.InvariantCulture),
            LengthMm = double.Parse(Value("--length") ?? "0.8", CultureInfo.InvariantCulture),
            Steps = Ints(Value("--steps") ?? "24800,99200,396800,793600"),
            MapMm = double.Parse(Value("--map-mm") ?? "20", CultureInfo.InvariantCulture),
            CellMm = double.Parse(Value("--cell-mm") ?? "0.05", CultureInfo.InvariantCulture),
            StepMm = double.Parse(Value("--step-mm") ?? "0.05", CultureInfo.InvariantCulture),
            RadiusMm = double.Parse(Value("--radius-mm") ?? "0.2", CultureInfo.InvariantCulture),
            Intervals = int.Parse(Value("--intervals") ?? "4", CultureInfo.InvariantCulture),
            Repeat = int.Parse(Value("--repeat") ?? "1", CultureInfo.InvariantCulture),
            Cold = args.Contains("--cold"),
            Out = Value("--out"),
        };

        static int[] Ints(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
    }
}