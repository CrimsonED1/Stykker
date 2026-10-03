using System.Diagnostics;

namespace Stykker.NanoCut.Gpu;

/// <summary>
/// A height field over the top of a workpiece: one z per grid cell, lowered by every tool step that reaches the
/// cell. This is a preview, not the exact result — it cannot represent overhangs, and its accuracy is set by the
/// cell size. Use the exact solids in <c>Stykker.NanoCut.Geometry3D</c> when the result has to be exact.
/// </summary>
/// <remarks>
/// Heights are stored as float32 in millimetres relative to <see cref="OriginMm"/>.Z, which keeps the float spacing
/// at a few nanometres for a workpiece of the usual size. A cell holds the height at its centre.
/// </remarks>
public sealed class ZMap
{
    private readonly IZMapBackend _backend;

    /// <summary>Creates a map of the stock top over [minX, maxX] × [minY, maxY] with the given cell count.</summary>
    /// <param name="minX">Lowest x of the workpiece in mm.</param>
    /// <param name="minY">Lowest y of the workpiece in mm.</param>
    /// <param name="minZ">Bottom of the workpiece in mm; nothing is ever cut below it.</param>
    /// <param name="maxX">Highest x of the workpiece in mm.</param>
    /// <param name="maxY">Highest y of the workpiece in mm.</param>
    /// <param name="maxZ">Top of the workpiece in mm, the height the map starts at.</param>
    /// <param name="cellsX">Number of cells in x, at least 1.</param>
    /// <param name="cellsY">Number of cells in y, at least 1.</param>
    /// <param name="backend">What runs the steps; the CPU backend by default.</param>
    public ZMap(double minX, double minY, double minZ, double maxX, double maxY, double maxZ,
        int cellsX, int cellsY, IZMapBackend? backend = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cellsX, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(cellsY, 1);
        if (!(maxX > minX)) throw new ArgumentOutOfRangeException(nameof(maxX), "maxX must be greater than minX.");
        if (!(maxY > minY)) throw new ArgumentOutOfRangeException(nameof(maxY), "maxY must be greater than minY.");
        if (!(maxZ > minZ)) throw new ArgumentOutOfRangeException(nameof(maxZ), "maxZ must be greater than minZ.");

        OriginMm = (minX, minY, minZ);
        CellsX = cellsX;
        CellsY = cellsY;
        CellSizeXMm = (maxX - minX) / cellsX;
        CellSizeYMm = (maxY - minY) / cellsY;
        BottomMm = minZ;
        TopMm = maxZ;
        TopRelative = (float)(maxZ - minZ);
        BottomRelative = 0f;
        Heights = new float[cellsX * cellsY];
        Array.Fill(Heights, TopRelative);
        _backend = backend ?? CpuBackend.Instance;
    }

    /// <summary>Creates a map over the box of an expanded bench scene.</summary>
    /// <param name="scene">The scene, whose box becomes the workpiece.</param>
    /// <param name="cellsX">Number of cells in x.</param>
    /// <param name="cellsY">Number of cells in y.</param>
    /// <param name="backend">What runs the steps; the CPU backend by default.</param>
    public static ZMap FromScene(ExpandedScene scene, int cellsX, int cellsY, IZMapBackend? backend = null) =>
        new(scene.BoxMinMm.X, scene.BoxMinMm.Y, scene.BoxMinMm.Z,
            scene.BoxMaxMm.X, scene.BoxMaxMm.Y, scene.BoxMaxMm.Z, cellsX, cellsY, backend);

    /// <summary>The corner the relative coordinates and heights are measured from, in absolute mm.</summary>
    public (double X, double Y, double Z) OriginMm { get; }

    /// <summary>Number of cells in x.</summary>
    public int CellsX { get; }

    /// <summary>Number of cells in y.</summary>
    public int CellsY { get; }

    /// <summary>Cell width in mm.</summary>
    public double CellSizeXMm { get; }

    /// <summary>Cell height in mm.</summary>
    public double CellSizeYMm { get; }

    /// <summary>Bottom of the workpiece in absolute mm; heights never go below it.</summary>
    public double BottomMm { get; }

    /// <summary>Top of the workpiece in absolute mm, where every cell starts.</summary>
    public double TopMm { get; }

    /// <summary>
    /// One height per cell in mm relative to <see cref="OriginMm"/>.Z, row-major with y as the outer index. The
    /// value is the z at the centre of the cell. Backends write into this array.
    /// </summary>
    public float[] Heights { get; }

