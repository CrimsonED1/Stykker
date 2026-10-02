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

    /// <summary>Time of the last <see cref="ApplySteps(ReadOnlySpan{BallStep})"/> call.</summary>
    public ZMapTiming LastTiming { get; private set; }

    /// <summary>Time of every <see cref="ApplySteps(ReadOnlySpan{BallStep})"/> call on this map added together.</summary>
    public ZMapTiming TotalTiming { get; private set; }

    /// <summary>Number of steps applied so far.</summary>
    public int AppliedSteps { get; private set; }

    /// <summary>Lowers the height field by every step, in order.</summary>
    /// <param name="steps">The tool steps in absolute mm.</param>
    public void ApplySteps(ReadOnlySpan<BallStep> steps)
    {
        if (steps.IsEmpty) return;
        var sw = Stopwatch.StartNew();
        var t = _backend.Apply(this, steps);
        sw.Stop();
        LastTiming = t with { WallMs = t.WallMs > 0 ? t.WallMs : sw.Elapsed.TotalMilliseconds };
        TotalTiming += LastTiming;
        AppliedSteps += steps.Length;
    }

    /// <summary>Lowers the height field by a range of steps, in order.</summary>
    /// <param name="steps">The tool steps in absolute mm.</param>
    /// <param name="index">First step to apply.</param>
    /// <param name="count">Number of steps to apply.</param>
    public void ApplySteps(IReadOnlyList<BallStep> steps, int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index + count, steps.Count);
        if (count == 0) return;
        var slice = new BallStep[count];
        for (int i = 0; i < count; i++) slice[i] = steps[index + i];
        ApplySteps(slice);
    }

    /// <summary>
    /// The material the steps took out of the stock, in mm³: the sum over all cells of (stock top − height) times
    /// the cell area. Computed in double on the host, so it does not depend on the backend.
    /// </summary>
    public double RemovedVolumeMm3
    {
        get
        {
            double top = TopMm - OriginMm.Z, sum = 0;
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
    /// The height field as a triangle mesh for a viewer: one vertex per cell centre, two triangles per interior
    /// quad, smooth normals from the height differences. The mesh is open at the bottom, it is a preview surface.
    /// </summary>
    public ZMapMesh ToMesh()
    {
        int nx = CellsX, ny = CellsY;
        float cx = (float)CellSizeXMm, cy = (float)CellSizeYMm;
        var pos = new float[nx * ny * 3];
        var nrm = new float[nx * ny * 3];
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int v = j * nx + i;
                pos[3 * v] = (i + 0.5f) * cx;
                pos[3 * v + 1] = (j + 0.5f) * cy;
                pos[3 * v + 2] = Heights[v];

                // Central differences, one-sided at the border.
                float hl = Heights[j * nx + (i > 0 ? i - 1 : i)];
                float hr = Heights[j * nx + (i + 1 < nx ? i + 1 : i)];
                float hd = Heights[(j > 0 ? j - 1 : j) * nx + i];
                float hu = Heights[(j + 1 < ny ? j + 1 : j) * nx + i];
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
        return new ZMapMesh(pos, nrm, idx, OriginMm);
    }
}
