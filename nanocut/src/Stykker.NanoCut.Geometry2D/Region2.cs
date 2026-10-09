namespace Stykker.NanoCut.Geometry2D;

/// <summary>
/// An immutable planar region: a set of closed contours on the 1 nm grid plus a fill rule.
/// Results of Boolean operations are normalised: non-crossing contours, outer boundaries
/// counter-clockwise, holes clockwise, no collinear vertices (fill rule <see cref="FillRule.NonZero"/>).
/// </summary>
public sealed class Region2
{
    private Region2(IReadOnlyList<Contour2> contours, FillRule fillRule, bool normalized)
    {
        Contours = contours;
        FillRule = fillRule;
        IsNormalized = normalized;
    }

    /// <summary>The empty region.</summary>
    public static Region2 Empty { get; } = new([], FillRule.NonZero, normalized: true);

    /// <summary>The contours.</summary>
    public IReadOnlyList<Contour2> Contours { get; }

    /// <summary>Fill rule that defines the inside of <see cref="Contours"/>.</summary>
    public FillRule FillRule { get; }

    /// <summary>True if the contours are a normalised result (see class remarks).</summary>
    public bool IsNormalized { get; }

    /// <summary>True if the region has no area.</summary>
    public bool IsEmpty => Normalize().Contours.Count == 0;

    /// <summary>Creates a region from arbitrary (possibly self-intersecting) contours.</summary>
    public static Region2 FromContours(IEnumerable<Contour2> contours, FillRule fillRule = FillRule.NonZero) =>
        new(contours.Where(c => c.Count >= 3).ToArray(), fillRule, normalized: false);

    /// <summary>Creates a region from one polygon.</summary>
    public static Region2 Polygon(params Vec2[] points) => FromContours([new Contour2(points)]);

