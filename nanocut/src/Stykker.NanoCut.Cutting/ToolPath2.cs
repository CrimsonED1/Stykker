using Stykker.NanoCut.Geometry2D;

namespace Stykker.NanoCut.Cutting;

/// <summary>A 2D tool path made of line and arc moves.</summary>
public sealed class ToolPath2
{
    private readonly List<Func<Tolerance, Vec2, IEnumerable<Vec2>>> _moves = [];

    private ToolPath2(Vec2 start) => Start = start;

    /// <summary>Start point.</summary>
    public Vec2 Start { get; }

    /// <summary>Straight path from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static ToolPath2 Linear(Vec2 from, Vec2 to) => new ToolPath2(from).LineTo(to);

    /// <summary>Polyline through the given points.</summary>
    public static ToolPath2 Polyline(params Vec2[] points)
    {
        if (points.Length == 0) throw new ArgumentException("At least one point required.", nameof(points));
        var p = new ToolPath2(points[0]);
        foreach (var q in points.Skip(1)) p.LineTo(q);
        return p;
    }

    /// <summary>Starts a path at <paramref name="start"/>.</summary>
    public static ToolPath2 StartAt(Vec2 start) => new(start);

    /// <summary>Appends a straight move.</summary>
    public ToolPath2 LineTo(Vec2 to)
    {
        _moves.Add((_, _) => [to]);
        return this;
    }

    /// <summary>
    /// Appends an arc around <paramref name="center"/> sweeping <paramref name="sweepDeg"/> degrees
    /// (positive = counter-clockwise) from the current point. The arc is replaced by chords whose
    /// sagitta is at most <see cref="Tolerance.SweepNm"/>.
    /// </summary>
    public ToolPath2 ArcAround(Vec2 center, double sweepDeg)
    {
        _moves.Add((tol, current) =>
        {
            Vec2 d = current - center;
            double r = Math.Sqrt((double)d.X * d.X + (double)d.Y * d.Y);
            if (r == 0) return [];
            double start = Math.Atan2(d.Y, d.X);
            return Shapes2.ArcPoints(center, r, start, sweepDeg * Math.PI / 180, tol.SweepNm).Skip(1);
        });
        return this;
    }

    /// <summary>The path as grid points for the given tolerance.</summary>
    public IReadOnlyList<Vec2> Discretize(Tolerance tol)
    {
        var pts = new List<Vec2> { Start };
        foreach (var move in _moves)
            foreach (var p in move(tol, pts[^1]))
                if (p != pts[^1]) pts.Add(p);
        return pts;
    }
}
