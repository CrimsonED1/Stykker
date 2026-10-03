using Stykker.NanoCut.Geometry2D;

namespace Stykker.NanoCut.Cutting;

/// <summary>
/// Planar machining process: an acting shape (any region, decomposed into convex parts) moves relative to one
/// or more workpieces. Used directly for planar processes (gear shaping/generating, wire cutting, 2.5D) and as
/// the core of turning (in the r–z plane).
/// </summary>
/// <remarks>
/// The motion is sampled adaptively: an interval is accepted when no tool point deviates from the straight
/// chord between the interval's poses by more than <see cref="Tolerance.SweepNm"/> (checked at the convex hull
/// vertices, where the deviation of a rigid motion is largest). Between two poses the tool's vertices move along
/// chords; the area swept is then exactly the union of the convex parts at both poses and the areas swept by their
/// edges. (A convex hull of the two poses would overcut by up to edge length × rotation angle.)
/// </remarks>
public static class Process2
{
    /// <summary>Statistics of a process run.</summary>
    public sealed record Stats(int Intervals, int Pieces);

    /// <summary>Area swept by <paramref name="tool"/> under <paramref name="motion"/> (in the motion's frame).</summary>
    public static Region2 Sweep(Region2 tool, Motion2 motion, Tolerance? tol = null) =>
        Region2.UnionAll(SweepPieces(tool, motion, tol ?? Tolerance.Default, out _).Select(p => Region2.FromContours([new Contour2(p)])));

    /// <summary>
    /// Removes the area swept by <paramref name="tool"/> from each workpiece. The motion is the tool's motion
    /// relative to the workpieces (use <see cref="Motion2.Relative"/> for a moving workpiece).
    /// </summary>
    public static Region2[] Cut(IReadOnlyList<Region2> workpieces, Region2 tool, Motion2 motion, Tolerance? tol, out Stats stats)
    {
        tol ??= Tolerance.Default;
        var pieces = SweepPieces(tool, motion, tol, out int intervals);
        stats = new Stats(intervals, pieces.Count);
        var result = workpieces.Select(w => w.Normalize()).ToArray();
        for (int w = 0; w < result.Length; w++)
        {
            if (result[w].Bounds is not { } b) continue;
            var relevant = pieces.Where(p => Overlaps(p, b)).ToList();
            const int batch = 256;
            for (int i = 0; i < relevant.Count; i += batch)
            {
                var swept = Region2.UnionAll(relevant.Skip(i).Take(batch).Select(p => Region2.FromContours([new Contour2(p)])));
                result[w] = result[w] - swept;
            }
        }
        return result;
    }

    /// <summary>Single-workpiece convenience overload.</summary>
    public static Region2 Cut(Region2 workpiece, Region2 tool, Motion2 motion, Tolerance? tol = null) =>
        Cut([workpiece], tool, motion, tol, out _)[0];

    private static bool Overlaps(Vec2[] p, (Vec2 Min, Vec2 Max) b)
    {
        // One pass over the vertices instead of four LINQ passes: the filter runs on every piece of every sweep
        // (209 000 pieces on the gear case of docs/processes.md), where the enumerator overhead dominates.
        long x0 = long.MaxValue, x1 = long.MinValue, y0 = long.MaxValue, y1 = long.MinValue;
        foreach (var v in p)
        {
            if (v.X < x0) x0 = v.X;
            if (v.X > x1) x1 = v.X;
            if (v.Y < y0) y0 = v.Y;
            if (v.Y > y1) y1 = v.Y;
        }
        return x0 <= b.Max.X && x1 >= b.Min.X && y0 <= b.Max.Y && y1 >= b.Min.Y;
    }

