namespace Stykker.NanoCut;

/// <summary>
/// Segment counts for arcs from a prescribed chord error. Polygons are always inscribed, so the
/// deviation from the exact arc lies on one side only (material tends to remain).
/// </summary>
public static class Discretization
{
    /// <summary>
    /// Number of segments for a full circle so that the sagitta r(1 - cos(π/n)) does not exceed
    /// <paramref name="chordErrorNm"/>: θ = 2·acos(1 - s/r), n = ⌈2π/θ⌉ (at least 3).
    /// </summary>
    public static int SegmentCount(double radiusNm, double chordErrorNm) =>
        SegmentCount(radiusNm, chordErrorNm, 2 * Math.PI);

    /// <summary>
    /// Number of segments for an arc of <paramref name="sweepRad"/> radians so that the sagitta does
    /// not exceed <paramref name="chordErrorNm"/>. Full circles get at least 3 segments, other arcs at least 1.
    /// </summary>
    public static int SegmentCount(double radiusNm, double chordErrorNm, double sweepRad)
    {
        if (!(radiusNm > 0)) throw new ArgumentOutOfRangeException(nameof(radiusNm));
        if (!(chordErrorNm > 0)) throw new ArgumentOutOfRangeException(nameof(chordErrorNm));
        double sweep = Math.Abs(sweepRad);
        int min = sweep >= 2 * Math.PI - 1e-12 ? 3 : 1;
        if (chordErrorNm >= radiusNm) return min;
        double theta = 2 * Math.Acos(1 - chordErrorNm / radiusNm);
        int n = Math.Max(min, (int)Math.Ceiling(sweep / theta - 1e-9));
        // Guard against floating point at the ceiling boundary.
        while (Sagitta(radiusNm, sweep / n) > chordErrorNm) n++;
        return n;
    }

    /// <summary>Sagitta (maximum chord-to-arc distance) of a chord spanning <paramref name="angleRad"/>.</summary>
    public static double Sagitta(double radiusNm, double angleRad) => radiusNm * (1 - Math.Cos(angleRad / 2));
}
