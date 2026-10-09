using System.Globalization;
using System.Text;

namespace Stykker.NanoCut.Geometry2D;

/// <summary>Deviation of a profile from its nominal shape.</summary>
/// <param name="MaxExcessMm">Largest distance of material outside the nominal profile (too much material).</param>
/// <param name="MaxShortfallMm">Largest distance of nominal material that is missing (too little material).</param>
/// <param name="ExcessAreaMm2">Exact area of actual − nominal.</param>
/// <param name="MissingAreaMm2">Exact area of nominal − actual.</param>
/// <param name="Points">Signed deviation at the sampled boundary points: positive = excess, negative = missing.</param>
public sealed record ProfileDeviation(double MaxExcessMm, double MaxShortfallMm, double ExcessAreaMm2, double MissingAreaMm2,
    IReadOnlyList<(Vec2 Point, double DeviationMm)> Points)
{
    /// <summary>Largest absolute deviation.</summary>
    public double MaxAbsMm => Math.Max(MaxExcessMm, MaxShortfallMm);
}

/// <summary>
/// Measurements on 2D profiles (for example sections of solids): heights and radii along a range, polar radii,
/// roughness, deviation from a nominal profile, and CSV export. Distances are evaluated in double precision from the
/// exact grid coordinates (sub-nm).
/// </summary>
public static class Profile2
{
    /// <summary>
    /// Largest v of the region at <paramref name="samples"/> equally spaced u from u0 to u1 (mm), e.g. the top
    /// surface of a section. NaN where the line u = const misses the region.
    /// </summary>
    public static double[] Heights(Region2 region, double u0Mm, double u1Mm, int samples) =>
        Extent(region, u0Mm, u1Mm, samples, swap: false, max: true);

    /// <summary>Smallest v of the region along u (bottom line); see <see cref="Heights"/>.</summary>
    public static double[] Depths(Region2 region, double u0Mm, double u1Mm, int samples) =>
        Extent(region, u0Mm, u1Mm, samples, swap: false, max: false);

    /// <summary>
    /// Largest u of the region at <paramref name="samples"/> equally spaced v from v0 to v1 (mm): for an axial section
    /// (u = radius, v = axial position) this is the outer radius along the axis.
    /// </summary>
    public static double[] Radii(Region2 region, double v0Mm, double v1Mm, int samples) =>
        Extent(region, v0Mm, v1Mm, samples, swap: true, max: true);

    /// <summary>Smallest u of the region along v (e.g. the bore radius of an axial section).</summary>
    public static double[] InnerRadii(Region2 region, double v0Mm, double v1Mm, int samples) =>
        Extent(region, v0Mm, v1Mm, samples, swap: true, max: false);

    /// <summary>
    /// Distance (mm) from <paramref name="centerMm"/> to the farthest (<paramref name="outer"/>) or nearest boundary
    /// crossing along rays at <paramref name="samples"/> equally spaced angles from a0 to a1 (rad), e.g. the radius
    /// around the axis in a radial section. NaN where the ray misses the boundary.
    /// </summary>
    public static double[] Polar(Region2 region, (double U, double V) centerMm, double a0Rad, double a1Rad, int samples,
        bool outer = true)
    {
        if (samples < 1) throw new ArgumentOutOfRangeException(nameof(samples));
        var edges = Edges(region);
        double cu = centerMm.U * Units.NmPerMm, cv = centerMm.V * Units.NmPerMm;
        var r = new double[samples];
        for (int i = 0; i < samples; i++)
        {
            double a = samples == 1 ? a0Rad : a0Rad + (a1Rad - a0Rad) * i / (samples - 1);
            double du = Math.Cos(a), dv = Math.Sin(a), best = double.NaN;
            foreach (var (pu, pv, qu, qv) in edges)
            {
                // Ray c + t·d against segment p + s·(q − p).
                double eu = qu - pu, ev = qv - pv;
                double den = du * ev - dv * eu;
                if (den == 0) continue;
                double wu = pu - cu, wv = pv - cv;
                double t = (wu * ev - wv * eu) / den, s = (wu * dv - wv * du) / den;
                if (t < 0 || s < 0 || s > 1) continue;
                if (double.IsNaN(best) || (outer ? t > best : t < best)) best = t;
            }
            r[i] = best / Units.NmPerMm;
        }
        return r;
    }

    /// <summary>
    /// Arithmetic mean roughness Ra and peak-to-valley height Rz (single sampling length, not the ISO five-length
    /// mean) of a profile, measured from its mean line; NaN samples are ignored.
    /// </summary>
    public static (double Ra, double Rz) Roughness(IReadOnlyList<double> heights)
    {
        var h = heights.Where(v => !double.IsNaN(v)).ToArray();
        if (h.Length == 0) return (double.NaN, double.NaN);
        double mean = h.Average();
        return (h.Average(v => Math.Abs(v - mean)), h.Max() - h.Min());
    }

