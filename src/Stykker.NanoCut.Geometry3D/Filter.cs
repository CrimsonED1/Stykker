namespace Stykker.NanoCut.Geometry3D;

/// <summary>
/// Floating-point filters for the exact predicates. A double evaluation decides the sign whenever its magnitude
/// exceeds a conservative bound on the accumulated rounding error (inputs carry a relative error of a few ulps,
/// each operation adds one); otherwise <see cref="Uncertain"/> is returned and the caller evaluates exactly.
/// The bound 1e-11 × Σ|terms| is about four orders of magnitude above the actual worst case (~1e-15).
/// </summary>
internal static class Filter
{
    public const int Uncertain = int.MinValue;
    public const double Rel = 1e-11;

    /// <summary>Sign of n·p + d.</summary>
    public static int Sign(in Plane3 plane, double x, double y, double z)
    {
        double a = (double)plane.Nx * x, b = (double)plane.Ny * y, c = (double)plane.Nz * z, d = (double)plane.D;
        double v = a + b + c + d;
        double bound = Rel * (Math.Abs(a) + Math.Abs(b) + Math.Abs(c) + Math.Abs(d));
        return v > bound ? 1 : v < -bound ? -1 : Uncertain;
    }

    /// <summary>
    /// Sign of n·p + d for coefficients at <paramref name="o"/> in <paramref name="k"/> (n_x, n_y, n_z, d). The point's
    /// coordinates may carry an absolute error up to <paramref name="pointErr"/> (e.g. after averaging large values).
    /// </summary>
    public static int Sign(double[] k, int o, double x, double y, double z, double pointErr = 0)
    {
        double a = k[o] * x, b = k[o + 1] * y, c = k[o + 2] * z, d = k[o + 3];
        double v = a + b + c + d;
        double bound = Rel * (Math.Abs(a) + Math.Abs(b) + Math.Abs(c) + Math.Abs(d))
                       + 2 * pointErr * (Math.Abs(k[o]) + Math.Abs(k[o + 1]) + Math.Abs(k[o + 2]));
        return v > bound ? 1 : v < -bound ? -1 : Uncertain;
    }

    /// <summary>
    /// Edge plane (at offset <paramref name="e"/>) evaluated where the ray y = cy, z = cz meets the support plane
    /// (offset 0) – coefficient array variant of <see cref="EdgeAtHit(in Plane3, in Plane3, double, double)"/>.
    /// </summary>
    public static int EdgeAtHit(double[] k, int e, double cy, double cz, double pointErr = 0)
    {
        double sy = k[1] * cy, sz = k[2] * cz, sd = k[3], sx = k[0];
        double xHit = -(sy + sz + sd) / sx;
        double ex = k[e], ey = k[e + 1] * cy, ez = k[e + 2] * cz, ed = k[e + 3];
        double g = ex * xHit + ey + ez + ed;
        // ∂g/∂cy = e_y − e_x·s_y/s_x, ∂g/∂cz = e_z − e_x·s_z/s_x: an error in the probe propagates with these weights.
        double ratio = Math.Abs(ex) / Math.Abs(sx);
        double bound = Rel * (Math.Abs(ex) * (Math.Abs(sy) + Math.Abs(sz) + Math.Abs(sd)) / Math.Abs(sx)
                              + Math.Abs(ey) + Math.Abs(ez) + Math.Abs(ed))
                       + 2 * pointErr * (Math.Abs(k[e + 1]) + ratio * Math.Abs(k[1]) + Math.Abs(k[e + 2]) + ratio * Math.Abs(k[2]));
        return g > bound ? 1 : g < -bound ? -1 : Uncertain;
    }

    /// <summary>
    /// Sign of the edge plane <paramref name="e"/> at the point where the ray y = cy, z = cz meets the support plane
    /// <paramref name="s"/> (Sx ≠ 0).
    /// </summary>
    public static int EdgeAtHit(in Plane3 s, in Plane3 e, double cy, double cz)
    {
        double sy = (double)s.Ny * cy, sz = (double)s.Nz * cz, sd = (double)s.D, sx = (double)s.Nx;
        double xHit = -(sy + sz + sd) / sx;
        double ex = (double)e.Nx, ey = (double)e.Ny * cy, ez = (double)e.Nz * cz, ed = (double)e.D;
        double g = ex * xHit + ey + ez + ed;
        double bound = Rel * (Math.Abs(ex) * (Math.Abs(sy) + Math.Abs(sz) + Math.Abs(sd)) / Math.Abs(sx)
                              + Math.Abs(ey) + Math.Abs(ez) + Math.Abs(ed));
        return g > bound ? 1 : g < -bound ? -1 : Uncertain;
    }
}