    /// <summary>Top of the stock in relative mm, the value every cell starts at.</summary>
    internal float TopRelative { get; }

    /// <summary>Bottom of the stock in relative mm, the clamp for every cut.</summary>
    internal float BottomRelative { get; }

    /// <summary>Cell centre x in relative mm.</summary>
    internal float CellCentreX(int i) => (i + 0.5f) * (float)CellSizeXMm;

    /// <summary>Cell centre y in relative mm.</summary>
    internal float CellCentreY(int j) => (j + 0.5f) * (float)CellSizeYMm;

    /// <summary>The backend this map runs its steps on.</summary>
    public IZMapBackend Backend => _backend;

    /// <summary>Time of the last <see cref="ApplySteps(ReadOnlySpan{BallStep}, ZMapReadBack)"/> call.</summary>
    public ZMapTiming LastTiming { get; private set; }

    /// <summary>Time of every <see cref="ApplySteps(ReadOnlySpan{BallStep}, ZMapReadBack)"/> call on this map added together.</summary>
    public ZMapTiming TotalTiming { get; private set; }

    /// <summary>Number of steps applied so far.</summary>
    public int AppliedSteps { get; private set; }

    /// <summary>
    /// Whether <see cref="Heights"/> holds the field as of the last applied step. It starts true, because the array is
    /// filled with the stock top, and it goes false when a batch is applied with
    /// <see cref="ZMapReadBack.Never"/> and the backend keeps the field to itself. Everything on the host that reads
    /// <see cref="Heights"/> checks this, so a stale field is an exception and not a wrong answer.
    /// </summary>
    public bool IsHeightsCurrent { get; private set; } = true;

    /// <summary>Lowers the height field by every step, in order.</summary>
    /// <param name="steps">The tool steps in absolute mm.</param>
    /// <param name="readBack">
    /// Whether the field is copied back to <see cref="Heights"/>. A GPU backend saves the copy with
    /// <see cref="ZMapReadBack.Never"/>, at the price of every host read of the field throwing until
    /// <see cref="ReadHeights"/> is called.
    /// </param>
    public void ApplySteps(ReadOnlySpan<BallStep> steps, ZMapReadBack readBack = ZMapReadBack.Always)
    {
        if (steps.IsEmpty) return;
        var sw = Stopwatch.StartNew();
        var t = _backend.Apply(this, steps, readBack);
        sw.Stop();
        LastTiming = t with { WallMs = t.WallMs > 0 ? t.WallMs : sw.Elapsed.TotalMilliseconds };
        TotalTiming += LastTiming;
        AppliedSteps += steps.Length;
        IsHeightsCurrent = readBack == ZMapReadBack.Always || _backend.KeepsHeightsOnHost;
    }

