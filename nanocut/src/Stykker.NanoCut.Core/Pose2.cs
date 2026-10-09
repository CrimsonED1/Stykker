namespace Stykker.NanoCut;

/// <summary>
/// A rigid motion of the plane: rotation by <see cref="AngleRad"/> about the origin, then translation by
/// (<see cref="TxNm"/>, <see cref="TyNm"/>). Poses are evaluated in double precision; applying a pose to grid
/// points rounds the result to the grid (≤ 0.71 nm, part of the numeric budget).
/// </summary>
public readonly record struct Pose2(double AngleRad, double TxNm, double TyNm)
{
    /// <summary>The identity.</summary>
    public static Pose2 Identity => default;

    /// <summary>Pure translation (mm).</summary>
    public static Pose2 TranslationMm(double dxMm, double dyMm) => new(0, dxMm * Units.NmPerMm, dyMm * Units.NmPerMm);

    /// <summary>Rotation by <paramref name="angleRad"/> about <paramref name="center"/>.</summary>
    public static Pose2 Rotation(double angleRad, Vec2 center = default)
    {
        double c = Math.Cos(angleRad), s = Math.Sin(angleRad);
        return new(angleRad, center.X - (c * center.X - s * center.Y), center.Y - (s * center.X + c * center.Y));
    }

    /// <summary>Applies the pose to a point (double, nm).</summary>
    public (double X, double Y) Apply(double x, double y)
    {
        double c = Math.Cos(AngleRad), s = Math.Sin(AngleRad);
        return (c * x - s * y + TxNm, s * x + c * y + TyNm);
    }

    /// <summary>Applies the pose to a grid point and rounds to the grid.</summary>
    public Vec2 Apply(Vec2 p)
    {
        var (x, y) = Apply(p.X, p.Y);
        return Vec2.Nm(Round(x), Round(y));
    }

    /// <summary>this ∘ inner: first <paramref name="inner"/>, then this.</summary>
    public Pose2 Compose(Pose2 inner)
    {
        var (x, y) = Apply(inner.TxNm, inner.TyNm);
        return new(AngleRad + inner.AngleRad, x, y);
    }

    /// <summary>The inverse motion.</summary>
    public Pose2 Inverse()
    {
        double c = Math.Cos(-AngleRad), s = Math.Sin(-AngleRad);
        return new(-AngleRad, -(c * TxNm - s * TyNm), -(s * TxNm + c * TyNm));
    }

    internal static long Round(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);
}

