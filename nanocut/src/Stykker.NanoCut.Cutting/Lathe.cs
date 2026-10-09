using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Cutting;

/// <summary>Result of a turning operation.</summary>
/// <param name="Profile">Remaining profile in the r–z half-plane (x = r, y = z).</param>
/// <param name="Part">The turned part as a solid of revolution about the z-axis.</param>
public sealed record TurnResult(Region2 Profile, Solid Part);

/// <summary>
/// Turning: the workpiece spins about the z-axis while the insert (cutting edge at centre height) moves in the
/// r–z plane. Everything a point of the insert removes during one revolution is a ring, so the process is computed
/// exactly in the r–z half-plane with <see cref="Process2"/> and revolved afterwards. Helical feed marks (scallops
/// of height ≈ f²/(8·r_ε) per revolution feed f) are not modelled, i.e. the result is the profile after a
/// continuous cut.
/// </summary>
public static class Lathe
{
    /// <summary>Profile of a cylindrical bar: radius r, from z0 to z1 (mm).</summary>
    public static Region2 BarProfile(double radiusMm, double z0Mm, double z1Mm) =>
        Region2.Rectangle(Vec2.Mm(0, z0Mm), Vec2.Mm(radiusMm, z1Mm));

    /// <summary>
    /// Turns <paramref name="stockProfile"/> with <paramref name="insert"/> (region in r–z, relative to its tool
    /// reference point) moving along <paramref name="path"/> (r–z motion of the reference point).
    /// </summary>
    public static TurnResult Turn(Region2 stockProfile, Region2 insert, Motion2 path, Tolerance? tol = null)
    {
        tol ??= Tolerance.Default;
        var profile = Process2.Cut(stockProfile, insert, path, tol);
        return new TurnResult(profile, Solid.Revolve(profile, tol));
    }
}
