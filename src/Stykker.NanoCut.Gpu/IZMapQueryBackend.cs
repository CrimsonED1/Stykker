namespace Stykker.NanoCut.Gpu;

/// <summary>
/// A backend that can also answer questions about a height field without copying the whole field to the host.
/// Both shipped backends implement it, which is what makes the CUDA results checkable against the CPU reference.
/// </summary>
/// <remarks>
/// The two queries are deliberately cheap in return traffic. <see cref="SampleHeights"/> can take millions of points
/// — that is what a viewer or a stock check wants — while <see cref="ProbeMaterial"/> takes thousands of tool poses
/// and returns one float each, so a caller can ask "does this move cut anything?" for a whole program without ever
/// touching the height field.
/// <para>
/// Calls on different maps run independently; two calls on the <em>same</em> map may be serialised by the backend,
/// because it packs the query into a buffer it keeps for that map and reuses on the next call.
/// </para>
/// </remarks>
public interface IZMapQueryBackend
{
    /// <summary>
    /// Height of the field at each point, bilinear between cell centres and clamped to the field, in the relative mm
    /// of <see cref="ZMap.OriginMm"/>. Writes one float per point.
    /// </summary>
    /// <param name="map">The height field to read.</param>
    /// <param name="points">The points, in absolute mm.</param>
    /// <param name="outHeights">Receives one relative height per point; at least as long as <paramref name="points"/>.</param>
    ZMapTiming SampleHeights(ZMap map, ReadOnlySpan<SamplePoint> points, Span<float> outHeights);

    /// <summary>
    /// How far the ball reaches into the material at each pose: the height of the field minus the bottom of the
    /// ball, zero where the ball hangs in air. Writes one float per pose.
    /// </summary>
    /// <param name="map">The height field to read.</param>
    /// <param name="poses">The tool poses, in absolute mm.</param>
    /// <param name="outPenetrationMm">Receives one depth in mm per pose, never negative; at least as long as <paramref name="poses"/>.</param>
    ZMapTiming ProbeMaterial(ZMap map, ReadOnlySpan<ToolPose> poses, Span<float> outPenetrationMm);
}