    /// <summary>
    /// Deviation of <paramref name="actual"/> from <paramref name="nominal"/>. Distances are evaluated at the vertices and
    /// edge midpoints of both boundaries; the excess and missing areas are exact.
    /// </summary>
    public static ProfileDeviation Deviation(Region2 actual, Region2 nominal)
    {
        actual = actual.Normalize();
        nominal = nominal.Normalize();
        var nominalEdges = Edges(nominal);
        var actualEdges = Edges(actual);
        var points = new List<(Vec2, double)>();
        double excess = 0, shortfall = 0;
        foreach (var p in Samples(actual))
        {
            double d = Distance(p, nominalEdges) / Units.NmPerMm;
            double signed = nominal.Locate(p) == 0 ? d : -d; // outside nominal = excess material
            points.Add((p, signed));
            if (signed > 0) excess = Math.Max(excess, signed);
            else shortfall = Math.Max(shortfall, -signed);
        }
        foreach (var p in Samples(nominal))
        {
            double d = Distance(p, actualEdges) / Units.NmPerMm;
            if (actual.Locate(p) == 0) shortfall = Math.Max(shortfall, d); // nominal boundary outside the actual part
            else if (actual.Locate(p) == 1) excess = Math.Max(excess, d);
        }
        return new ProfileDeviation(excess, shortfall, (actual - nominal).AreaMm2, (nominal - actual).AreaMm2, points);
    }

    /// <summary>The contours as CSV: contour, index, u_mm, v_mm.</summary>
    public static string ToCsv(Region2 region)
    {
        var sb = new StringBuilder("contour,index,u_mm,v_mm\n");
        var contours = region.Normalize().Contours;
        for (int c = 0; c < contours.Count; c++)
            for (int i = 0; i < contours[c].Count; i++)
                sb.Append(c).Append(',').Append(i).Append(',')
                  .Append(contours[c][i].XMm.ToString("0.#########", CultureInfo.InvariantCulture)).Append(',')
                  .Append(contours[c][i].YMm.ToString("0.#########", CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    /// <summary>A sampled profile as CSV with the given column names.</summary>
    public static string ToCsv(IReadOnlyList<double> at, IReadOnlyList<double> values, string atName = "s_mm", string valueName = "h_mm")
    {
        if (at.Count != values.Count) throw new ArgumentException("Lengths differ.");
        var sb = new StringBuilder().Append(atName).Append(',').Append(valueName).Append('\n');
        for (int i = 0; i < at.Count; i++)
            sb.Append(at[i].ToString("0.#########", CultureInfo.InvariantCulture)).Append(',')
              .Append(double.IsNaN(values[i]) ? "" : values[i].ToString("0.#########", CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Equally spaced sample positions from a to b (mm), matching the sampled profiles above.</summary>
    public static double[] Positions(double aMm, double bMm, int samples) =>
        Enumerable.Range(0, samples).Select(i => samples == 1 ? aMm : aMm + (bMm - aMm) * i / (samples - 1)).ToArray();

    private static double[] Extent(Region2 region, double aMm, double bMm, int samples, bool swap, bool max)
    {
        if (samples < 1) throw new ArgumentOutOfRangeException(nameof(samples));
        var edges = Edges(region);
        var result = new double[samples];
        for (int i = 0; i < samples; i++)
        {
            double x = (samples == 1 ? aMm : aMm + (bMm - aMm) * i / (samples - 1)) * Units.NmPerMm, best = double.NaN;
            foreach (var e in edges)
            {
                var (pu, pv, qu, qv) = swap ? (e.Pv, e.Pu, e.Qv, e.Qu) : e;
                double lo = Math.Min(pu, qu), hi = Math.Max(pu, qu);
                if (x < lo || x > hi) continue;
                if (pu == qu)
                {
                    Take(pv);
                    Take(qv);
                }
                else Take(pv + (qv - pv) * (x - pu) / (qu - pu));
            }
            result[i] = best / Units.NmPerMm;

            void Take(double v)
            {
                if (double.IsNaN(best) || (max ? v > best : v < best)) best = v;
            }
        }
        return result;
    }

    private static List<(double Pu, double Pv, double Qu, double Qv)> Edges(Region2 region)
    {
        var list = new List<(double, double, double, double)>();
        foreach (var c in region.Normalize().Contours)
            for (int i = 0; i < c.Count; i++)
            {
                var p = c[i];
                var q = c[(i + 1) % c.Count];
                list.Add((p.X, p.Y, q.X, q.Y));
            }
        return list;
    }

    private static IEnumerable<Vec2> Samples(Region2 region)
    {
        foreach (var c in region.Contours)
            for (int i = 0; i < c.Count; i++)
            {
                var p = c[i];
                var q = c[(i + 1) % c.Count];
                yield return p;
                yield return new Vec2((p.X + q.X) / 2, (p.Y + q.Y) / 2);
            }
    }

    private static double Distance(Vec2 p, List<(double Pu, double Pv, double Qu, double Qv)> edges)
    {
        double best = double.PositiveInfinity;
        foreach (var (pu, pv, qu, qv) in edges)
        {
            double eu = qu - pu, ev = qv - pv, wu = p.X - pu, wv = p.Y - pv;
            double len2 = eu * eu + ev * ev;
            double t = len2 == 0 ? 0 : Math.Clamp((wu * eu + wv * ev) / len2, 0, 1);
            double du = wu - t * eu, dv = wv - t * ev;
            best = Math.Min(best, du * du + dv * dv);
        }
        return Math.Sqrt(best);
    }
}
