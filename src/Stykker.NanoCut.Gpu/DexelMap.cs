namespace Stykker.NanoCut.Gpu;

/// <summary>
/// A dexel preview of milling: every grid column keeps up to <see cref="MaxIntervals"/> material intervals [z0, z1]
/// instead of the single height of a <see cref="ZMap"/>. A swept ball meets a column in one interval (it is convex), and
/// each step subtracts that interval, so a roof of material above the tool stays, and a ball buried in the stock
/// removes exactly what it sweeps -- the two things a height field cannot represent.
/// </summary>
/// <remarks>
/// Heights are float32, relative to the grid origin, like the Z-map. Intervals of a column are sorted, disjoint and
/// stored as (z0, z1) pairs; <see cref="Counts"/> says how many are in use. A subtraction that would split an interval
/// when the column is already full cuts through to that interval's top instead (it loses the roof piece, as a Z-map
/// would) and is counted in <see cref="Overflows"/>.
/// </remarks>
public sealed class DexelMap
{
    private readonly IDexelBackend _backend;

    /// <summary>Creates a map of the full stock box: one interval [minZ, maxZ] per column.</summary>
    /// <param name="minX">Lowest x of the workpiece in mm.</param>
    /// <param name="minY">Lowest y of the workpiece in mm.</param>
    /// <param name="minZ">Bottom of the workpiece in mm.</param>
    /// <param name="maxX">Highest x of the workpiece in mm.</param>
    /// <param name="maxY">Highest y of the workpiece in mm.</param>
    /// <param name="maxZ">Top of the workpiece in mm.</param>
    /// <param name="cellsX">Number of cells in x, at least 1.</param>
    /// <param name="cellsY">Number of cells in y, at least 1.</param>
    /// <param name="maxIntervals">Intervals per column, 1 to 16.</param>
    /// <param name="backend">What runs the steps; the CPU backend by default.</param>
    public DexelMap(double minX, double minY, double minZ, double maxX, double maxY, double maxZ,
        int cellsX, int cellsY, int maxIntervals = 4, IDexelBackend? backend = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cellsX, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(cellsY, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIntervals, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxIntervals, 16);
        if (!(maxX > minX)) throw new ArgumentOutOfRangeException(nameof(maxX), "maxX must be greater than minX.");
        if (!(maxY > minY)) throw new ArgumentOutOfRangeException(nameof(maxY), "maxY must be greater than minY.");
        if (!(maxZ > minZ)) throw new ArgumentOutOfRangeException(nameof(maxZ), "maxZ must be greater than minZ.");
        long columns = (long)cellsX * cellsY;
        if (columns * maxIntervals * 2 > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(cellsX), "the map is too large for one array of intervals.");

        OriginMm = (minX, minY, minZ);
        CellsX = cellsX;
        CellsY = cellsY;
        MaxIntervals = maxIntervals;
        CellSizeXMm = (maxX - minX) / cellsX;
        CellSizeYMm = (maxY - minY) / cellsY;
        BottomMm = minZ;
        TopMm = maxZ;
        TopRelative = (float)(maxZ - minZ);
        Intervals = new float[columns * maxIntervals * 2];
        Counts = new byte[columns];
        for (long c = 0; c < columns; c++)
        {
            Intervals[c * maxIntervals * 2 + 1] = TopRelative;
            Counts[c] = 1;
        }
        _backend = backend ?? CpuBackend.Instance;
    }

    /// <summary>Creates a map over the box of an expanded bench scene.</summary>
    /// <param name="scene">The scene, whose box becomes the workpiece.</param>
    /// <param name="cellsX">Number of cells in x.</param>
    /// <param name="cellsY">Number of cells in y.</param>
    /// <param name="maxIntervals">Intervals per column.</param>
    /// <param name="backend">What runs the steps; the CPU backend by default.</param>
    public static DexelMap FromScene(ExpandedScene scene, int cellsX, int cellsY, int maxIntervals = 4,
        IDexelBackend? backend = null) =>
        new(scene.BoxMinMm.X, scene.BoxMinMm.Y, scene.BoxMinMm.Z,
            scene.BoxMaxMm.X, scene.BoxMaxMm.Y, scene.BoxMaxMm.Z, cellsX, cellsY, maxIntervals, backend);

