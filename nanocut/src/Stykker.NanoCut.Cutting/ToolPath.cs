namespace Stykker.NanoCut.Cutting;

/// <summary>A 3D tool path (currently straight moves; arcs follow in phase 4).</summary>
public sealed class ToolPath
{
    private readonly List<Vec3> _points;

    private ToolPath(List<Vec3> points) => _points = points;

    /// <summary>Straight move from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static ToolPath Linear(Vec3 from, Vec3 to) => new([from, to]);

    /// <summary>Polyline through the given points.</summary>
    public static ToolPath Polyline(params Vec3[] points)
    {
        if (points.Length == 0) throw new ArgumentException("At least one point required.", nameof(points));
        return new([.. points]);
    }

    /// <summary>The path points.</summary>
    public IReadOnlyList<Vec3> Points => _points;
}
