namespace Stykker.NanoCut.Cutting;

/// <summary>
/// A planar motion: one or more continuous segments, each a function t ∈ [0, 1] → <see cref="Pose2"/>.
/// The sweep is never interpolated across segment boundaries, so a segment may jump (e.g. a periodic tool
/// shifted back by one pitch).
/// </summary>
public sealed class Motion2
{
    private Motion2(IReadOnlyList<Func<double, Pose2>> segments) => Segments = segments;

    /// <summary>The continuous segments.</summary>
    public IReadOnlyList<Func<double, Pose2>> Segments { get; }

    /// <summary>A custom continuous motion.</summary>
    public static Motion2 Custom(Func<double, Pose2> pose) => new([pose]);

    /// <summary>A fixed pose.</summary>
    public static Motion2 Fixed(Pose2 pose) => Custom(_ => pose);

    /// <summary>Straight translation from <paramref name="from"/> to <paramref name="to"/> (no rotation).</summary>
    public static Motion2 Linear(Vec2 from, Vec2 to) =>
        Custom(t => new Pose2(0, from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t));

    /// <summary>Polyline translation through the given points (one segment per move).</summary>
    public static Motion2 Polyline(params Vec2[] points)
    {
        if (points.Length < 2) return Fixed(points.Length == 1 ? new Pose2(0, points[0].X, points[0].Y) : Pose2.Identity);
        return Sequence(Enumerable.Range(0, points.Length - 1).Select(i => Linear(points[i], points[i + 1])).ToArray());
    }

    /// <summary>Rotation about <paramref name="center"/> from <paramref name="fromRad"/> to <paramref name="toRad"/>.</summary>
    public static Motion2 Rotate(Vec2 center, double fromRad, double toRad) =>
        Custom(t => Pose2.Rotation(fromRad + (toRad - fromRad) * t, center));

    /// <summary>Segments of all motions one after another.</summary>
    public static Motion2 Sequence(params Motion2[] motions) => new(motions.SelectMany(m => m.Segments).ToArray());

    /// <summary>
    /// Relative motion of a tool in the frame of a moving workpiece: workpiece(t)⁻¹ ∘ tool(t), segment by segment
    /// (both motions must have the same number of segments, or one of them a single segment).
    /// </summary>
    public static Motion2 Relative(Motion2 tool, Motion2 workpiece)
    {
        int n = Math.Max(tool.Segments.Count, workpiece.Segments.Count);
        if (!(tool.Segments.Count == n || tool.Segments.Count == 1) || !(workpiece.Segments.Count == n || workpiece.Segments.Count == 1))
            throw new ArgumentException("Segment counts do not match.");
        var segs = new Func<double, Pose2>[n];
        for (int i = 0; i < n; i++)
        {
            var ts = tool.Segments[tool.Segments.Count == 1 ? 0 : i];
            var ws = workpiece.Segments[workpiece.Segments.Count == 1 ? 0 : i];
            segs[i] = t => ws(t).Inverse().Compose(ts(t));
        }
        return new Motion2(segs);
    }
}

/// <summary>
/// A spatial motion: one or more continuous segments, each a function t ∈ [0, 1] → <see cref="Pose3"/>.
/// </summary>
public sealed class Motion3
{
    private Motion3(IReadOnlyList<Func<double, Pose3>> segments) => Segments = segments;

    /// <summary>The continuous segments.</summary>
    public IReadOnlyList<Func<double, Pose3>> Segments { get; }

    /// <summary>A custom continuous motion.</summary>
    public static Motion3 Custom(Func<double, Pose3> pose) => new([pose]);

    /// <summary>A fixed pose.</summary>
    public static Motion3 Fixed(Pose3 pose) => Custom(_ => pose);

    /// <summary>Straight translation (mm) from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static Motion3 Linear(Vec3 from, Vec3 to) =>
        Custom(t => Pose3.Identity with
        {
            TxNm = from.X + (to.X - from.X) * t,
            TyNm = from.Y + (to.Y - from.Y) * t,
            TzNm = from.Z + (to.Z - from.Z) * t,
        });

    /// <summary>Polyline translation through the given points.</summary>
    public static Motion3 Polyline(params Vec3[] points) =>
        Sequence(Enumerable.Range(0, Math.Max(1, points.Length - 1))
            .Select(i => points.Length == 1 ? Linear(points[0], points[0]) : Linear(points[i], points[i + 1])).ToArray());

    /// <summary>From one pose to another: translation linear, rotation about a fixed axis (slerp).</summary>
    public static Motion3 Between(Pose3 from, Pose3 to) => Custom(t => Pose3.Interpolate(from, to, t));

    /// <summary>Rotation about the axis through <paramref name="point"/> with direction (ax, ay, az).</summary>
    public static Motion3 Rotate(Vec3 point, double ax, double ay, double az, double fromRad, double toRad) =>
        Custom(t => Pose3.Rotation(fromRad + (toRad - fromRad) * t, ax, ay, az, point));

    /// <summary>Pointwise composition outer(t) ∘ inner(t) (e.g. a rotating spindle carried by a moving slide).</summary>
    public static Motion3 Compose(Motion3 outer, Motion3 inner) => Zip(outer, inner, (o, i) => o.Compose(i));

    /// <summary>Segments of all motions one after another.</summary>
    public static Motion3 Sequence(params Motion3[] motions) => new(motions.SelectMany(m => m.Segments).ToArray());

    /// <summary>Relative motion of a tool in the frame of a moving workpiece: workpiece(t)⁻¹ ∘ tool(t).</summary>
    public static Motion3 Relative(Motion3 tool, Motion3 workpiece) => Zip(workpiece, tool, (w, t) => w.Inverse().Compose(t));

    private static Motion3 Zip(Motion3 a, Motion3 b, Func<Pose3, Pose3, Pose3> f)
    {
        int n = Math.Max(a.Segments.Count, b.Segments.Count);
        if (!(a.Segments.Count == n || a.Segments.Count == 1) || !(b.Segments.Count == n || b.Segments.Count == 1))
            throw new ArgumentException("Segment counts do not match.");
        var segs = new Func<double, Pose3>[n];
        for (int i = 0; i < n; i++)
        {
            var sa = a.Segments[a.Segments.Count == 1 ? 0 : i];
            var sb = b.Segments[b.Segments.Count == 1 ? 0 : i];
            segs[i] = t => f(sa(t), sb(t));
        }
        return new Motion3(segs);
    }
}
