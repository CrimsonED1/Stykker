namespace Stykker.NanoCut.Geometry2D;

/// <summary>
/// Minkowski sums of convex tools with paths: the area swept by a convex shape moving along a polyline.
/// For a straight segment the swept area is exactly the convex hull of the start and end positions,
/// so the result is exact on the grid (no sampling along the path).
/// </summary>
public static class Minkowski2
{
    /// <summary>
    /// Area swept by the convex <paramref name="tool"/> (coordinates relative to its reference point)
    /// whose reference point follows <paramref name="path"/>.
    /// </summary>
    public static Region2 SweepConvex(IReadOnlyList<Vec2> tool, IReadOnlyList<Vec2> path, bool closed = false) =>
        SweepConvex(tool, [path], closed);

    /// <summary>Union of the areas swept along several paths.</summary>
    public static Region2 SweepConvex(IReadOnlyList<Vec2> tool, IEnumerable<IReadOnlyList<Vec2>> paths, bool closed)
    {
        var hullTool = ConvexHull.Compute(tool);
        if (hullTool.Length < 3) throw new ArgumentException("Tool must enclose an area.", nameof(tool));
        var pieces = new List<IReadOnlyList<Vec2>>();
        foreach (var path in paths)
        {
            int n = path.Count;
            if (n == 0) continue;
            if (n == 1)
            {
                pieces.Add(Translate(hullTool, path[0]));
                continue;
            }
            int segments = closed ? n : n - 1;
            for (int i = 0; i < segments; i++)
            {
                Vec2 a = path[i], b = path[(i + 1) % n];
                pieces.Add(a == b ? Translate(hullTool, a) : SegmentHull(hullTool, a, b));
            }
        }
        var loops = BooleanKernel.Execute(pieces, FillRule.NonZero, [], FillRule.NonZero, BooleanOp.Union);
        return Region2.FromKernel(loops);
    }

    /// <summary>Exact convex hull of the tool at a and at b.</summary>
    public static Vec2[] SegmentHull(IReadOnlyList<Vec2> convexTool, Vec2 a, Vec2 b)
    {
        var pts = new Vec2[convexTool.Count * 2];
        for (int i = 0; i < convexTool.Count; i++)
        {
            pts[2 * i] = Vec2.Nm(convexTool[i].X + a.X, convexTool[i].Y + a.Y);
            pts[2 * i + 1] = Vec2.Nm(convexTool[i].X + b.X, convexTool[i].Y + b.Y);
        }
        return ConvexHull.Compute(pts);
    }

    private static Vec2[] Translate(Vec2[] pts, Vec2 d)
    {
        var r = new Vec2[pts.Length];
        for (int i = 0; i < r.Length; i++) r[i] = Vec2.Nm(pts[i].X + d.X, pts[i].Y + d.Y);
        return r;
    }
}