    internal static List<Vec2[]> SweepPieces(Region2 tool, Motion2 motion, Tolerance tol, out int intervals)
    {
        var parts = tool.ConvexParts();
        var probe = ConvexHull.Compute(parts.SelectMany(p => p));
        var pieces = new List<Vec2[]>();
        intervals = 0;
        foreach (var seg in motion.Segments)
        {
            var timed = SampleTimed(seg, probe, tol.SweepNm);
            var poses = timed.Select(x => x.P).ToList();
            var placed = poses.Select(pose => parts.Select(part => part.Select(pose.Apply).ToArray()).ToArray()).ToArray();
            // The part at every sampled pose ...
            foreach (var atPose in placed)
                foreach (var part in atPose) pieces.Add(part);
            // ... plus the area swept by each edge between consecutive poses (vertices moving along chords).
            for (int i = 0; i + 1 < placed.Length; i++)
            {
                intervals++;
                double t0 = timed[i].T, t1 = timed[i + 1].T, h = (t1 - t0) * 1e-3;
                // Rotation centres at the start, over the whole step and at the end: the edge point closest to the
                // centre moves along the edge, and the swept area bulges there. That point can wander during the step.
                var centers = new (Pose2 At, (double X, double Y)? C)[]
                {
                    (poses[i], RotationCenter(seg(t0), seg(t0 + h))),
                    (poses[i], RotationCenter(poses[i], poses[i + 1])),
                    (poses[i + 1], RotationCenter(seg(t1 - h), seg(t1))),
                };
                for (int k = 0; k < parts.Count; k++)
                {
                    var part = parts[k];
                    for (int e = 0; e < part.Length; e++)
                    {
                        int f = (e + 1) % part.Length;
                        var lambdas = new List<double>();
                        foreach (var (at, c) in centers)
                            if (c is { } cc && FoldParameter(at, part[e], part[f], cc) is double l) lambdas.Add(l);
                        var chain = new List<double> { 0 };
                        if (lambdas.Count > 0)
                        {
                            // Split across the range the fold point covers (and a little around it).
                            double lo = lambdas.Min(), hi = lambdas.Max();
                            for (int j = 0; j <= 4; j++)
                            {
                                double l = lo + (hi - lo) * j / 4;
                                if (l > 1e-9 && l < 1 - 1e-9 && l - chain[^1] > 1e-9) chain.Add(l);
                            }
                        }
                        chain.Add(1);
                        for (int j = 0; j + 1 < chain.Count; j++)
                        {
                            var (ua, ub) = EdgePoint(part[e], part[f], chain[j], poses[i], poses[i + 1], placed[i][k][e], placed[i + 1][k][e], placed[i][k][f], placed[i + 1][k][f]);
                            var (va, vb) = EdgePoint(part[e], part[f], chain[j + 1], poses[i], poses[i + 1], placed[i][k][e], placed[i + 1][k][e], placed[i][k][f], placed[i + 1][k][f]);
                            AddEdgeSweep(pieces, ua, va, ub, vb);
                        }
                    }
                }
            }
        }
        return pieces.Where(p => p.Length >= 3).ToList();
    }

    /// <summary>Fixed point of the motion from pose a to pose b (the rotation centre), or null for a translation.</summary>
    private static (double X, double Y)? RotationCenter(Pose2 a, Pose2 b)
    {
        var t = b.Compose(a.Inverse());
        double c = Math.Cos(t.AngleRad), s = Math.Sin(t.AngleRad);
        double det = (1 - c) * (1 - c) + s * s;
        if (det < 1e-24) return null;
        // (I − R)·C = T  with  I − R = [[1 − c, s], [−s, 1 − c]].
        return (((1 - c) * t.TxNm - s * t.TyNm) / det, (s * t.TxNm + (1 - c) * t.TyNm) / det);
    }

    /// <summary>Parameter λ ∈ (0, 1) of the edge point (at pose a) closest to the rotation centre, if inside the edge.</summary>
    private static double? FoldParameter(Pose2 a, Vec2 p, Vec2 q, (double X, double Y) center)
    {
        var (px, py) = a.Apply(p.X, p.Y);
        var (qx, qy) = a.Apply(q.X, q.Y);
        double dx = qx - px, dy = qy - py, len2 = dx * dx + dy * dy;
        if (len2 == 0) return null;
        double lambda = ((center.X - px) * dx + (center.Y - py) * dy) / len2;
        return lambda > 1e-9 && lambda < 1 - 1e-9 ? lambda : null;
    }

    // Placed position of the edge point at parameter λ at both poses (vertices reuse the placed, rounded positions).
    private static (Vec2 A, Vec2 B) EdgePoint(Vec2 p, Vec2 q, double lambda, Pose2 a, Pose2 b, Vec2 pa, Vec2 pb, Vec2 qa, Vec2 qb)
    {
        if (lambda <= 0) return (pa, pb);
        if (lambda >= 1) return (qa, qb);
        double wx = p.X + lambda * (q.X - p.X), wy = p.Y + lambda * (q.Y - p.Y);
        return (Round(a.Apply(wx, wy)), Round(b.Apply(wx, wy)));
    }