    /// <summary>Lowers the height field by a range of steps, in order.</summary>
    /// <param name="steps">The tool steps in absolute mm.</param>
    /// <param name="index">First step to apply.</param>
    /// <param name="count">Number of steps to apply.</param>
    /// <param name="readBack">Whether the field is copied back to <see cref="Heights"/>.</param>
    public void ApplySteps(IReadOnlyList<BallStep> steps, int index, int count,
        ZMapReadBack readBack = ZMapReadBack.Always)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index + count, steps.Count);
        if (count == 0) return;
        var slice = new BallStep[count];
        for (int i = 0; i < count; i++) slice[i] = steps[index + i];
        ApplySteps(slice, readBack);
    }

    /// <summary>
    /// Copies the height field back to <see cref="Heights"/> from wherever the backend keeps it and reports the copy
    /// time in milliseconds. On a batch applied with <see cref="ZMapReadBack.Never"/> this is the price of the copy
    /// that was avoided; on a CPU backend it does nothing, because the array is the state.
    /// </summary>
    public double ReadHeights()
    {
        double downloadMs = _backend.ReadHeights(this);
        IsHeightsCurrent = true;
        return downloadMs;
    }

    /// <summary>
    /// The material the steps took out of the stock in mm³, as the backend computes it. On the CUDA backend this is
    /// a reduction on the device that returns a single double, so it works while the field stays on the device and
    /// costs a few microseconds instead of a read-back of the whole field.
    /// </summary>
    public double BackendRemovedVolumeMm3 => _backend.RemovedVolumeMm3(this);

    /// <summary>
    /// The material the steps took out of the stock, in mm³: the sum over all cells of (stock top − height) times
    /// the cell area. Computed in double on the host from <see cref="Heights"/>, so it needs that field to be
    /// current — call <see cref="ReadHeights"/> first when the last batch was applied with
    /// <see cref="ZMapReadBack.Never"/>. Compare with <see cref="BackendRemovedVolumeMm3"/>, which does not.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="Heights"/> is not the field as of the last step.</exception>
    public double RemovedVolumeMm3
    {
        get
        {
            RequireCurrentHeights();
            // The float top the field starts at, not TopMm − OriginMm.Z in double: an untouched cell then removes
            // exactly nothing, as on the device, instead of the rounding of the top times the cell area.
            double top = TopRelative, sum = 0;
            foreach (float h in Heights) sum += top - h;
            return sum * CellSizeXMm * CellSizeYMm;
        }
    }

    /// <summary>
    /// The remaining stock volume in mm³: the box volume minus <see cref="RemovedVolumeMm3"/>. Compare this with
    /// the volume of the exact kernel to judge the preview.
    /// </summary>
    public double RemainingVolumeMm3 => BoxVolumeMm3 - RemovedVolumeMm3;

    /// <summary>The volume of the untouched stock box in mm³.</summary>
    public double BoxVolumeMm3 =>
        (TopMm - BottomMm) * CellsX * CellSizeXMm * CellsY * CellSizeYMm;

    /// <summary>
    /// Height of the field at each point in absolute mm, bilinear between cell centres and clamped to the field, and
    /// where the time went. A point outside the workpiece sees the border height, it is not extrapolated.
    /// </summary>
    /// <param name="points">The points to ask about, in absolute mm.</param>
    /// <param name="outHeights">Receives one absolute height in mm per point; at least as long as <paramref name="points"/>.</param>
    /// <returns>Kernel, upload, download and wall time of the query.</returns>
    /// <exception cref="NotSupportedException">The backend cannot answer queries.</exception>
    /// <exception cref="InvalidOperationException">
    /// The backend answers from <see cref="Heights"/>, which is not current.
    /// </exception>
    public ZMapTiming SampleHeights(ReadOnlySpan<SamplePoint> points, Span<float> outHeights)
    {
        if (points.IsEmpty) return ZMapTiming.Zero;
        if (outHeights.Length < points.Length)
        {
            throw new ArgumentException($"outHeights has {outHeights.Length} entries for {points.Length} points.",
                nameof(outHeights));
        }

        // The backend answers in the relative mm of the grid, the caller asked in absolute mm. Shifting the result is
        // a host pass over the output, so it is deliberately not part of the reported kernel time.
        ZMapTiming timing = Query().SampleHeights(this, points, outHeights);
        float dz = (float)OriginMm.Z;
        for (int i = 0; i < points.Length; i++) outHeights[i] += dz;
        return timing;
    }

    /// <summary>
    /// Hands a set of points to the backend to keep, so repeated queries about them do not pay for them again. On the
    /// CUDA backend that is an upload to the device, once; the caller owns the result and should dispose it.
    /// </summary>
    /// <param name="points">The points to keep, in absolute mm.</param>
    /// <exception cref="NotSupportedException">The backend cannot answer queries.</exception>
    public PointSet UploadPoints(ReadOnlySpan<SamplePoint> points)
    {
        if (points.IsEmpty) throw new ArgumentException("no points to keep", nameof(points));
        return Query().UploadPoints(points);
    }

    /// <summary>
    /// Height of the field at every point of a set this map's backend already holds, bilinear between cell centres
    /// and clamped to the field, and where the time went. The answer is the one
    /// <see cref="SampleHeights(ReadOnlySpan{SamplePoint}, Span{float})"/> gives for the same points.
    /// </summary>
    /// <param name="points">A set from <see cref="UploadPoints"/>.</param>
    /// <param name="outHeights">Receives one absolute height in mm per point; at least <see cref="PointSet.Count"/> long.</param>
    /// <returns>Kernel, upload, download and wall time of the query.</returns>
    /// <exception cref="NotSupportedException">The backend cannot answer queries.</exception>
    public ZMapTiming SampleHeights(PointSet points, Span<float> outHeights)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (outHeights.Length < points.Count)
        {
            throw new ArgumentException($"outHeights has {outHeights.Length} entries for {points.Count} points.",
                nameof(outHeights));
        }

        ZMapTiming timing = Query().SampleHeights(this, points, outHeights);
        float dz = (float)OriginMm.Z;
        for (int i = 0; i < points.Count; i++) outHeights[i] += dz;
        return timing;
    }

    /// <summary>
    /// How far a ball reaches into the material at each pose: the height of the field minus the bottom of the ball,
    /// zero where the ball hangs in air, and where the time went. This is the query that keeps its input and output
    /// in the tens of kilobytes for a whole tool path, so a caller can check a program for air cuts without ever
    /// reading the height field back.
    /// </summary>
    /// <param name="poses">The tool poses, in absolute mm.</param>
    /// <param name="outPenetrationMm">Receives one depth in mm per pose, never negative; at least as long as <paramref name="poses"/>.</param>
    /// <returns>Kernel, upload, download and wall time of the query.</returns>
    /// <exception cref="NotSupportedException">The backend cannot answer queries.</exception>
    /// <exception cref="InvalidOperationException">
    /// The backend answers from <see cref="Heights"/>, which is not current.
    /// </exception>
    public ZMapTiming ProbeMaterial(ReadOnlySpan<ToolPose> poses, Span<float> outPenetrationMm)
    {
        if (poses.IsEmpty) return ZMapTiming.Zero;
        if (outPenetrationMm.Length < poses.Length)
        {
            throw new ArgumentException(
                $"outPenetrationMm has {outPenetrationMm.Length} entries for {poses.Length} poses.",
                nameof(outPenetrationMm));
        }
        return Query().ProbeMaterial(this, poses, outPenetrationMm);
    }

    /// <summary>The backend as a query backend, or an exception when it is not one.</summary>
    private IZMapQueryBackend Query() => _backend as IZMapQueryBackend
        ?? throw new NotSupportedException($"backend '{_backend.Name}' cannot answer height queries");

    /// <summary>Throws when <see cref="Heights"/> is not the field as of the last step.</summary>
    private void RequireCurrentHeights()
    {
        if (!IsHeightsCurrent)
        {
            throw new InvalidOperationException(
                "the height field is on the backend, not in Heights; call ReadHeights() or use BackendRemovedVolumeMm3");
        }
    }

    /// <summary>
    /// The height field as a triangle mesh for a viewer: one vertex per cell centre, two triangles per interior
    /// quad, smooth normals from the height differences. The mesh is open at the bottom, it is a preview surface.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="Heights"/> is not the field as of the last step.</exception>
    public ZMapMesh ToMesh()
    {
        RequireCurrentHeights();
        return MeshOf(Heights, CellsX, CellsY, CellSizeXMm, CellSizeYMm, OriginMm);
    }

    /// <summary>A height field (row-major, relative heights) as the mesh <see cref="ToMesh"/> describes.</summary>
    internal static ZMapMesh MeshOf(float[] heights, int nx, int ny, double cellX, double cellY,
        (double X, double Y, double Z) origin)
    {
        float cx = (float)cellX, cy = (float)cellY;
        var pos = new float[nx * ny * 3];
        var nrm = new float[nx * ny * 3];
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int v = j * nx + i;
                pos[3 * v] = (i + 0.5f) * cx;
                pos[3 * v + 1] = (j + 0.5f) * cy;
                pos[3 * v + 2] = heights[v];

                // Central differences, one-sided at the border.
                float hl = heights[j * nx + (i > 0 ? i - 1 : i)];
                float hr = heights[j * nx + (i + 1 < nx ? i + 1 : i)];
                float hd = heights[(j > 0 ? j - 1 : j) * nx + i];
                float hu = heights[(j + 1 < ny ? j + 1 : j) * nx + i];
                float dx = (i > 0 && i + 1 < nx) ? 2f * cx : cx;
                float dy = (j > 0 && j + 1 < ny) ? 2f * cy : cy;
                float gx = (hr - hl) / dx, gy = (hu - hd) / dy;
                float len = MathF.Sqrt(gx * gx + gy * gy + 1f);
                nrm[3 * v] = -gx / len;
                nrm[3 * v + 1] = -gy / len;
                nrm[3 * v + 2] = 1f / len;
            }
        }

        var idx = new uint[(nx - 1) * (ny - 1) * 6];
        int k = 0;
        for (int j = 0; j + 1 < ny; j++)
        {
            for (int i = 0; i + 1 < nx; i++)
            {
                uint a = (uint)(j * nx + i), b = a + 1, c = (uint)((j + 1) * nx + i), e = c + 1;
                idx[k++] = a; idx[k++] = b; idx[k++] = c;
                idx[k++] = b; idx[k++] = e; idx[k++] = c;
            }
        }
        return new ZMapMesh(pos, nrm, idx, origin);
    }
}
