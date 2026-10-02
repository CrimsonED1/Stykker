using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Cutting;

/// <summary>A 3D cutting tool, defined relative to its reference point (tool centre point).</summary>
public abstract class Tool
{
    /// <summary>Ball tool (ball-nose cutter); the reference point is the ball centre.</summary>
    public static Tool Ball(double radiusMm) => new BallTool(radiusMm);

    /// <summary>Volume swept by the tool when its reference point moves straight from a to b.</summary>
    internal abstract Solid SweepLinear(Vec3 a, Vec3 b, Tolerance tol);

    private sealed class BallTool(double radiusMm) : Tool
    {
        internal override Solid SweepLinear(Vec3 a, Vec3 b, Tolerance tol)
        {
            if (!(radiusMm > 0)) throw new ArgumentOutOfRangeException(nameof(radiusMm));
            return Solid.Capsule(a, b, radiusMm, tol);
        }
    }
}
