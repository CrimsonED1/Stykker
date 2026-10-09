namespace Stykker.NanoCut;

/// <summary>
/// Exact geometric predicates on grid coordinates. With |coordinate| ≤ 2^31 every difference is
/// at most 2^32, so orient2d needs ≤ 2^65 and orient3d ≤ 2^99: both fit in <see cref="Int128"/>.
/// The plane predicates on homogeneous points use <see cref="Int384"/> (docs/bit-budget.md).
/// </summary>
public static class Predicates
{
    /// <summary>
    /// Twice the signed area of triangle (a, b, c): positive if c lies left of the directed line a→b
    /// (counter-clockwise turn), negative if right, zero if collinear. Exact.
    /// </summary>
    public static Int128 Orient2DValue(Vec2 a, Vec2 b, Vec2 c)
    {
        long abx = b.X - a.X, aby = b.Y - a.Y;
        long acx = c.X - a.X, acy = c.Y - a.Y;
        return (Int128)abx * acy - (Int128)aby * acx;
    }

    /// <summary>Sign of <see cref="Orient2DValue"/>: +1 counter-clockwise, -1 clockwise, 0 collinear.</summary>
    public static int Orient2D(Vec2 a, Vec2 b, Vec2 c) => Int128.Sign(Orient2DValue(a, b, c));

    /// <summary>
    /// Six times the signed volume of tetrahedron (a, b, c, d), det[b-a, c-a, d-a]: positive if d lies on the
    /// side the normal (b - a) × (c - a) points to (above the counter-clockwise triangle), negative below,
    /// zero if coplanar. Note: this is the negative of Shewchuk's orient3d convention.
    /// </summary>
    public static Int128 Orient3DValue(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        long bx = b.X - a.X, by = b.Y - a.Y, bz = b.Z - a.Z;
        long cx = c.X - a.X, cy = c.Y - a.Y, cz = c.Z - a.Z;
        long dx = d.X - a.X, dy = d.Y - a.Y, dz = d.Z - a.Z;
        Int128 nx = (Int128)by * cz - (Int128)bz * cy;
        Int128 ny = (Int128)bz * cx - (Int128)bx * cz;
        Int128 nz = (Int128)bx * cy - (Int128)by * cx;
        return nx * dx + ny * dy + nz * dz;
    }

    /// <summary>Sign of <see cref="Orient3DValue"/>: +1 above the counter-clockwise triangle (a, b, c), -1 below, 0 coplanar.</summary>
    public static int Orient3D(Vec3 a, Vec3 b, Vec3 c, Vec3 d) => Int128.Sign(Orient3DValue(a, b, c, d));

    /// <summary>Side of a grid point relative to a plane: +1 on the normal side, -1 opposite, 0 on the plane.</summary>
    public static int Side(in Plane3 plane, Vec3 p) => Int128.Sign(plane.Evaluate(p));

    /// <summary>
    /// Side of an exact homogeneous point (typically the intersection of three planes) relative to a plane:
    /// sign(n·X + d·W) · sign(W). Exact; the intermediate value stays below 2^298.
    /// </summary>
    public static int Side(in Plane3 plane, in HomogeneousPoint3 p)
    {
        Int384 v = (Int384)plane.Nx * p.X + (Int384)plane.Ny * p.Y
                   + (Int384)plane.Nz * p.Z + (Int384)plane.D * p.W;
        return v.Sign * p.W.Sign;
    }
}
