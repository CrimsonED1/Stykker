using System.Diagnostics;

namespace Stykker.NanoCut.Gpu;

/// <summary>
/// The reference backend: the same <see cref="ToolProfile"/> evaluation as the CUDA kernel, as a parallel loop over
/// the rows of the height field. It needs nothing but the runtime, so it is what CI and every machine without a GPU
/// uses, and what the GPU results are checked against.
/// </summary>
public sealed class CpuBackend : IZMapBackend
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
    public ZMapTiming Apply(ZMap map, ReadOnlySpan<BallStep> steps)
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
}
