namespace Stykker.NanoCut.Geometry3D;

/// <summary>
/// A cutting plane with its own 2D frame: a grid origin and two orthonormal in-plane directions U and V. A section
/// of a solid is returned in (u, v) coordinates on the nm grid; the region is counter-clockwise seen from the side
/// the normal U × V points to.
/// </summary>
/// <remarks>
/// The plane used for the exact intersection has an integer normal (U × V scaled to 2^40 and rounded), so for planes
/// that are not axis-aligned the frame is orthogonal to it within about 1e-12 rad – well below 1 nm over 1 m.
/// </remarks>
public sealed class SectionPlane
{
    private const double NormalScale = 1L << 40;

    /// <summary>Creates a plane through <paramref name="origin"/> spanned by <paramref name="u"/> and
    /// <paramref name="v"/> (normalised; V is made orthogonal to U).</summary>
    public SectionPlane(Vec3 origin, (double X, double Y, double Z) u, (double X, double Y, double Z) v)
    {
        Origin = origin;
        U = Normalize(u);
        var n = Cross(U, v);
        if (Length(n) < 1e-12) throw new ArgumentException("U and V must not be parallel.");
        Normal = Normalize(n);
        V = Cross(Normal, U);
        Plane = IntegerPlane(Normal, origin);
    }

    /// <summary>Grid point that maps to (0, 0).</summary>
    public Vec3 Origin { get; }

    /// <summary>First in-plane axis (unit vector).</summary>
    public (double X, double Y, double Z) U { get; }

    /// <summary>Second in-plane axis (unit vector, orthogonal to U).</summary>
    public (double X, double Y, double Z) V { get; }

    /// <summary>Unit normal U × V.</summary>
    public (double X, double Y, double Z) Normal { get; }

    /// <summary>The exact plane used for intersecting faces.</summary>
    public Plane3 Plane { get; }

    /// <summary>Plane z = <paramref name="zMm"/>, coordinates (x, y).</summary>
    public static SectionPlane XY(double zMm) => new(Vec3.Mm(0, 0, zMm), (1, 0, 0), (0, 1, 0));

    /// <summary>Plane y = <paramref name="yMm"/>, coordinates (x, z).</summary>
    public static SectionPlane XZ(double yMm) => new(Vec3.Mm(0, yMm, 0), (1, 0, 0), (0, 0, 1));

    /// <summary>Plane x = <paramref name="xMm"/>, coordinates (y, z).</summary>
    public static SectionPlane YZ(double xMm) => new(Vec3.Mm(xMm, 0, 0), (0, 1, 0), (0, 0, 1));

    /// <summary>
    /// Plane through an axis (turned parts): u is the radius in direction <paramref name="angleRad"/> around the
    /// axis, v the position along the axis measured from <paramref name="axisPoint"/>. Angle 0 is
    /// <paramref name="reference"/> (default: world X projected onto the plane normal to the axis, or Y if the axis is
    /// X). The half u &gt;= 0 is the profile at that angle; u &lt; 0 is the opposite side.
    /// </summary>
    public static SectionPlane Axial(Vec3 axisPoint, (double X, double Y, double Z) axis, double angleRad,
        (double X, double Y, double Z)? reference = null)
    {
        var (e1, e2, a) = AxisFrame(axis, reference);
        double c = Math.Cos(angleRad), s = Math.Sin(angleRad);
        var radial = (c * e1.X + s * e2.X, c * e1.Y + s * e2.Y, c * e1.Z + s * e2.Z);
        return new SectionPlane(axisPoint, radial, a);
    }

    /// <summary>
    /// Plane normal to an axis at <paramref name="atMm"/> along it (for roundness): u points to angle 0 (see
    /// <see cref="Axial"/>), v to angle 90°, so polar angles in (u, v) are angles around the axis.
    /// </summary>
    public static SectionPlane Radial(Vec3 axisPoint, (double X, double Y, double Z) axis, double atMm,
        (double X, double Y, double Z)? reference = null)
    {
        var (e1, e2, a) = AxisFrame(axis, reference);
        double t = atMm * Units.NmPerMm;
        var origin = new Vec3(axisPoint.X + (long)Math.Round(a.X * t), axisPoint.Y + (long)Math.Round(a.Y * t),
            axisPoint.Z + (long)Math.Round(a.Z * t));
        return new SectionPlane(origin, e1, e2);
    }

    /// <summary>
    /// Plane through the segment <paramref name="a"/> → <paramref name="b"/> and the direction
    /// <paramref name="up"/>: u runs along the segment from a (0 … length), v along up.
    /// </summary>
    public static SectionPlane Through(Vec3 a, Vec3 b, (double X, double Y, double Z) up) =>
        new(a, (b.X - a.X, b.Y - a.Y, b.Z - a.Z), up);

    /// <summary>In-plane coordinates (nm) of a point given in nm.</summary>
    public (double U, double V) ToPlane(double x, double y, double z)
    {
        double dx = x - Origin.X, dy = y - Origin.Y, dz = z - Origin.Z;
        return (dx * U.X + dy * U.Y + dz * U.Z, dx * V.X + dy * V.Y + dz * V.Z);
    }

    /// <summary>World position (nm grid) of the in-plane point <paramref name="p"/>.</summary>
    public Vec3 ToWorld(Vec2 p) => new(
        Origin.X + (long)Math.Round(p.X * U.X + p.Y * V.X),
        Origin.Y + (long)Math.Round(p.X * U.Y + p.Y * V.Y),
        Origin.Z + (long)Math.Round(p.X * U.Z + p.Y * V.Z));

    private static ((double X, double Y, double Z) E1, (double X, double Y, double Z) E2, (double X, double Y, double Z) A)
        AxisFrame((double X, double Y, double Z) axis, (double X, double Y, double Z)? reference)
    {
        var a = Normalize(axis);
        var r = reference ?? (Math.Abs(a.X) > 0.9 ? (0, 1, 0) : (1, 0, 0));
        double d = r.X * a.X + r.Y * a.Y + r.Z * a.Z;
        var e1 = (r.X - d * a.X, r.Y - d * a.Y, r.Z - d * a.Z);
        if (Length(e1) < 1e-9) throw new ArgumentException("The reference direction must not be parallel to the axis.");
        e1 = Normalize(e1);
        return (e1, Cross(a, e1), a);
    }

    private static Plane3 IntegerPlane((double X, double Y, double Z) n, Vec3 origin)
    {
        double m = Math.Max(Math.Abs(n.X), Math.Max(Math.Abs(n.Y), Math.Abs(n.Z)));
        Int128 nx = (Int128)Math.Round(n.X / m * NormalScale);
        Int128 ny = (Int128)Math.Round(n.Y / m * NormalScale);
        Int128 nz = (Int128)Math.Round(n.Z / m * NormalScale);
        Int128 d = -(nx * origin.X + ny * origin.Y + nz * origin.Z);
        return new Plane3(nx, ny, nz, d).Canonical();
    }

    internal static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static double Length((double X, double Y, double Z) v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    private static (double X, double Y, double Z) Normalize((double X, double Y, double Z) v)
    {
        double l = Length(v);
        if (l == 0) throw new ArgumentException("Zero direction.");
        return (v.X / l, v.Y / l, v.Z / l);
    }
}