/// <summary>
/// A rigid motion of space: rotation matrix R (row-major, double) then translation T (nm).
/// Applying it to grid points rounds to the grid (≤ 0.87 nm).
/// </summary>
public readonly record struct Pose3(
    double R00, double R01, double R02,
    double R10, double R11, double R12,
    double R20, double R21, double R22,
    double TxNm, double TyNm, double TzNm)
{
    /// <summary>The identity.</summary>
    public static Pose3 Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0);

    /// <summary>Pure translation (mm).</summary>
    public static Pose3 TranslationMm(double dx, double dy, double dz) =>
        Identity with { TxNm = dx * Units.NmPerMm, TyNm = dy * Units.NmPerMm, TzNm = dz * Units.NmPerMm };

    /// <summary>Rotation by <paramref name="angleRad"/> (right-hand rule) about the axis through <paramref name="point"/> with direction (ax, ay, az).</summary>
    public static Pose3 Rotation(double angleRad, double ax, double ay, double az, Vec3 point = default)
    {
        double l = Math.Sqrt(ax * ax + ay * ay + az * az);
        if (l == 0) throw new ArgumentException("Axis must not be zero.");
        double x = ax / l, y = ay / l, z = az / l, c = Math.Cos(angleRad), s = Math.Sin(angleRad), k = 1 - c;
        var r = new Pose3(
            c + x * x * k, x * y * k - z * s, x * z * k + y * s,
            y * x * k + z * s, c + y * y * k, y * z * k - x * s,
            z * x * k - y * s, z * y * k + x * s, c + z * z * k, 0, 0, 0);
        var (px, py, pz) = r.Apply(point.X, point.Y, point.Z);
        return r with { TxNm = point.X - px, TyNm = point.Y - py, TzNm = point.Z - pz };
    }

    /// <summary>Pose from a unit quaternion (x, y, z, w) and a translation in mm.</summary>
    public static Pose3 FromQuaternion(double qx, double qy, double qz, double qw, double txMm, double tyMm, double tzMm)
    {
        double n = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
        if (n == 0) throw new ArgumentException("Quaternion must not be zero.");
        qx /= n; qy /= n; qz /= n; qw /= n;
        return new Pose3(
            1 - 2 * (qy * qy + qz * qz), 2 * (qx * qy - qz * qw), 2 * (qx * qz + qy * qw),
            2 * (qx * qy + qz * qw), 1 - 2 * (qx * qx + qz * qz), 2 * (qy * qz - qx * qw),
            2 * (qx * qz - qy * qw), 2 * (qy * qz + qx * qw), 1 - 2 * (qx * qx + qy * qy),
            txMm * Units.NmPerMm, tyMm * Units.NmPerMm, tzMm * Units.NmPerMm);
    }

    /// <summary>The rotation as a unit quaternion (x, y, z, w) with w ≥ 0.</summary>
    public (double X, double Y, double Z, double W) Quaternion()
    {
        double x, y, z, w, trace = R00 + R11 + R22;
        if (trace > 0)
        {
            double s = Math.Sqrt(trace + 1) * 2;
            w = s / 4; x = (R21 - R12) / s; y = (R02 - R20) / s; z = (R10 - R01) / s;
        }
        else if (R00 > R11 && R00 > R22)
        {
            double s = Math.Sqrt(1 + R00 - R11 - R22) * 2;
            w = (R21 - R12) / s; x = s / 4; y = (R01 + R10) / s; z = (R02 + R20) / s;
        }
        else if (R11 > R22)
        {
            double s = Math.Sqrt(1 + R11 - R00 - R22) * 2;
            w = (R02 - R20) / s; x = (R01 + R10) / s; y = s / 4; z = (R12 + R21) / s;
        }
        else
        {
            double s = Math.Sqrt(1 + R22 - R00 - R11) * 2;
            w = (R10 - R01) / s; x = (R02 + R20) / s; y = (R12 + R21) / s; z = s / 4;
        }
        return w < 0 ? (-x, -y, -z, -w) : (x, y, z, w);
    }

    /// <summary>
    /// Screw-free interpolation between two poses: translation linear, rotation by spherical linear interpolation
    /// (constant angular speed about a fixed axis).
    /// </summary>
    public static Pose3 Interpolate(Pose3 a, Pose3 b, double t)
    {
        var qa = a.Quaternion();
        var qb = b.Quaternion();
        double dot = qa.X * qb.X + qa.Y * qb.Y + qa.Z * qb.Z + qa.W * qb.W;
        if (dot < 0) { qb = (-qb.X, -qb.Y, -qb.Z, -qb.W); dot = -dot; }
        double wa, wb;
        if (dot > 0.9999999)
        {
            wa = 1 - t; wb = t;
        }
        else
        {
            double theta = Math.Acos(Math.Min(1, dot)), sin = Math.Sin(theta);
            wa = Math.Sin((1 - t) * theta) / sin;
            wb = Math.Sin(t * theta) / sin;
        }
        var r = FromQuaternion(wa * qa.X + wb * qb.X, wa * qa.Y + wb * qb.Y, wa * qa.Z + wb * qb.Z, wa * qa.W + wb * qb.W, 0, 0, 0);
        return r with
        {
            TxNm = a.TxNm + (b.TxNm - a.TxNm) * t,
            TyNm = a.TyNm + (b.TyNm - a.TyNm) * t,
            TzNm = a.TzNm + (b.TzNm - a.TzNm) * t,
        };
    }

    /// <summary>Applies the pose to a point (double, nm).</summary>
    public (double X, double Y, double Z) Apply(double x, double y, double z) =>
        (R00 * x + R01 * y + R02 * z + TxNm, R10 * x + R11 * y + R12 * z + TyNm, R20 * x + R21 * y + R22 * z + TzNm);

    /// <summary>Applies the pose to a grid point and rounds to the grid.</summary>
    public Vec3 Apply(Vec3 p)
    {
        var (x, y, z) = Apply(p.X, p.Y, p.Z);
        return Vec3.Nm(Pose2.Round(x), Pose2.Round(y), Pose2.Round(z));
    }

    /// <summary>this ∘ inner: first <paramref name="inner"/>, then this.</summary>
    public Pose3 Compose(Pose3 inner)
    {
        var (tx, ty, tz) = Apply(inner.TxNm, inner.TyNm, inner.TzNm);
        return new(
            R00 * inner.R00 + R01 * inner.R10 + R02 * inner.R20, R00 * inner.R01 + R01 * inner.R11 + R02 * inner.R21, R00 * inner.R02 + R01 * inner.R12 + R02 * inner.R22,
            R10 * inner.R00 + R11 * inner.R10 + R12 * inner.R20, R10 * inner.R01 + R11 * inner.R11 + R12 * inner.R21, R10 * inner.R02 + R11 * inner.R12 + R12 * inner.R22,
            R20 * inner.R00 + R21 * inner.R10 + R22 * inner.R20, R20 * inner.R01 + R21 * inner.R11 + R22 * inner.R21, R20 * inner.R02 + R21 * inner.R12 + R22 * inner.R22,
            tx, ty, tz);
    }

    /// <summary>The inverse motion.</summary>
    public Pose3 Inverse()
    {
        // R^T, -R^T t
        return new(
            R00, R10, R20, R01, R11, R21, R02, R12, R22,
            -(R00 * TxNm + R10 * TyNm + R20 * TzNm),
            -(R01 * TxNm + R11 * TyNm + R21 * TzNm),
            -(R02 * TxNm + R12 * TyNm + R22 * TzNm));
    }
}
