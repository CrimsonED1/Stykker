namespace Stykker.NanoCut.Geometry2D;

/// <summary>Exact convex hull (Andrew's monotone chain) on grid points.</summary>
public static class ConvexHull
{
    /// <summary>Counter-clockwise hull vertices without collinear points.</summary>
    public static Vec2[] Compute(IEnumerable<Vec2> points)
    {
        var p = points.Distinct().ToArray();
        Array.Sort(p);
        if (p.Length < 3) return p;
        var hull = new Vec2[2 * p.Length];
        int k = 0;
        for (int i = 0; i < p.Length; i++)
        {
            while (k >= 2 && Predicates.Orient2D(hull[k - 2], hull[k - 1], p[i]) <= 0) k--;
            hull[k++] = p[i];
        }
        for (int i = p.Length - 2, t = k + 1; i >= 0; i--)
        {
            while (k >= t && Predicates.Orient2D(hull[k - 2], hull[k - 1], p[i]) <= 0) k--;
            hull[k++] = p[i];
        }
        return hull[..(k - 1)];
    }

    /// <summary>True if the contour is convex (either orientation, collinear vertices allowed).</summary>
    public static bool IsConvex(IReadOnlyList<Vec2> contour)
    {
        int n = contour.Count;
        if (n < 3) return false;
        int sign = 0;
        for (int i = 0; i < n; i++)
        {
            int o = Predicates.Orient2D(contour[i], contour[(i + 1) % n], contour[(i + 2) % n]);
            if (o == 0) continue;
            if (sign == 0) sign = o;
            else if (o != sign) return false;
        }
        return sign != 0;
    }
}
