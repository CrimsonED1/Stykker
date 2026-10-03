namespace Stykker.NanoCut.Gpu;

/// <summary>
/// One tool step: a ball of <see cref="RadiusMm"/> moves in a straight line from <see cref="From"/> to
/// <see cref="To"/>, so the material it can remove is the capsule (swept ball) between the two positions.
/// Coordinates are absolute millimetres.
/// </summary>
/// <remarks>
/// This is the preview model of one entry of an expanded bench scene, where the exact kernel subtracts the convex
/// hull of two polyhedral balls instead. The polyhedron lies inside the sphere, so a Z-map of capsules removes
/// slightly more material than the exact kernel; see <c>docs/gpu-findings.md</c>.
/// </remarks>
public readonly record struct BallStep(
    (double X, double Y, double Z) From,
    (double X, double Y, double Z) To,
    double RadiusMm)
{
    /// <summary>A step that does not move: the ball at <paramref name="at"/> (a plunge or a dwell).</summary>
    public static BallStep At((double X, double Y, double Z) at, double radiusMm) => new(at, at, radiusMm);
}
