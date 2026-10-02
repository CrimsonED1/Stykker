namespace Stykker.NanoCut.Geometry2D;

/// <summary>
/// A closed polygonal contour on the 1 nm grid. The last point connects back to the first.
/// </summary>
public sealed class Contour2 : IReadOnlyList<Vec2>
{
    private readonly Vec2[] _points;

    /// <summary>Creates a contour from grid points (copied).</summary>
    public Contour2(IEnumerable<Vec2> points)
    {
        _points = points.ToArray();
        foreach (var p in _points)
        {
            Units.CheckCoordinate(p.X, nameof(points));
            Units.CheckCoordinate(p.Y, nameof(points));
        }
    }

    internal Contour2(Vec2[] points, bool trusted)
    {
        _ = trusted;
        _points = points;
    }

    /// <summary>The vertices.</summary>
    public ReadOnlySpan<Vec2> Points => _points;

    /// <inheritdoc />
    public int Count => _points.Length;

    /// <inheritdoc />
    public Vec2 this[int index] => _points[index];

    /// <summary>Twice the signed area in nm² (exact): positive for counter-clockwise contours.</summary>
    public Int128 TwiceSignedAreaNm2
    {
        get
        {
            Int128 s = 0;
            int n = _points.Length;
            for (int i = 0; i < n; i++)
                s += Vec2.Cross(_points[i], _points[(i + 1) % n]);
            return s;
        }
    }

    /// <summary>Signed area in mm² (positive for counter-clockwise).</summary>
    public double SignedAreaMm2 => (double)TwiceSignedAreaNm2 / (2.0 * Units.NmPerMm * Units.NmPerMm);

    /// <summary>True if the contour runs counter-clockwise (outer boundary in normalised results).</summary>
    public bool IsCounterClockwise => TwiceSignedAreaNm2 > 0;

    /// <summary>Perimeter in mm.</summary>
    public double PerimeterMm
    {
        get
        {
            double s = 0;
            int n = _points.Length;
            for (int i = 0; i < n; i++)
            {
                Vec2 d = _points[(i + 1) % n] - _points[i];
                s += Math.Sqrt((double)d.X * d.X + (double)d.Y * d.Y);
            }
            return Units.NmToMm(s);
        }
    }

    /// <summary>The same contour with reversed orientation.</summary>
    public Contour2 Reversed()
    {
        var p = (Vec2[])_points.Clone();
        Array.Reverse(p);
        return new Contour2(p, trusted: true);
    }

    /// <summary>The contour translated by <paramref name="offset"/>.</summary>
    public Contour2 Translated(Vec2 offset)
    {
        var p = new Vec2[_points.Length];
        for (int i = 0; i < p.Length; i++) p[i] = _points[i] + offset;
        return new Contour2(p);
    }

    /// <inheritdoc />
    public IEnumerator<Vec2> GetEnumerator() => ((IEnumerable<Vec2>)_points).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _points.GetEnumerator();
}
