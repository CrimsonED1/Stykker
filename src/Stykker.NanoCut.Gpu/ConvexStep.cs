namespace Stykker.NanoCut.Gpu;

/// <summary>
/// The orientation of a tool: a rotation matrix in row-major order, without translation. A <see cref="ConvexStep"/>
/// carries one of these together with the two positions, because between two poses the tool is swept by the
/// translation alone — a rotation inside a step has to be sampled into steps of its own, which is what
/// <see cref="ConvexStep.StepsForRotation"/> sizes.
/// </summary>
/// <param name="M00">Row 0, column 0.</param>
/// <param name="M01">Row 0, column 1.</param>
/// <param name="M02">Row 0, column 2.</param>
/// <param name="M10">Row 1, column 0.</param>
/// <param name="M11">Row 1, column 1.</param>
/// <param name="M12">Row 1, column 2.</param>
/// <param name="M20">Row 2, column 0.</param>
/// <param name="M21">Row 2, column 1.</param>
/// <param name="M22">Row 2, column 2.</param>
public readonly record struct Orientation3(
    double M00, double M01, double M02,
    double M10, double M11, double M12,
    double M20, double M21, double M22)
{
    /// <summary>No rotation.</summary>
    public static Orientation3 Identity { get; } = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    /// <summary>A rotation by <paramref name="angleRad"/> (right-hand rule) about the z-axis.</summary>
    public static Orientation3 AboutZ(double angleRad)
    {
        double c = Math.Cos(angleRad), s = Math.Sin(angleRad);
        return new(c, -s, 0, s, c, 0, 0, 0, 1);
    }

    /// <summary>A rotation by <paramref name="angleRad"/> (right-hand rule) about the axis (ax, ay, az).</summary>
    public static Orientation3 AboutAxis(double angleRad, double ax, double ay, double az)
    {
        double l = Math.Sqrt(ax * ax + ay * ay + az * az);
        if (l == 0) throw new ArgumentException("The axis must not be the zero vector.", nameof(ax));
        double x = ax / l, y = ay / l, z = az / l, c = Math.Cos(angleRad), s = Math.Sin(angleRad), k = 1 - c;
        return new(c + x * x * k, x * y * k - z * s, x * z * k + y * s,
                   y * x * k + z * s, c + y * y * k, y * z * k - x * s,
                   z * x * k - y * s, z * y * k + x * s, c + z * z * k);
    }

    /// <summary>The direction a tool-frame vector points in after the rotation.</summary>
    public (double X, double Y, double Z) Apply(double x, double y, double z) =>
        (M00 * x + M01 * y + M02 * z, M10 * x + M11 * y + M12 * z, M20 * x + M21 * y + M22 * z);
}

/// <summary>
/// One step of a program with a convex tool: the tool in orientation <see cref="Orientation"/> moves from
/// <see cref="FromMm"/> to <see cref="ToMm"/>, and what it can remove is the polytope swept between the two — the
/// convex set <c>{ R·v + T(t) : v in the tool, 0 ≤ t ≤ 1 }</c>, which is convex, so a vertical line meets it in one
/// interval. That is the whole difference to <see cref="BallStep"/>, whose swept capsule is the same idea with a
/// sphere.
/// </summary>
public readonly record struct ConvexStep
{
    /// <summary>Creates a step.</summary>
    /// <param name="orientation">The tool's orientation, the same for both positions.</param>
    /// <param name="fromMm">Where the tool starts, absolute mm.</param>
    /// <param name="toMm">Where it ends, absolute mm.</param>
    public ConvexStep(Orientation3 orientation, (double X, double Y, double Z) fromMm, (double X, double Y, double Z) toMm)
    {
        Orientation = orientation;
        FromMm = fromMm;
        ToMm = toMm;
    }

    /// <summary>The tool's orientation during the step.</summary>
    public Orientation3 Orientation { get; init; }

    /// <summary>Where the tool starts, absolute mm.</summary>
    public (double X, double Y, double Z) FromMm { get; init; }

    /// <summary>Where it ends, absolute mm.</summary>
    public (double X, double Y, double Z) ToMm { get; init; }

    /// <summary>A step with the tool unrotated.</summary>
    public static ConvexStep Move((double X, double Y, double Z) fromMm, (double X, double Y, double Z) toMm) =>
        new(Orientation3.Identity, fromMm, toMm);

    /// <summary>A step that does not move: the tool standing at one place (a plunge or a dwell).</summary>
    public static ConvexStep At((double X, double Y, double Z) atMm) => new(Orientation3.Identity, atMm, atMm);

    /// <summary>
    /// How many steps a rotation of <paramref name="sweepRad"/> needs on a tool of the given corner radius, so that
    /// the sweep stays inside <paramref name="toleranceMm"/> of the truth. The outermost corner rides a circle of
    /// that radius, and a step rotating by d misses its chord by r·(1 − cos(d/2)) ≈ r·d²/8, so d ≤ √(8·tol/r).
    /// <para>
    /// This is the sampling a preview needs, not the exact kernel's: the exact kernel takes the convex hull of
    /// both poses, which covers the chord as well, so it can step over the same rotation in one piece. The preview
    /// has to be sampled finely enough that the corners it sweeps do not leave a scalloped surface behind.
    /// </para>
    /// </summary>
    public static int StepsForRotation(double sweepRad, double radiusMm, double toleranceMm)
    {
        if (!(sweepRad > 0)) return 1;
        if (!(radiusMm > 0) || !(toleranceMm > 0))
            throw new ArgumentOutOfRangeException(nameof(radiusMm), "a radius and a tolerance must be positive.");
        return (int)Math.Ceiling(sweepRad / Math.Sqrt(8 * toleranceMm / radiusMm) - 1e-9);
    }
}