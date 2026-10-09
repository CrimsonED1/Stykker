using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Cutting;

public static partial class Process3
{
    /// <summary>Statistics of a spinning-tool run.</summary>
    /// <param name="Seconds">Process time of the feed motion.</param>
    /// <param name="Revolutions">Spindle revolutions during the feed.</param>
    /// <param name="Body">Run of the spin-invariant body (feed motion only), or null.</param>
    /// <param name="Cutters">Run of the spin-dependent cutters with the spatial process (feed ∘ spin), or null.</param>
    /// <param name="PlanarCutters">Run of the spin-dependent cutters with the planar edge sweep (prismatic cutters fed in their plane), or null.</param>
    public sealed record SpinStats(double Seconds, double Revolutions, Stats? Body, Stats? Cutters, Process2.Stats? PlanarCutters);

    // Spinning cutters: many small swept pieces per revolution, mostly inside material that is already gone, so larger
    // groups are united before each (expensive) cut of the ever finer workpiece.
    private const int SpinBatch = 64;

    /// <summary>
    /// Removes the volume swept by a <see cref="SpinningTool"/> fed along <paramref name="feed"/> (relative to the
    /// workpieces), segment i lasting <paramref name="segmentSeconds"/>[i]. Three cases:
    /// <list type="bullet">
    /// <item>The rotationally symmetric <see cref="SpinningTool.Body"/> is cut with the feed alone: its swept volume
    /// does not depend on the spin (exact for translations, one Minkowski sweep per feed segment).</item>
    /// <item>Prismatic cutters (<see cref="SpinningTool.CutterPrism"/>, e.g. saw teeth) fed in their own plane (spindle
    /// axis fixed, no feed along it): the planar process sweeps the tooth profile under feed + spin with exact edge
    /// sweeps, then the swept area is extruded over the tooth thickness and subtracted once. Error ≤
    /// <see cref="Tolerance.SweepNm"/> (chord deviation of the tooth paths); steps per revolution ≈ π ÷ √(2 ·
    /// <see cref="Tolerance.SweepNm"/> ÷ tip radius), at least 63.</item>
    /// <item>Any other cutters or feed: the spatial process on feed ∘ spin (<see cref="SpinningTool.Motion"/>), convex
    /// hulls of consecutive poses with steps ≤ 2 · <see cref="Tolerance.SweepNm"/> ÷ cutter part diameter. Pieces outside
    /// the workpiece bounds are skipped, but the cost still grows with revolutions × steps per revolution × teeth in
    /// the workpiece bounds, and with the face count of the cut surface.</item>
    /// </list>
    /// </summary>
    /// <param name="workpieces">Workpieces in the feed frame.</param>
    /// <param name="tool">The spinning tool.</param>
    /// <param name="feed">Motion of the tool frame (the spindle axis is the tool z-axis).</param>
    /// <param name="segmentSeconds">Duration of each feed segment in seconds.</param>
    /// <param name="tol">Tolerance (chord and sweep error).</param>
    /// <param name="stats">Statistics of the run.</param>
    /// <param name="startSeconds">Process time at the start of the feed (spindle phase, for cuts split into several calls).</param>
    /// <param name="planar">Use the planar edge sweep when it applies (default); false forces the spatial process.</param>
    public static Solid[] CutSpinning(IReadOnlyList<Solid> workpieces, SpinningTool tool, Motion3 feed, IReadOnlyList<double> segmentSeconds,
        Tolerance? tol, out SpinStats stats, double startSeconds = 0, bool planar = true)
    {
        tol ??= Tolerance.Default;
        if (segmentSeconds.Count != feed.Segments.Count) throw new ArgumentException("One duration per feed segment required.", nameof(segmentSeconds));
        var result = workpieces.ToArray();
        Stats? body = null, cutters = null;
        Process2.Stats? planarStats = null;
        if (tool.Body is not null)
        {
            result = Cut(result, tool.Body, feed, tol, out var s1);
            body = s1;
        }
        if (tool.Cutters is not null)
        {
            if (planar && tool.CutterPrism is { } prism && PlaneFrame(feed) is { } frame)
                result = CutPlanar(result, tool, prism, frame, feed, segmentSeconds, startSeconds, tol, out planarStats);
            else
            {
                result = Cut(result, tool.Cutters, tool.Motion(feed, segmentSeconds, startSeconds), tol, out var s2, SpinBatch);
                cutters = s2;
            }
        }
        double seconds = segmentSeconds.Sum();
        stats = new SpinStats(seconds, Math.Abs(tool.Rpm) / 60 * seconds, body, cutters, planarStats);
        return result;
    }

    /// <summary>
    /// <see cref="CutSpinning(IReadOnlyList{Solid}, SpinningTool, Motion3, IReadOnlyList{double}, Tolerance?, out SpinStats, double, bool)"/>
    /// at a constant feed speed (mm/s along the path of the tool origin, see <see cref="SpinningTool.Durations"/>).
    /// </summary>
    public static Solid[] CutSpinning(IReadOnlyList<Solid> workpieces, SpinningTool tool, Motion3 feed, double feedMmPerS,
        Tolerance? tol, out SpinStats stats, double startSeconds = 0, bool planar = true) =>
        CutSpinning(workpieces, tool, feed, SpinningTool.Durations(feed, feedMmPerS), tol, out stats, startSeconds, planar);

