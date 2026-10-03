using System.Diagnostics;

namespace Stykker.NanoCut.Gpu;

/// <summary>
/// The reference backend: the same <see cref="ToolProfile"/> evaluation as the CUDA kernel, as a parallel loop over
/// the rows of the height field. It needs nothing but the runtime, so it is what CI and every machine without a GPU
/// uses, and what the GPU results are checked against.
/// </summary>
public sealed class CpuBackend : IZMapBackend, IZMapQueryBackend
{
    /// <summary>A shared backend on all logical processors.</summary>
    public static CpuBackend Instance { get; } = new();

    /// <summary>Creates a backend on the given number of threads.</summary>
    /// <param name="parallelism">Threads to use; all logical processors by default.</param>
    public CpuBackend(int? parallelism = null)
    {
        Parallelism = parallelism is { } p && p > 0 ? p : Environment.ProcessorCount;
    }

    /// <summary>Threads this backend runs on.</summary>
    public int Parallelism { get; }

    /// <inheritdoc />
    public string Name => $"cpu-{Parallelism}";

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public string? UnavailableReason => null;

    /// <inheritdoc />
    public bool KeepsHeightsOnHost => true;

    /// <inheritdoc />
    public ZMapTiming Apply(ZMap map, ReadOnlySpan<BallStep> steps, ZMapReadBack readBack)
    {
        float[] packed = ToolProfile.Pack(steps, map.OriginMm);
        int stepCount = steps.Length;
        float[] heights = map.Heights;
        int nx = map.CellsX, ny = map.CellsY;
        float cx = (float)map.CellSizeXMm, cy = (float)map.CellSizeYMm;
        float bottom = map.BottomRelative;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism };

        var sw = Stopwatch.StartNew();
        Parallel.For(0, ny, options, j =>
        {
            float y = (j + 0.5f) * cy;
            int row = j * nx;
            for (int i = 0; i < nx; i++)
            {
                float x = (i + 0.5f) * cx;
                float cur = heights[row + i];
                for (int s = 0; s < stepCount; s++)
                {
                    float b = ToolProfile.Bottom(x, y, packed, s);
                    if (b < cur) cur = b > bottom ? b : bottom;
                }
                heights[row + i] = cur;
            }
        });
        sw.Stop();

        double ms = sw.Elapsed.TotalMilliseconds;
        return new ZMapTiming(ms, 0, 0, 0, ms);
    }

    /// <inheritdoc />
    public double ReadHeights(ZMap map) => 0;

    /// <inheritdoc />
    public double RemovedVolumeMm3(ZMap map) => map.RemovedVolumeMm3;

    /// <summary>
    /// Reads the height at every point, in the same way as the CUDA kernel, from the field in
    /// <see cref="ZMap.Heights"/>. The span is staged into an array first, because a <see langword="ref"/> struct
    /// cannot be captured by the parallel loop; that copy per call is what a set of points saves.
    /// </summary>
    public ZMapTiming SampleHeights(ZMap map, ReadOnlySpan<SamplePoint> points, Span<float> outHeights) =>
        SampleHeights(map, PointSet.OnHost(points.ToArray()), outHeights);

    /// <summary>
    /// The same query about points the backend already holds in an array, so there is no staging copy per call. The
    /// answer is the one the span overload gives, to the bit.
    /// </summary>
    public ZMapTiming SampleHeights(ZMap map, PointSet points, Span<float> outHeights)
    {
        RequireCurrent(map);
        int nx = map.CellsX, ny = map.CellsY;
        float cx = (float)map.CellSizeXMm, cy = (float)map.CellSizeYMm;
        float[] heights = map.Heights;
        float ox = (float)map.OriginMm.X, oy = (float)map.OriginMm.Y;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism };

        SamplePoint[] ask = points.Points;
        var answer = new float[ask.Length];
        var sw = Stopwatch.StartNew();
        Parallel.For(0, ask.Length, options, i =>
        {
            var p = ask[i];
            answer[i] = Sample(heights, nx, ny, cx, cy, (float)p.X - ox, (float)p.Y - oy);
        });
        sw.Stop();

        answer.CopyTo(outHeights);
        double ms = sw.Elapsed.TotalMilliseconds;
        return new ZMapTiming(ms, 0, 0, 0, ms);
    }

    /// <summary>
    /// Copies the points into an array the set owns, and that is the whole preparation: the span overload has to copy
    /// them on every call because a span cannot be captured by the parallel loop, this one does not.
    /// </summary>
    public PointSet UploadPoints(ReadOnlySpan<SamplePoint> points) => PointSet.OnHost(points.ToArray());

    /// <summary>
    /// Reads how deep the ball cuts at every pose, in the same way as the CUDA kernel, from the field in
    /// <see cref="ZMap.Heights"/>. The two spans are staged into arrays first, because a
    /// <see langword="ref"/> struct cannot be captured by the parallel loop.
    /// </summary>
    public ZMapTiming ProbeMaterial(ZMap map, ReadOnlySpan<ToolPose> poses, Span<float> outPenetrationMm)
    {
        RequireCurrent(map);
        int nx = map.CellsX, ny = map.CellsY;
        float cx = (float)map.CellSizeXMm, cy = (float)map.CellSizeYMm;
        float[] heights = map.Heights;
        float ox = (float)map.OriginMm.X, oy = (float)map.OriginMm.Y, oz = (float)map.OriginMm.Z;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism };

        var ask = poses.ToArray();
        var answer = new float[ask.Length];
        var sw = Stopwatch.StartNew();
        Parallel.For(0, ask.Length, options, i =>
        {
            var p = ask[i];
            float h = Sample(heights, nx, ny, cx, cy, (float)p.X - ox, (float)p.Y - oy);
            answer[i] = MathF.Max(0f, h - ((float)p.Z - oz - (float)p.RadiusMm));
        });
        sw.Stop();

        answer.CopyTo(outPenetrationMm);
        double ms = sw.Elapsed.TotalMilliseconds;
        return new ZMapTiming(ms, 0, 0, 0, ms);
    }

    /// <summary>
    /// Bilinear height between the four surrounding cell centres, clamped to the field. This mirrors
    /// <c>sample_height</c> in <c>zmap.cu</c> operation for operation, so the CUDA kernel and this reference differ
    /// only by the fused multiply-add that nvcc contracts.
    /// </summary>
    internal static float Sample(float[] heights, int nx, int ny, float cellX, float cellY, float x, float y)
    {
        float gx = Math.Clamp(x / cellX - 0.5f, 0f, nx - 1);
        float gy = Math.Clamp(y / cellY - 0.5f, 0f, ny - 1);
        int i0 = (int)gx, j0 = (int)gy;
        int i1 = i0 + 1 < nx ? i0 + 1 : nx - 1;
        int j1 = j0 + 1 < ny ? j0 + 1 : ny - 1;
        float fx = gx - i0, fy = gy - j0;
        int r0 = j0 * nx, r1 = j1 * nx;
        float a = heights[r0 + i0] + (heights[r0 + i1] - heights[r0 + i0]) * fx;
        float b = heights[r1 + i0] + (heights[r1 + i1] - heights[r1 + i0]) * fx;
        return a + (b - a) * fy;
    }

    /// <summary>Throws when the field this backend answers from is not on the host.</summary>
    private static void RequireCurrent(ZMap map)
    {
        if (!map.IsHeightsCurrent)
        {
            throw new InvalidOperationException(
                "the height field is on the backend, not in Heights; call ReadHeights() first");
        }
    }
}
