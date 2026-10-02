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
        long x0 = p.Min(v => v.X), x1 = p.Max(v => v.X), y0 = p.Min(v => v.Y), y1 = p.Max(v => v.Y);
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
            var poses = Sample(seg, probe, tol.SweepNm);
            var placed = poses.Select(pose => parts.Select(part => part.Select(pose.Apply).ToArray()).ToArray()).ToArray();
            // The part at every sampled pose ...
            foreach (var atPose in placed)
                foreach (var part in atPose) pieces.Add(part);
            // ... plus the area swept by each edge between consecutive poses (vertices moving along chords).
            // Together they form the exact swept area of the linearly interpolated motion: no overcut, and the
            // deviation from the true motion is bounded by the chord deviation of the vertex paths.
            for (int i = 0; i + 1 < placed.Length; i++)
            {
                intervals++;
                for (int k = 0; k < parts.Count; k++)
                {
                    var a = placed[i][k];
                    var b = placed[i + 1][k];
                    for (int e = 0; e < a.Length; e++)
                        AddEdgeSweep(pieces, a[e], a[(e + 1) % a.Length], b[e], b[(e + 1) % a.Length]);
                }
            }
        }
        return pieces.Where(p => p.Length >= 3).ToList();
    }

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
    internal static List<Pose2> Sample(Func<double, Pose2> seg, Vec2[] probe, double maxDeviationNm)
    {
        var poses = new List<Pose2> { seg(0) };
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
        foreach (var (t, p) in accepted.OrderBy(a => a.T)) poses.Add(p);
        if (poses.Count == 2 && poses[0] == poses[1]) poses.RemoveAt(1);
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
