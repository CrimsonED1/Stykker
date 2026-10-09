namespace Stykker.NanoCut.Geometry2D;

/// <summary>Point generators for arcs and circles with controlled chord error.</summary>
public static class Shapes2
{
    /// <summary>
    /// Counter-clockwise vertices of a polygon inscribed in the circle, with sagitta ≤ <paramref name="chordErrorNm"/>.
    /// The segment count is rounded up to a multiple of 4 so that vertices lie exactly at 0°, 90°, 180°
    /// and 270° (axis-aligned extremes, e.g. the bottom of a ball tool, are exact).
    /// Vertices are rounded to the grid (≤ 0.71 nm).
    /// </summary>
    public static Vec2[] CirclePoints(Vec2 center, double radiusNm, double chordErrorNm)
    {
        int n = (Discretization.SegmentCount(radiusNm, chordErrorNm) + 3) / 4 * 4;
        var pts = new Vec2[n];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            pts[i] = Round(center, radiusNm * Math.Cos(a), radiusNm * Math.Sin(a));
        }
        return pts;
    }

    /// <summary>
    /// Vertices of an arc inscribed with sagitta ≤ <paramref name="chordErrorNm"/>, from
    /// <paramref name="startRad"/> sweeping <paramref name="sweepRad"/> (positive = counter-clockwise),
    /// including both end points.
    /// </summary>
    public static Vec2[] ArcPoints(Vec2 center, double radiusNm, double startRad, double sweepRad, double chordErrorNm)
    {
        int n = Discretization.SegmentCount(radiusNm, chordErrorNm, sweepRad);
        var pts = new Vec2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            double a = startRad + sweepRad * i / n;
            pts[i] = Round(center, radiusNm * Math.Cos(a), radiusNm * Math.Sin(a));
        }
        return pts;
    }

    private static Vec2 Round(Vec2 c, double dx, double dy) =>
        Vec2.Nm(c.X + (long)Math.Round(dx, MidpointRounding.AwayFromZero),
                c.Y + (long)Math.Round(dy, MidpointRounding.AwayFromZero));
}