    /// <summary>The corner the relative coordinates and heights are measured from, in absolute mm.</summary>
    public (double X, double Y, double Z) OriginMm { get; }

    /// <summary>Number of cells in x.</summary>
    public int CellsX { get; }

    /// <summary>Number of cells in y.</summary>
    public int CellsY { get; }

    /// <summary>Intervals a column can hold.</summary>
    public int MaxIntervals { get; }

    /// <summary>Cell size in x, mm.</summary>
    public double CellSizeXMm { get; }

    /// <summary>Cell size in y, mm.</summary>
    public double CellSizeYMm { get; }

    /// <summary>Bottom of the stock, absolute mm.</summary>
    public double BottomMm { get; }

    /// <summary>Top of the stock, absolute mm.</summary>
    public double TopMm { get; }

    /// <summary>
    /// Column c's intervals are the pairs at c·<see cref="MaxIntervals"/>·2, heights in mm relative to the origin's z.
    /// Only the first <see cref="Counts"/>[c] pairs are in use. Current only while <see cref="IsCurrent"/> is true.
    /// </summary>
    public float[] Intervals { get; }

    /// <summary>Intervals in use per column (row-major, x fastest).</summary>
    public byte[] Counts { get; }

    /// <summary>Subtractions that found their column full and cut through to the interval's top instead of splitting it.</summary>
    public long Overflows { get; internal set; }

    /// <summary>What runs the steps.</summary>
    public IDexelBackend Backend => _backend;

    /// <summary>Timing of the last <see cref="ApplySteps"/> or <see cref="ApplyConvexSteps"/> call.</summary>
    public ZMapTiming LastTiming { get; private set; }

    /// <summary>Timing of all <see cref="ApplySteps"/> and <see cref="ApplyConvexSteps"/> calls so far.</summary>
    public ZMapTiming TotalTiming { get; private set; }

    /// <summary>Steps applied so far.</summary>
    public int AppliedSteps { get; private set; }

    /// <summary>Whether <see cref="Intervals"/>, <see cref="Counts"/> and <see cref="Overflows"/> show the last step.</summary>
    public bool IsCurrent { get; private set; } = true;

    internal float TopRelative { get; }

    /// <summary>Subtracts the swept ball of every step, in order.</summary>
    /// <param name="steps">The tool steps in absolute mm.</param>
    /// <param name="readBack">Whether the intervals are copied back to the host.</param>
    public void ApplySteps(ReadOnlySpan<BallStep> steps, ZMapReadBack readBack = ZMapReadBack.Always)
    {
        if (steps.IsEmpty) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = _backend.ApplyDexels(this, steps, readBack);
        sw.Stop();
        LastTiming = t with { WallMs = t.WallMs > 0 ? t.WallMs : sw.Elapsed.TotalMilliseconds };
        TotalTiming += LastTiming;
        AppliedSteps += steps.Length;
        IsCurrent = readBack == ZMapReadBack.Always || _backend.KeepsDexelsOnHost;
    }

