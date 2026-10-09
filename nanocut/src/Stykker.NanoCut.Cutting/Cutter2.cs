using Stykker.NanoCut.Geometry2D;

namespace Stykker.NanoCut.Cutting;

/// <summary>Result of a 2D cut.</summary>
/// <param name="Remaining">Stock after the cut.</param>
/// <param name="Removed">Material removed (stock ∩ swept area).</param>
/// <param name="Swept">Total area swept by the tool.</param>
/// <param name="RemovedAreaMm2">Removed area in mm².</param>
/// <param name="MaxDepthMm">Extent of the removed material along the depth direction (default -y), in mm.</param>
public sealed record CutResult2(Region2 Remaining, Region2 Removed, Region2 Swept, double RemovedAreaMm2, double MaxDepthMm);

/// <summary>Material removal of a convex tool moving along a path in 2D.</summary>
public static class Cutter2
{
    /// <summary>
    /// Removes the area swept by <paramref name="tool"/> along <paramref name="path"/> from <paramref name="stock"/>.
    /// Depth is measured along <paramref name="depthDirection"/> (default -y).
    /// </summary>
    public static CutResult2 Cut(Region2 stock, Tool2 tool, ToolPath2 path, Tolerance? tol = null, Vec2? depthDirection = null)
    {
        tol ??= Tolerance.Default;
        var swept = Minkowski2.SweepConvex(tool.Outline(tol), path.Discretize(tol));
        var normalizedStock = stock.Normalize();
        var removed = normalizedStock & swept;
        var remaining = normalizedStock - swept;
        var (depth, _) = Penetration2.DepthAlong(removed, depthDirection ?? Penetration2.Down);
        return new CutResult2(remaining, removed, swept, removed.AreaMm2, depth);
    }
}
