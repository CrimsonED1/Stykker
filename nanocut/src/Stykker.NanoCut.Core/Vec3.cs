namespace Stykker.NanoCut;

/// <summary>
/// A point (or vector) on the 3D integer grid. One unit is one nanometre.
/// </summary>
public readonly record struct Vec3(long X, long Y, long Z)
{
    /// <summary>Creates a grid point from millimetres, rounding to the nearest nanometre.</summary>
    public static Vec3 Mm(double x, double y, double z) => new(Units.MmToNm(x), Units.MmToNm(y), Units.MmToNm(z));

    /// <summary>Creates a grid point from nanometres and checks the coordinate range.</summary>
    public static Vec3 Nm(long x, long y, long z) =>
        new(Units.CheckCoordinate(x, nameof(x)), Units.CheckCoordinate(y, nameof(y)), Units.CheckCoordinate(z, nameof(z)));

    /// <summary>X in millimetres.</summary>
    public double XMm => Units.NmToMm(X);

    /// <summary>Y in millimetres.</summary>
    public double YMm => Units.NmToMm(Y);

    /// <summary>Z in millimetres.</summary>
    public double ZMm => Units.NmToMm(Z);

    /// <summary>Component-wise sum.</summary>
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>Component-wise difference.</summary>
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <inheritdoc />
    public override string ToString() => $"({XMm:0.######}, {YMm:0.######}, {ZMm:0.######}) mm";
}
