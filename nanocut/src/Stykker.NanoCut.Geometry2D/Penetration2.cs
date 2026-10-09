namespace Stykker.NanoCut.Geometry2D;

/// <summary>Result of a 2D penetration analysis.</summary>
/// <param name="Overlap">Region where both bodies overlap (normalised).</param>
/// <param name="AreaMm2">Penetration area in mm².</param>
/// <param name="DepthMm">Penetration depth: extent of the overlap along the penetration direction, in mm.</param>
/// <param name="DeepestPoint">Overlap vertex reaching furthest along the penetration direction.</param>
public sealed record Penetration2Result(Region2 Overlap, double AreaMm2, double DepthMm, Vec2? DeepestPoint);

/// <summary>Penetration area and depth between two regions.</summary>
public static class Penetration2
{
    /// <summary>Default penetration direction: -y (from the top face downwards).</summary>
    public static Vec2 Down => new(0, -1);

    /// <summary>
    /// Analyses how far <paramref name="intruder"/> penetrates <paramref name="body"/> along
    /// <paramref name="direction"/> (default <see cref="Down"/>).
    /// </summary>
    public static Penetration2Result Analyze(Region2 body, Region2 intruder, Vec2? direction = null)
    {
        var overlap = body & intruder;
        var (depth, deepest) = DepthAlong(overlap, direction ?? Down);
        return new Penetration2Result(overlap, overlap.AreaMm2, depth, deepest);
    }

    /// <summary>
    /// Extent of <paramref name="region"/> along <paramref name="direction"/> in mm:
    /// max(p·d) - min(p·d) over all vertices, with d normalised. For an entry surface perpendicular to d this is
    /// the largest distance of the new surface to the original surface. The extremes of a linear function on a
    /// polygon lie at vertices, so the value is exact up to the final division by |d|.
    /// </summary>
    public static (double DepthMm, Vec2? DeepestPoint) DepthAlong(Region2 region, Vec2 direction)
    {
        if (direction == default) throw new ArgumentException("Direction must not be zero.", nameof(direction));
        Int128 min = Int128.MaxValue, max = Int128.MinValue;
        Vec2? deepest = null;
        foreach (var c in region.Contours)
            foreach (var p in c.Points)
            {
                Int128 t = Vec2.Dot(p, direction);
                if (t < min) min = t;
                if (t > max) { max = t; deepest = p; }
            }
        if (deepest is null) return (0, null);
        double len = Math.Sqrt((double)direction.X * direction.X + (double)direction.Y * direction.Y);
        return (Units.NmToMm((double)(max - min) / len), deepest);
    }

    /// <summary>Euclidean distance (nm) from a point to the nearest boundary segment of a region.</summary>
    public static double DistanceToBoundaryNm(Vec2 p, Region2 region)
    {
        double best = double.PositiveInfinity;
        foreach (var c in region.Contours)
        {
            int n = c.Count;
            for (int i = 0; i < n; i++)
                best = Math.Min(best, DistanceToSegmentNm(p, c[i], c[(i + 1) % n]));
        }
        return best;
    }

    /// <summary>Euclidean distance (nm) from a point to a segment.</summary>
    public static double DistanceToSegmentNm(Vec2 p, Vec2 a, Vec2 b)
    {
        Vec2 ab = b - a, ap = p - a;
        Int128 len2 = Vec2.Dot(ab, ab);
        Int128 t = Vec2.Dot(ap, ab);
        if (len2 == 0 || t <= 0) return Length(ap);
        if (t >= len2) return Length(p - b);
        // Distance to the line: |cross| / |ab| (cross is exact).
        return Math.Abs((double)Vec2.Cross(ab, ap)) / Math.Sqrt((double)len2);
    }

    private static double Length(Vec2 v) => Math.Sqrt((double)v.X * v.X + (double)v.Y * v.Y);
}
