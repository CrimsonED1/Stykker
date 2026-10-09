namespace Stykker.NanoCut;

/// <summary>
/// A point (or vector) on the 2D integer grid. One unit is one nanometre.
/// </summary>
public readonly record struct Vec2(long X, long Y) : IComparable<Vec2>
{
    /// <summary>Creates a grid point from millimetres, rounding to the nearest nanometre.</summary>
    public static Vec2 Mm(double x, double y) => new(Units.MmToNm(x), Units.MmToNm(y));

    /// <summary>Creates a grid point from nanometres and checks the coordinate range.</summary>
    public static Vec2 Nm(long x, long y) =>
        new(Units.CheckCoordinate(x, nameof(x)), Units.CheckCoordinate(y, nameof(y)));

    /// <summary>X in millimetres.</summary>
    public double XMm => Units.NmToMm(X);

    /// <summary>Y in millimetres.</summary>
    public double YMm => Units.NmToMm(Y);

    /// <summary>Component-wise sum.</summary>
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);

    /// <summary>Component-wise difference.</summary>
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);

    /// <summary>Negation.</summary>
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);

    /// <summary>Exact dot product.</summary>
    public static Int128 Dot(Vec2 a, Vec2 b) => (Int128)a.X * b.X + (Int128)a.Y * b.Y;

    /// <summary>Exact z component of the cross product.</summary>
    public static Int128 Cross(Vec2 a, Vec2 b) => (Int128)a.X * b.Y - (Int128)a.Y * b.X;

    /// <summary>Lexicographic order (X, then Y).</summary>
    public int CompareTo(Vec2 other)
    {
        int c = X.CompareTo(other.X);
        return c != 0 ? c : Y.CompareTo(other.Y);
    }

    /// <inheritdoc />
    public override string ToString() => $"({XMm:0.######}, {YMm:0.######}) mm";
}
