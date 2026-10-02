using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Cutting;

/// <summary>
/// General 3D machining process: an acting shape moves relative to one or more workpieces along an arbitrary
/// rigid motion (translation and rotation). Between two sampled poses with the same orientation each convex part
/// sweeps its exact Minkowski sum with the move (<see cref="Sweep3.Translate"/>). With rotation the convex hull of
/// both poses is used, which can overcut by up to part diameter × rotation angle / 2, so the sampling keeps that
/// bound below <see cref="Tolerance.SweepNm"/> (correct but slow for large rotations).
/// For processes that keep an axis (planar processes, turning) <see cref="Process2"/> and <see cref="Lathe"/> are
/// much faster and use an exact edge-sweep instead.
/// </summary>
public static partial class Process3
{
    /// <summary>Statistics of a process run.</summary>
    public sealed record Stats(int Intervals, int Hulls, int Cuts);

    /// <summary>Removes the volume swept by <paramref name="tool"/> under <paramref name="motion"/> (relative to the workpieces).</summary>
    public static Solid[] Cut(IReadOnlyList<Solid> workpieces, ToolShape tool, Motion3 motion, Tolerance? tol, out Stats stats) =>
        Cut(workpieces, tool, motion, tol, out stats, Batch);

    internal static Solid[] Cut(IReadOnlyList<Solid> workpieces, ToolShape tool, Motion3 motion, Tolerance? tol, out Stats stats, int batch)
    {
        tol ??= Tolerance.Default;
        var result = workpieces.ToArray();
        var partPoints = tool.Parts.Select(p => p.Vertices.Select(v => v.Grid).Distinct().ToArray()).ToArray();
        var probe = ConvexHullProbe(partPoints);
        double diameter = partPoints.Max(Diameter);
        var rotated = new Dictionary<(double, double, double, double, double, double, double, double, double), Solid[]>();
        int intervals = 0, hulls = 0, cuts = 0;
        var pending = new List<Solid>();
        // Bounds of the workpieces (nm); swept pieces that miss all of them cannot remove anything and are skipped.
        var bounds = result.Select(BoundsNm).ToArray();
        foreach (var seg in motion.Segments)
        {
            var poses = Sample(seg, probe, diameter, tol.SweepNm);
            for (int i = 0; i + 1 < poses.Count || (poses.Count == 1 && i == 0); i++)
            {
                intervals++;
                var a = poses[i];
                var b = poses.Count == 1 ? a : poses[i + 1];
                bool sameRotation = a.R00 == b.R00 && a.R01 == b.R01 && a.R02 == b.R02 && a.R10 == b.R10 && a.R11 == b.R11
                                    && a.R12 == b.R12 && a.R20 == b.R20 && a.R21 == b.R21 && a.R22 == b.R22;
                for (int k = 0; k < tool.Parts.Count; k++)
                {
                    Solid swept;
                    if (sameRotation)
                    {
                        // Exact fast path: the part (rotated once, rounded) translated along the move.
                        var key = (a.R00, a.R01, a.R02, a.R10, a.R11, a.R12, a.R20, a.R21, a.R22);
                        if (!rotated.TryGetValue(key, out var placed))
                            rotated[key] = placed = tool.Parts.Select(p => p.Transform(a with { TxNm = 0, TyNm = 0, TzNm = 0 })).ToArray();
                        var t0 = new Vec3(Round(a.TxNm), Round(a.TyNm), Round(a.TzNm));
                        var t1 = new Vec3(Round(b.TxNm), Round(b.TyNm), Round(b.TzNm));
                        swept = Sweep3.Translate(Translate(placed[k], t0), t1 - t0);
                    }
                    else
                    {
                        var pts = partPoints[k];
                        var moved = pts.Select(a.Apply).Concat(pts.Select(b.Apply)).ToArray();
                        if (!bounds.Any(w => Overlaps(w, BoundsNm(moved)))) continue;
                        swept = ConvexHull3.Compute(moved);
                    }
                    if (!bounds.Any(w => Overlaps(w, BoundsNm(swept)))) continue;
                    hulls++;
                    pending.Add(swept);
                    if (pending.Count == batch) Flush();
                }
            }
        }
        Flush();

        // Small groups of neighbouring swept pieces are united first (cheap, local) and then cut from the workpieces:
        // cutting every piece alone touches the whole, ever finer workpiece per step; one huge union is slower still.
        // Pieces whose bounds miss every workpiece were skipped above (e.g. the teeth of a spinning blade outside).
        void Flush()
        {
            if (pending.Count == 0) return;
            var swept = UnionTree(pending);
            pending.Clear();
            for (int w = 0; w < result.Length; w++)
            {
                if (!Overlaps(result[w], swept)) continue;
                result[w] = result[w] - swept;
                bounds[w] = BoundsNm(result[w]);
                cuts++;
            }
        }
        stats = new Stats(intervals, hulls, cuts);
        return result;
    }

    /// <summary>Single-workpiece convenience overload.</summary>
    public static Solid Cut(Solid workpiece, ToolShape tool, Motion3 motion, Tolerance? tol = null) =>
        Cut([workpiece], tool, motion, tol, out _)[0];

    private const int Batch = 8;

    private static Solid UnionTree(List<Solid> pieces)
    {
        var level = pieces;
        while (level.Count > 1)
        {
            var next = new List<Solid>((level.Count + 1) / 2);
            for (int i = 0; i + 1 < level.Count; i += 2) next.Add(level[i] | level[i + 1]);
            if (level.Count % 2 == 1) next.Add(level[^1]);
            level = next;
        }
        return level[0];
    }

