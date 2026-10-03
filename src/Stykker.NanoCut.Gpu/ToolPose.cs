namespace Stykker.NanoCut.Gpu;

/// <summary>
/// A ball tool at a position, the query unit of <see cref="ZMap.ProbeMaterial"/>, in absolute millimetres. Packed
/// into the query buffer as four floats (x, y, z, r) relative to <see cref="ZMap.OriginMm"/>.
/// </summary>
/// <param name="X">Absolute x of the ball centre in mm.</param>
/// <param name="Y">Absolute y of the ball centre in mm.</param>
/// <param name="Z">Absolute z of the ball centre in mm.</param>
/// <param name="RadiusMm">Radius of the ball in mm.</param>
public readonly record struct ToolPose(double X, double Y, double Z, double RadiusMm)
{
    /// <summary>The pose of a <see cref="BallStep"/> at its start.</summary>
    /// <param name="step">The step, whose radius and start position are taken.</param>
    public static ToolPose From(BallStep step) => new(step.From.X, step.From.Y, step.From.Z, step.RadiusMm);
}