    private static Vec2 Round((double X, double Y) p) =>
        Vec2.Nm((long)Math.Round(p.X, MidpointRounding.AwayFromZero), (long)Math.Round(p.Y, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Area covered by segment u(t)v(t) with u, v moving linearly from (u0, v0) to (u1, v1): the quadrilateral
    /// u0 v0 v1 u1, or two triangles if it is self-intersecting. Pieces are added counter-clockwise.
    /// </summary>
    private static void AddEdgeSweep(List<Vec2[]> pieces, Vec2 u0, Vec2 v0, Vec2 u1, Vec2 v1)
    {
        if (u0 == u1 && v0 == v1) return;
        if (Crosses(u0, v0, v1, u1, out var x))
        {
            // The two positions of the edge cross at x.
            AddCcw(pieces, [u0, x, u1]);
            AddCcw(pieces, [v0, v1, x]);
        }
        else if (Crosses(v0, v1, u1, u0, out var y))
        {
            // The two vertex paths cross at y.
            AddCcw(pieces, [u0, v0, y]);
            AddCcw(pieces, [y, v1, u1]);
        }
        else AddCcw(pieces, [u0, v0, v1, u1]);
    }

    private static bool Crosses(Vec2 a, Vec2 b, Vec2 c, Vec2 d, out Vec2 x)
    {
        x = default;
        int d1 = Predicates.Orient2D(c, d, a), d2 = Predicates.Orient2D(c, d, b);
        int d3 = Predicates.Orient2D(a, b, c), d4 = Predicates.Orient2D(a, b, d);
        if (!(d1 * d2 < 0 && d3 * d4 < 0)) return false;
        x = BooleanKernel.RoundedIntersection(a, b, c, d);
        return true;
    }

    private static void AddCcw(List<Vec2[]> pieces, Vec2[] poly)
    {
        var clean = BooleanKernel.RemoveCollinear(poly.ToList());
        if (clean.Length < 3) return;
        Int128 area = 0;
        for (int i = 0; i < clean.Length; i++) area += Vec2.Cross(clean[i], clean[(i + 1) % clean.Length]);
        if (area == 0) return;
        if (area < 0) Array.Reverse(clean);
        pieces.Add(clean);
    }

    /// <summary>Adaptive poses so that every probe point stays within <paramref name="maxDeviationNm"/> of its chords.</summary>
    internal static List<Pose2> Sample(Func<double, Pose2> seg, Vec2[] probe, double maxDeviationNm) =>
        SampleTimed(seg, probe, maxDeviationNm).Select(x => x.P).ToList();

    /// <summary>As <see cref="Sample"/>, with the motion parameter of every pose.</summary>
    internal static List<(double T, Pose2 P)> SampleTimed(Func<double, Pose2> seg, Vec2[] probe, double maxDeviationNm)
    {
        var poses = new List<(double T, Pose2 P)> { (0, seg(0)) };
        var stack = new Stack<(double T0, double T1, Pose2 P0, Pose2 P1, int Depth)>();
        stack.Push((0, 1, seg(0), seg(1), 0));
        var accepted = new List<(double T, Pose2 P)>();
        while (stack.Count > 0)
        {
            var (t0, t1, p0, p1, depth) = stack.Pop();
            bool ok = depth >= 40 || (Math.Abs(p1.AngleRad - p0.AngleRad) <= 0.1 && MaxDeviation(seg, t0, t1, p0, p1, probe) <= maxDeviationNm);
            if (ok)
            {
                accepted.Add((t1, p1));
                continue;
            }
            double tm = (t0 + t1) / 2;
            var pm = seg(tm);
            stack.Push((tm, t1, pm, p1, depth + 1)); // processed after the left half
            stack.Push((t0, tm, p0, pm, depth + 1));
        }
        foreach (var x in accepted.OrderBy(a => a.T)) poses.Add(x);
        if (poses.Count == 2 && poses[0].P == poses[1].P) poses.RemoveAt(1);
        return poses;
    }

    private static double MaxDeviation(Func<double, Pose2> seg, double t0, double t1, Pose2 p0, Pose2 p1, Vec2[] probe)
    {
        double worst = 0;
        foreach (double f in new[] { 0.25, 0.5, 0.75 })
        {
            var pm = seg(t0 + (t1 - t0) * f);
            foreach (var v in probe)
            {
                var (ax, ay) = p0.Apply(v.X, v.Y);
                var (bx, by) = p1.Apply(v.X, v.Y);
                var (mx, my) = pm.Apply(v.X, v.Y);
                double cx = ax + (bx - ax) * f, cy = ay + (by - ay) * f;
                worst = Math.Max(worst, Math.Sqrt((mx - cx) * (mx - cx) + (my - cy) * (my - cy)));
            }
        }
        return worst;
    }
}