    /// <summary>Axis-aligned rectangle between two corners.</summary>
    public static Region2 Rectangle(Vec2 min, Vec2 max, Tolerance? tol = null)
    {
        _ = tol; // exact on the grid; the parameter keeps the API uniform
        long x0 = Math.Min(min.X, max.X), x1 = Math.Max(min.X, max.X);
        long y0 = Math.Min(min.Y, max.Y), y1 = Math.Max(min.Y, max.Y);
        if (x0 == x1 || y0 == y1) return Empty;
        return new Region2([new Contour2([new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)])], FillRule.NonZero, true);
    }

    /// <summary>
    /// Circle as an inscribed polygon whose sagitta is at most <see cref="Tolerance.ChordNm"/>.
    /// </summary>
    public static Region2 Circle(Vec2 center, double radiusMm, Tolerance? tol = null)
    {
        tol ??= Tolerance.Default;
        var pts = Shapes2.CirclePoints(center, radiusMm * Units.NmPerMm, tol.ChordNm);
        return FromContours([new Contour2(pts)]);
    }

    /// <summary>Boolean operation with another region.</summary>
    public Region2 Boolean(Region2 clip, BooleanOp op)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var loops = BooleanKernel.Execute(Contours, FillRule, clip.Contours, clip.FillRule, op);
        return FromKernel(loops);
    }

    /// <summary>Union of many regions in a single pass.</summary>
    public static Region2 UnionAll(IEnumerable<Region2> regions)
    {
        var list = regions.ToList();
        if (list.Count == 0) return Empty;
        if (list.Any(r => !r.IsNormalized))
            list = list.Select(r => r.Normalize()).ToList();
        // Normalised regions have winding 0/1, so the union is "winding > 0" of all contours together.
        var loops = BooleanKernel.Execute(list.SelectMany(r => r.Contours), FillRule.Positive, [], FillRule.NonZero, BooleanOp.Union);
        return FromKernel(loops);
    }

    internal static Region2 FromKernel(List<Vec2[]> loops) =>
        new(loops.Select(l => new Contour2(l, trusted: true)).ToArray(), FillRule.NonZero, normalized: true);

    /// <summary>The same point set with normalised contours.</summary>
    public Region2 Normalize() => IsNormalized ? this : Boolean(Empty, BooleanOp.Union);

    /// <summary>Intersection.</summary>
    public static Region2 operator &(Region2 a, Region2 b) => a.Boolean(b, BooleanOp.Intersection);

    /// <summary>Union.</summary>
    public static Region2 operator |(Region2 a, Region2 b) => a.Boolean(b, BooleanOp.Union);

    /// <summary>Difference.</summary>
    public static Region2 operator -(Region2 a, Region2 b) => a.Boolean(b, BooleanOp.Difference);

    /// <summary>Symmetric difference.</summary>
    public static Region2 operator ^(Region2 a, Region2 b) => a.Boolean(b, BooleanOp.Xor);

    /// <summary>Twice the area in nm², exact.</summary>
    public Int128 TwiceAreaNm2
    {
        get
        {
            Int128 s = 0;
            foreach (var c in Normalize().Contours) s += c.TwiceSignedAreaNm2;
            return s;
        }
    }

    /// <summary>Area in mm² (from the exact integer area).</summary>
    public double AreaMm2 => (double)TwiceAreaNm2 / (2.0 * Units.NmPerMm * Units.NmPerMm);

    /// <summary>Total boundary length in mm.</summary>
    public double PerimeterMm => Normalize().Contours.Sum(c => c.PerimeterMm);

    /// <summary>Axis-aligned bounds, or null if the region has no contours.</summary>
    public (Vec2 Min, Vec2 Max)? Bounds
    {
        get
        {
            if (Contours.Count == 0) return null;
            long x0 = long.MaxValue, y0 = long.MaxValue, x1 = long.MinValue, y1 = long.MinValue;
            foreach (var c in Contours)
                foreach (var p in c.Points)
                {
                    x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y);
                    x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
                }
            return (new Vec2(x0, y0), new Vec2(x1, y1));
        }
    }

    /// <summary>The region moved by a rigid motion; vertices are rounded to the grid (≤ 0.71 nm).</summary>
    public Region2 Transform(Pose2 pose)
    {
        var contours = Contours.Select(c => new Contour2(c.Points.ToArray().Select(pose.Apply))).ToArray();
        return new Region2(contours, FillRule, normalized: false);
    }

    /// <summary>Convex polygons covering the region exactly (counter-clockwise).</summary>
    public List<Vec2[]> ConvexParts() => Triangulator2.ConvexParts(this);

    /// <summary>The region translated by <paramref name="offset"/>.</summary>
    public Region2 Translate(Vec2 offset) =>
        new(Contours.Select(c => c.Translated(offset)).ToArray(), FillRule, IsNormalized);

    /// <summary>
    /// Winding-based point test: 1 inside, 0 outside, -1 exactly on the boundary (exact).
    /// </summary>
    public int Locate(Vec2 p)
    {
        var region = Normalize();
        int winding = 0;
        foreach (var c in region.Contours)
        {
            int n = c.Count;
            for (int i = 0; i < n; i++)
            {
                Vec2 a = c[i], b = c[(i + 1) % n];
                int o = Predicates.Orient2D(a, b, p);
                if (o == 0 && Vec2.Dot(a - p, b - p) <= 0) return -1;
                if (a.Y <= p.Y)
                {
                    if (b.Y > p.Y && o > 0) winding++;
                }
                else if (b.Y <= p.Y && o < 0) winding--;
            }
        }
        return winding != 0 ? 1 : 0;
    }

    /// <summary>
    /// Offsets the region by <paramref name="deltaMm"/> (positive grows, negative shrinks) with round corners.
    /// The disc is an inscribed polygon with sagitta ≤ <see cref="Tolerance.ChordNm"/>, so the offset
    /// distance is reached up to the chord error (one-sided: never more than |delta|).
    /// </summary>
    public Region2 Offset(double deltaMm, Tolerance? tol = null)
    {
        tol ??= Tolerance.Default;
        var self = Normalize();
        if (deltaMm == 0 || self.Contours.Count == 0) return self;
        var disc = Shapes2.CirclePoints(default, Math.Abs(deltaMm) * Units.NmPerMm, tol.ChordNm);
        var band = Minkowski2.SweepConvex(disc, self.Contours.Select(c => (IReadOnlyList<Vec2>)c), closed: true);
        return deltaMm > 0 ? self | band : self - band;
    }

    /// <summary>
    /// Contours as polylines in mm relative to <paramref name="origin"/> (interleaved x, y as float32),
    /// ready for three.js LineLoop or Babylon.js CreateLines.
    /// </summary>
    public float[][] ToPolylines(Vec2 origin = default) =>
        Contours.Select(c =>
        {
            var a = new float[c.Count * 2];
            for (int i = 0; i < c.Count; i++)
            {
                a[2 * i] = (float)Units.NmToMm(c[i].X - origin.X);
                a[2 * i + 1] = (float)Units.NmToMm(c[i].Y - origin.Y);
            }
            return a;
        }).ToArray();

    /// <summary>Simple SVG document (mm units, y up) for inspection.</summary>
    public string ToSvg(double strokeWidthMm = 0.02)
    {
        var region = Normalize();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var b = region.Bounds ?? (default, default);
        double x0 = b.Min.XMm, y0 = b.Min.YMm, w = b.Max.XMm - x0, h = b.Max.YMm - y0;
        double m = Math.Max(w, h) * 0.05 + strokeWidthMm;
        var sb = new System.Text.StringBuilder();
        sb.Append(inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{x0 - m} {-(y0 + h) - m} {w + 2 * m} {h + 2 * m}\">");
        sb.Append(inv, $"<path fill=\"#9ab\" fill-rule=\"nonzero\" stroke=\"#234\" stroke-width=\"{strokeWidthMm}\" d=\"");
        foreach (var c in region.Contours)
        {
            for (int i = 0; i < c.Count; i++)
                sb.Append(inv, $"{(i == 0 ? "M" : "L")}{c[i].XMm} {-c[i].YMm} ");
            sb.Append("Z ");
        }
        sb.Append("\"/></svg>");
        return sb.ToString();
    }
}