    /// <summary>Single-workpiece convenience overload of <see cref="CutSpinning(IReadOnlyList{Solid}, SpinningTool, Motion3, double, Tolerance?, out SpinStats, double, bool)"/>.</summary>
    public static Solid CutSpinning(Solid workpiece, SpinningTool tool, Motion3 feed, double feedMmPerS, Tolerance? tol = null) =>
        CutSpinning([workpiece], tool, feed, feedMmPerS, tol, out _)[0];

    /// <summary>
    /// The fixed frame of a feed that keeps the tool orientation and moves only perpendicular to the tool z-axis
    /// (checked at 17 points per segment), or null.
    /// </summary>
    private static Pose3? PlaneFrame(Motion3 feed)
    {
        var p0 = feed.Segments[0](0);
        foreach (var seg in feed.Segments)
            for (int i = 0; i <= 16; i++)
            {
                var p = seg(i / 16.0);
                if (Math.Abs(p.R00 - p0.R00) > 1e-12 || Math.Abs(p.R01 - p0.R01) > 1e-12 || Math.Abs(p.R02 - p0.R02) > 1e-12 ||
                    Math.Abs(p.R10 - p0.R10) > 1e-12 || Math.Abs(p.R11 - p0.R11) > 1e-12 || Math.Abs(p.R12 - p0.R12) > 1e-12 ||
                    Math.Abs(p.R20 - p0.R20) > 1e-12 || Math.Abs(p.R21 - p0.R21) > 1e-12 || Math.Abs(p.R22 - p0.R22) > 1e-12)
                    return null;
                // Component of the move along the spindle axis (third column of R).
                double along = p0.R02 * (p.TxNm - p0.TxNm) + p0.R12 * (p.TyNm - p0.TyNm) + p0.R22 * (p.TzNm - p0.TzNm);
                if (Math.Abs(along) > 0.5) return null;
            }
        return p0;
    }

    private static Solid[] CutPlanar(Solid[] workpieces, SpinningTool tool, (Region2 Profile, double Z0Mm, double Z1Mm) prism, Pose3 frame,
        Motion3 feed, IReadOnlyList<double> segmentSeconds, double startSeconds, Tolerance tol, out Process2.Stats stats)
    {
        var inv = frame.Inverse();
        // Planar motion in the frame's xy-plane: in-plane feed position and spindle angle over time, split into pieces of
        // ≤ 45° so that whole revolutions cannot cancel between sampled poses.
        var pieces = new List<Motion2>();
        double t0 = startSeconds;
        for (int i = 0; i < feed.Segments.Count; i++)
        {
            var seg = feed.Segments[i];
            double start = t0, dur = segmentSeconds[i];
            int m = Math.Max(1, (int)Math.Ceiling(Math.Abs(tool.AngularVelocityRadPerS) * dur / (Math.PI / 4) - 1e-9));
            for (int j = 0; j < m; j++)
            {
                int jj = j;
                pieces.Add(Motion2.Custom(t =>
                {
                    double u = (jj + t) / m;
                    var p = seg(u);
                    var (x, y, _) = inv.Apply(p.TxNm, p.TyNm, p.TzNm);
                    return new Pose2(tool.AngleAt(start + u * dur), x, y);
                }));
            }
            t0 += dur;
        }
        var motion = Motion2.Sequence([.. pieces]);

        // The workpieces' bounds in the plane frame, with a margin: only swept pieces inside matter.
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        double zLo = double.MaxValue, zHi = double.MinValue;
        foreach (var w in workpieces)
        {
            if (w.BoundsMm is not { } b) continue;
            foreach (double cx in new[] { b.MinX, b.MaxX })
                foreach (double cy in new[] { b.MinY, b.MaxY })
                    foreach (double cz in new[] { b.MinZ, b.MaxZ })
                    {
                        var (x, y, z) = inv.Apply(cx * Units.NmPerMm, cy * Units.NmPerMm, cz * Units.NmPerMm);
                        x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                        zLo = Math.Min(zLo, z); zHi = Math.Max(zHi, z);
                    }
        }
        var swept = Process2.SweepPieces(prism.Profile, motion, tol, out int intervals);
        stats = new Process2.Stats(intervals, swept.Count);
        if (x0 > x1 || zHi < prism.Z0Mm * Units.NmPerMm || zLo > prism.Z1Mm * Units.NmPerMm) return workpieces;
        const long margin = 1000;
        long bx0 = (long)Math.Floor(x0) - margin, bx1 = (long)Math.Ceiling(x1) + margin;
        long by0 = (long)Math.Floor(y0) - margin, by1 = (long)Math.Ceiling(y1) + margin;
        var relevant = swept.Where(p => p.Max(v => v.X) >= bx0 && p.Min(v => v.X) <= bx1 && p.Max(v => v.Y) >= by0 && p.Min(v => v.Y) <= by1)
            .Select(p => Region2.FromContours([new Contour2(p)])).ToList();
        if (relevant.Count == 0) return workpieces;
        // Union in a tree of small groups: neighbouring tooth positions overlap heavily, and small unions stay simple.
        const int g = 16;
        var level = relevant;
        while (level.Count > 1)
        {
            var next = new List<Region2>();
            for (int i = 0; i < level.Count; i += g) next.Add(Region2.UnionAll(level.Skip(i).Take(g)));
            level = next;
        }
        var area = level[0];
        var volume = Solid.Extrude(area, prism.Z0Mm, prism.Z1Mm, frame);
        return workpieces.Select(w => Overlaps(w, volume) ? w - volume : w).ToArray();
    }
}
