using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Cutting;

/// <summary>Result of a 3D cut.</summary>
/// <param name="Remaining">Stock after the cut.</param>
/// <param name="Removed">Material removed (stock ∩ swept volume).</param>
/// <param name="Swept">Volume swept by the tool.</param>
/// <param name="RemovedVolumeMm3">Removed volume in mm³.</param>
/// <param name="MaxDepthMm">Extent of the removed material along the depth direction (default −z), in mm.</param>
public sealed record CutResult(Solid Remaining, Solid Removed, Solid Swept, double RemovedVolumeMm3, double MaxDepthMm);

/// <summary>Material removal of a tool moving along a path in 3D.</summary>
public static class Cutter
{
    /// <summary>Removes the volume swept by <paramref name="tool"/> along <paramref name="path"/> from <paramref name="stock"/>.</summary>
    public static CutResult Cut(Solid stock, Tool tool, ToolPath path, Tolerance? tol = null)
    {
        tol ??= Tolerance.Default;
        var pts = path.Points;
        Solid swept = pts.Count == 1
            ? tool.SweepLinear(pts[0], pts[0], tol)
            : tool.SweepLinear(pts[0], pts[1], tol);
        for (int i = 1; i + 1 < pts.Count; i++)
            swept = swept | tool.SweepLinear(pts[i], pts[i + 1], tol);
        var (remaining, removed) = stock.Split(swept);
        return new CutResult(remaining, removed, swept, removed.VolumeMm3, DepthAlongZ(removed));
    }

    /// <summary>max z − min z over the vertices of <paramref name="s"/>, in mm (exact up to the final conversion).</summary>
    public static double DepthAlongZ(Solid s)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var v in s.Vertices)
        {
            min = Math.Min(min, v.Z);
            max = Math.Max(max, v.Z);
        }
        return min > max ? 0 : Units.NmToMm(max - min);
    }
}