    /// <summary>Subtracts the convex tool swept by every step, in order.</summary>
    /// <param name="tool">The tool, as the intersection of its half-spaces.</param>
    /// <param name="steps">The tool steps in absolute mm, each with its orientation.</param>
    /// <param name="readBack">Whether the intervals are copied back to the host.</param>
    public void ApplyConvexSteps(ConvexTool tool, ReadOnlySpan<ConvexStep> steps, ZMapReadBack readBack = ZMapReadBack.Always)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (steps.IsEmpty) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = _backend.ApplyConvexDexels(this, tool, steps, readBack);
        sw.Stop();
        LastTiming = t with { WallMs = t.WallMs > 0 ? t.WallMs : sw.Elapsed.TotalMilliseconds };
        TotalTiming += LastTiming;
        AppliedSteps += steps.Length;
        IsCurrent = readBack == ZMapReadBack.Always || _backend.KeepsDexelsOnHost;
    }

    /// <summary>Copies the state back to the host; returns the copy time in ms.</summary>
    public double ReadBack()
    {
        double ms = _backend.ReadDexels(this);
        IsCurrent = true;
        return ms;
    }

    /// <summary>The removed volume in mm³ as the backend computes it (a reduction on the device for CUDA).</summary>
    public double BackendRemovedVolumeMm3 => _backend.RemovedVolumeMm3(this);

    /// <summary>The removed volume in mm³ from the host copy: the stock height minus the material left, per column.</summary>
    /// <exception cref="InvalidOperationException">The host copy is not current; call <see cref="ReadBack"/>.</exception>
    public double RemovedVolumeMm3
    {
        get
        {
            RequireCurrent();
            double top = TopRelative, sum = 0;
            int k2 = MaxIntervals * 2;
            for (long c = 0; c < Counts.Length; c++)
            {
                double left = 0;
                long o = c * k2;
                for (int i = 0; i < Counts[c]; i++) left += Intervals[o + 2 * i + 1] - Intervals[o + 2 * i];
                sum += top - left;
            }
            return sum * CellSizeXMm * CellSizeYMm;
        }
    }

    /// <summary>The volume of the untouched stock box in mm³.</summary>
    public double BoxVolumeMm3 => (TopMm - BottomMm) * CellsX * CellSizeXMm * CellsY * CellSizeYMm;

    /// <summary>The material left in mm³: <see cref="BoxVolumeMm3"/> minus <see cref="RemovedVolumeMm3"/>.</summary>
    public double RemainingVolumeMm3 => BoxVolumeMm3 - RemovedVolumeMm3;

    /// <summary>
    /// The top of the material per column, relative to the origin (0 where nothing is left): what a view from above
    /// sees. Roofs and the cavities under them are in <see cref="Intervals"/>, not here.
    /// </summary>
    public float[] TopHeights()
    {
        RequireCurrent();
        var h = new float[Counts.Length];
        int k2 = MaxIntervals * 2;
        for (long c = 0; c < Counts.Length; c++)
            h[c] = Counts[c] == 0 ? 0f : Intervals[c * k2 + 2 * (Counts[c] - 1) + 1];
        return h;
    }

    /// <summary>The top surface as a mesh for a viewer, built like <see cref="ZMap.ToMesh"/>.</summary>
    public ZMapMesh ToMesh() => ZMap.MeshOf(TopHeights(), CellsX, CellsY, CellSizeXMm, CellSizeYMm, OriginMm);

    private void RequireCurrent()
    {
        if (!IsCurrent)
            throw new InvalidOperationException(
                "the intervals are on the backend; call ReadBack() or use BackendRemovedVolumeMm3");
    }

    /// <summary>
    /// Subtracts [lo, hi] from one column's sorted, disjoint intervals (pairs in <paramref name="iv"/>, count
    /// <paramref name="n"/>, room for <paramref name="capacity"/>). Returns true when the column was full and an interval
    /// that should have split was cut through to its top instead. zmap.cu's dexel kernel does the same.
    /// </summary>
    internal static bool Subtract(Span<float> iv, ref int n, int capacity, float lo, float hi)
    {
        if (!(hi > lo)) return false;
        bool overflow = false;
        int w = 0;
        Span<float> outBuf = stackalloc float[34];
        for (int i = 0; i < n; i++)
        {
            float a = iv[2 * i], b = iv[2 * i + 1];
            if (hi <= a || lo >= b)
            {
                outBuf[2 * w] = a; outBuf[2 * w + 1] = b; w++;
                continue;
            }
            bool left = lo > a, right = hi < b;
            if (left && right && n + 1 > capacity)
            {
                // No room for a split: keep the part below the cut, lose the roof above it.
                overflow = true;
                right = false;
            }
            if (left) { outBuf[2 * w] = a; outBuf[2 * w + 1] = lo; w++; }
            if (right) { outBuf[2 * w] = hi; outBuf[2 * w + 1] = b; w++; }
        }
        outBuf[..(2 * w)].CopyTo(iv);
        n = w;
        return overflow;
    }
}