    private static long Round(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    private static Solid Translate(Solid s, Vec3 t) => t == default ? s : s.Transform(Pose3.Identity with { TxNm = t.X, TyNm = t.Y, TzNm = t.Z });

    // Deviation of a rigid motion is affine in the point, so the extreme points of the tool suffice.
    private static Vec3[] ConvexHullProbe(Vec3[][] parts)
    {
        var all = parts.SelectMany(p => p).Distinct().ToArray();
        if (all.Length <= 64) return all;
        // Bounding box corners enclose the tool; their deviation bounds every tool point's deviation.
        long x0 = all.Min(p => p.X), x1 = all.Max(p => p.X), y0 = all.Min(p => p.Y), y1 = all.Max(p => p.Y);
        long z0 = all.Min(p => p.Z), z1 = all.Max(p => p.Z);
        return [new(x0, y0, z0), new(x1, y0, z0), new(x0, y1, z0), new(x1, y1, z0), new(x0, y0, z1), new(x1, y0, z1), new(x0, y1, z1), new(x1, y1, z1)];
    }

    private static (long X0, long Y0, long Z0, long X1, long Y1, long Z1)? BoundsNm(Solid s) =>
        s.IsEmpty ? null : BoundsNm(s.Vertices.Select(v => new Vec3((long)Math.Round(v.X), (long)Math.Round(v.Y), (long)Math.Round(v.Z))).ToArray());

    private static (long X0, long Y0, long Z0, long X1, long Y1, long Z1)? BoundsNm(Vec3[] pts)
    {
        if (pts.Length == 0) return null;
        long x0 = long.MaxValue, y0 = long.MaxValue, z0 = long.MaxValue, x1 = long.MinValue, y1 = long.MinValue, z1 = long.MinValue;
        foreach (var p in pts)
        {
            x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
            x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
        }
        return (x0, y0, z0, x1, y1, z1);
    }

    // Grid bounds widened by 2 nm, so rounding of exact (homogeneous) vertices to the grid cannot hide a contact.
    private static bool Overlaps((long X0, long Y0, long Z0, long X1, long Y1, long Z1)? a, (long X0, long Y0, long Z0, long X1, long Y1, long Z1)? b) =>
        a is { } p && b is { } q &&
        p.X0 <= q.X1 + 2 && q.X0 <= p.X1 + 2 && p.Y0 <= q.Y1 + 2 && q.Y0 <= p.Y1 + 2 && p.Z0 <= q.Z1 + 2 && q.Z0 <= p.Z1 + 2;

    private static bool Overlaps(Solid a, Solid b) =>
        a.BoundsMm is { } p && b.BoundsMm is { } q &&
        p.MinX <= q.MaxX && q.MinX <= p.MaxX && p.MinY <= q.MaxY && q.MinY <= p.MaxY && p.MinZ <= q.MaxZ && q.MinZ <= p.MaxZ;

    private static double Diameter(Vec3[] pts)
    {
        double x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
        double z0 = pts.Min(p => p.Z), z1 = pts.Max(p => p.Z);
        return Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0) + (z1 - z0) * (z1 - z0));
    }

    private static double Angle(Pose3 a, Pose3 b)
    {
        // Rotation angle of R_b · R_aᵀ from its trace.
        double trace = b.R00 * a.R00 + b.R01 * a.R01 + b.R02 * a.R02
                     + b.R10 * a.R10 + b.R11 * a.R11 + b.R12 * a.R12
                     + b.R20 * a.R20 + b.R21 * a.R21 + b.R22 * a.R22;
        return Math.Acos(Math.Clamp((trace - 1) / 2, -1, 1));
    }

    internal static List<Pose3> Sample(Func<double, Pose3> seg, Vec3[] probe, double diameter, double maxDeviationNm)
    {
        var poses = new List<Pose3> { seg(0) };
        var stack = new Stack<(double T0, double T1, Pose3 P0, Pose3 P1, int Depth)>();
        stack.Push((0, 1, seg(0), seg(1), 0));
        var accepted = new List<(double T, Pose3 P)>();
        while (stack.Count > 0)
        {
            var (t0, t1, p0, p1, depth) = stack.Pop();
            double angle = Angle(p0, p1);
            bool ok = depth >= 40 ||
                      (diameter * angle / 2 <= maxDeviationNm && MaxDeviation(seg, t0, t1, p0, p1, probe) <= maxDeviationNm);
            if (ok)
            {
                accepted.Add((t1, p1));
                continue;
            }
            double tm = (t0 + t1) / 2;
            var pm = seg(tm);
            stack.Push((tm, t1, pm, p1, depth + 1));
            stack.Push((t0, tm, p0, pm, depth + 1));
        }
        foreach (var (_, p) in accepted.OrderBy(a => a.T)) poses.Add(p);
        if (poses.Count == 2 && poses[0] == poses[1]) poses.RemoveAt(1);
        return poses;
    }

    private static double MaxDeviation(Func<double, Pose3> seg, double t0, double t1, Pose3 p0, Pose3 p1, Vec3[] probe)
    {
        double worst = 0;
        foreach (double f in new[] { 0.25, 0.5, 0.75 })
        {
            var pm = seg(t0 + (t1 - t0) * f);
            foreach (var v in probe)
            {
                var (ax, ay, az) = p0.Apply(v.X, v.Y, v.Z);
                var (bx, by, bz) = p1.Apply(v.X, v.Y, v.Z);
                var (mx, my, mz) = pm.Apply(v.X, v.Y, v.Z);
                double cx = ax + (bx - ax) * f, cy = ay + (by - ay) * f, cz = az + (bz - az) * f;
                worst = Math.Max(worst, Math.Sqrt((mx - cx) * (mx - cx) + (my - cy) * (my - cy) + (mz - cz) * (mz - cz)));
            }
        }
        return worst;
    }
}
