using Stykker.NanoCut.Geometry2D;

namespace Stykker.NanoCut.Cutting;

/// <summary>A convex 2D tool cross-section, defined relative to its reference point (tool centre).</summary>
public abstract class Tool2
{
    /// <summary>Circular tool (e.g. ball or end mill seen along its axis).</summary>
    public static Tool2 Circle(double radiusMm) => new CircleTool(radiusMm);

    /// <summary>Convex polygonal tool; points in mm relative to the reference point.</summary>
    public static Tool2 ConvexPolygon(params (double X, double Y)[] pointsMm) =>
        new PolygonTool(pointsMm.Select(p => Vec2.Mm(p.X, p.Y)).ToArray());

    /// <summary>Grid outline of the tool for the given tolerance (counter-clockwise).</summary>
    public abstract Vec2[] Outline(Tolerance tol);

    private sealed class CircleTool(double radiusMm) : Tool2
    {
        public override Vec2[] Outline(Tolerance tol)
        {
            if (!(radiusMm > 0)) throw new ArgumentOutOfRangeException(nameof(radiusMm));
            return Shapes2.CirclePoints(default, radiusMm * Units.NmPerMm, tol.ChordNm);
        }
    }

    private sealed class PolygonTool(Vec2[] points) : Tool2
    {
        public override Vec2[] Outline(Tolerance tol)
        {
            if (!ConvexHull.IsConvex(points))
                throw new ArgumentException("Tool polygon must be convex.");
            return ConvexHull.Compute(points);
        }
    }
}
