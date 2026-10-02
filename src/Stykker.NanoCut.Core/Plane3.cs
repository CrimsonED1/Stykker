namespace Stykker.NanoCut;

/// <summary>
/// A plane through three grid points, stored exactly as n·x + d = 0 with
/// n = (b - a) × (c - a) (|n_i| ≤ 2^65) and d = -n·a (|d| &lt; 2^98).
/// Faces in the 3D kernel are described by their supporting plane, never by rounded points.
/// </summary>
public readonly record struct Plane3(Int128 Nx, Int128 Ny, Int128 Nz, Int128 D)
{
    /// <summary>The plane through a, b, c; the normal points to the side from which a, b, c appear counter-clockwise.</summary>
    public static Plane3 FromPoints(Vec3 a, Vec3 b, Vec3 c)
    {
        long bx = b.X - a.X, by = b.Y - a.Y, bz = b.Z - a.Z;
        long cx = c.X - a.X, cy = c.Y - a.Y, cz = c.Z - a.Z;
        Int128 nx = (Int128)by * cz - (Int128)bz * cy;
        Int128 ny = (Int128)bz * cx - (Int128)bx * cz;
        Int128 nz = (Int128)bx * cy - (Int128)by * cx;
        Int128 d = -(nx * a.X + ny * a.Y + nz * a.Z);
        return new Plane3(nx, ny, nz, d);
    }

    /// <summary>The same plane with all coefficients divided by their greatest common divisor (orientation kept).</summary>
    public Plane3 Canonical()
    {
        UInt128 g = Gcd(Abs(Nx), Abs(Ny));
        if (g == 1) return this;
        g = Gcd(g, Abs(Nz));
        if (g == 1) return this;
        g = Gcd(g, Abs(D));
        if (g <= 1) return this;
        // g = 2^127 only when every coefficient is 0 or Int128.MinValue; (Int128)2^127 would wrap to a negative divisor.
        if (g >> 127 != 0) return new Plane3(Int128.Sign(Nx), Int128.Sign(Ny), Int128.Sign(Nz), Int128.Sign(D));
        Int128 gi = (Int128)g;
        return new Plane3(Nx / gi, Ny / gi, Nz / gi, D / gi);
    }

    /// <summary>The same plane with opposite orientation.</summary>
    public Plane3 Flipped() => new(-Nx, -Ny, -Nz, -D);

    private static UInt128 Abs(Int128 v) => v < 0 ? (UInt128)(-v) : (UInt128)v;

    // Binary GCD (shifts and subtractions only); 64-bit when both values fit, which is the common case.
    private static UInt128 Gcd(UInt128 a, UInt128 b)
    {
        if (a == 0) return b;
        if (b == 0) return a;
        if ((a >> 64) == 0 && (b >> 64) == 0) return Gcd64((ulong)a, (ulong)b);
        // One operand small: a single 128-bit remainder brings the other one down to 64 bits.
        if ((a >> 64) == 0) return Gcd64((ulong)a, (ulong)(b % a));
        if ((b >> 64) == 0) return Gcd64((ulong)b, (ulong)(a % b));
        int shift = (int)UInt128.TrailingZeroCount(a | b);
        a >>= (int)UInt128.TrailingZeroCount(a);
        do
        {
            b >>= (int)UInt128.TrailingZeroCount(b);
            if (a > b) (a, b) = (b, a);
            b -= a;
        } while (b != 0);
        return a << shift;
    }

    private static ulong Gcd64(ulong a, ulong b)
    {
        if (b == 0) return a;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(a | b);
        a >>= System.Numerics.BitOperations.TrailingZeroCount(a);
        do
        {
            b >>= System.Numerics.BitOperations.TrailingZeroCount(b);
            if (a > b) (a, b) = (b, a);
            b -= a;
        } while (b != 0);
        return a << shift;
    }

    /// <summary>True if the three defining points were collinear (no plane).</summary>
    public bool IsDegenerate => Nx == 0 && Ny == 0 && Nz == 0;

    /// <summary>n·p + d, exact (|value| &lt; 2^99).</summary>
    public Int128 Evaluate(Vec3 p) => Nx * p.X + Ny * p.Y + Nz * p.Z + D;

    /// <summary>
    /// Exact intersection point of three planes in homogeneous coordinates (Cramer's rule) with W &gt; 0, or
    /// null if the normals are linearly dependent.
    /// </summary>
    public static HomogeneousPoint3? Intersect(in Plane3 p, in Plane3 q, in Plane3 r)
    {
        if (InBudget(p) && InBudget(q) && InBudget(r)) return IntersectFast(p, q, r);
        return IntersectGeneric(p, q, r);
    }

    // Fast-path budget: |n| < 2^66 and |d| < 2^98, which covers every plane through grid points (docs/bit-budget.md).
    // Then |n × n| < 2^133, |W| < 2^201 and |X| < 2^233, far inside the 256-bit range.
    private static bool InBudget(in Plane3 p) =>
        Below(p.Nx, 66) && Below(p.Ny, 66) && Below(p.Nz, 66) && Below(p.D, 98);

    private static bool Below(Int128 v, int bits) => v > Int128.MinValue && (UInt128)(v < 0 ? -v : v) >> bits == 0;

    /// <summary>
    /// Same point as Cramer's rule, written with cross products: x = −(d_p (n_q × n_r) + d_q (n_r × n_p) + d_r (n_p × n_q))
    /// / (n_p · (n_q × n_r)), in fixed 256-bit arithmetic.
    /// </summary>
    internal static HomogeneousPoint3? IntersectFast(in Plane3 p, in Plane3 q, in Plane3 r)
    {
        var (ax, ay, az) = Cross(q, r);
        var (bx, by, bz) = Cross(r, p);
        var (cx, cy, cz) = Cross(p, q);
        var w = ax * p.Nx + ay * p.Ny + az * p.Nz;
        if (w.IsZero) return null;
        var x = (ax * p.D + bx * q.D + cx * r.D).Negate();
        var y = (ay * p.D + by * q.D + cy * r.D).Negate();
        var z = (az * p.D + bz * q.D + cz * r.D).Negate();
        if (w.IsNegative) { x = x.Negate(); y = y.Negate(); z = z.Negate(); w = w.Negate(); }
        return new HomogeneousPoint3(x.ToInt384(), y.ToInt384(), z.ToInt384(), w.ToInt384());
    }

    private static (Int256 X, Int256 Y, Int256 Z) Cross(in Plane3 a, in Plane3 b) => (
        Int256.Product(a.Ny, b.Nz) - Int256.Product(a.Nz, b.Ny),
        Int256.Product(a.Nz, b.Nx) - Int256.Product(a.Nx, b.Nz),
        Int256.Product(a.Nx, b.Ny) - Int256.Product(a.Ny, b.Nx));

    /// <summary>Cramer's rule in Int384 (any input within Int128).</summary>
    internal static HomogeneousPoint3? IntersectGeneric(in Plane3 p, in Plane3 q, in Plane3 r)
    {
        // Solve N x = -d with rows n_p, n_q, n_r.
        Int384 a11 = p.Nx, a12 = p.Ny, a13 = p.Nz, b1 = -(Int384)p.D;
        Int384 a21 = q.Nx, a22 = q.Ny, a23 = q.Nz, b2 = -(Int384)q.D;
        Int384 a31 = r.Nx, a32 = r.Ny, a33 = r.Nz, b3 = -(Int384)r.D;

        Int384 w = Det3(a11, a12, a13, a21, a22, a23, a31, a32, a33);
        if (w.IsZero) return null;
        Int384 x = Det3(b1, a12, a13, b2, a22, a23, b3, a32, a33);
        Int384 y = Det3(a11, b1, a13, a21, b2, a23, a31, b3, a33);
        Int384 z = Det3(a11, a12, b1, a21, a22, b2, a31, a32, b3);
        // Normalise to W > 0 so that callers can compare signs without tracking W.
        return w.Sign < 0 ? new HomogeneousPoint3(-x, -y, -z, -w) : new HomogeneousPoint3(x, y, z, w);
    }

    private static Int384 Det3(Int384 a, Int384 b, Int384 c, Int384 d, Int384 e, Int384 f, Int384 g, Int384 h, Int384 i) =>
        a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
}

/// <summary>
/// An exact point (X/W, Y/W, Z/W) in nm, created as the intersection of three planes. It is never
/// rounded, so repeated Booleans do not drift. Bounds: |X|,|Y|,|Z| &lt; 2^231, |W| &lt; 2^198.
/// </summary>
public readonly record struct HomogeneousPoint3(Int384 X, Int384 Y, Int384 Z, Int384 W)
{
    /// <summary>Approximate Cartesian position in mm (display only).</summary>
    public (double X, double Y, double Z) ToMm()
    {
        double w = (double)W * Units.NmPerMm;
        return ((double)X / w, (double)Y / w, (double)Z / w);
    }
}
