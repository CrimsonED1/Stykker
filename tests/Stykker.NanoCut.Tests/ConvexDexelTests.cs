using Stykker.NanoCut;
using Stykker.NanoCut.Cutting;
using Stykker.NanoCut.Gpu;
using Stykker.NanoCut.Geometry3D;
using Xunit.Abstractions;

namespace Stykker.NanoCut.Tests;

/// <summary>
/// The convex-tool preview: a tool as the intersection of half-spaces, moved through a pose sequence, cut with the
/// same dexel arithmetic as the sphere. The cases are the ones the plan names — a box, an octahedron, a rotating tool
/// — and they are pinned against <see cref="Process3"/>, which reaches the answer by a completely different road
/// (exact Booleans and convex hulls rather than a linear program per column).
/// </summary>
public class ConvexDexelTests(ITestOutputHelper output)
{
    private static DexelMap Stock(int cells, double size = 20, int k = 4, IDexelBackend? backend = null) =>
        new(0, 0, 0, size, size, size, cells, cells, k, backend);

    /// <summary>The tool as the exact kernel sees it: one half-space per face, outward normal, in mm.</summary>
    private static ConvexTool FromSolid(Solid solid)
    {
        var planes = new HalfSpace[solid.FaceCount];
        for (int i = 0; i < planes.Length; i++)
        {
            var p = solid.Faces[i].Support;
            double nx = (double)p.Nx, ny = (double)p.Ny, nz = (double)p.Nz;
            double n = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            planes[i] = new HalfSpace(nx / n, ny / n, nz / n, -(double)p.D / n);
        }
        return ConvexTool.FromPlanes(planes);
    }

    private static ConvexTool BoxTool(double halfX, double halfY, double halfZ) => ConvexTool.Box(-halfX, -halfY, -halfZ, halfX, halfY, halfZ);

    private static Solid BoxSolid(double halfX, double halfY, double halfZ) => Solid.Box(Vec3.Mm(-halfX, -halfY, -halfZ), Vec3.Mm(halfX, halfY, halfZ));

    // ---- the tool itself ------------------------------------------------------------------------------

    [Fact]
    public void ABoxHasEightCornersAndSixPlanes()
    {
        ConvexTool box = BoxTool(1, 2, 3);
        Assert.Equal(6, box.PlaneCount);
        Assert.Equal(8, box.CornersMm.Count);
        Assert.Equal((-1, -2, -3, 1, 2, 3), box.BoxMm);
        Assert.Equal(Math.Sqrt(14), box.RadiusMm, 12);
    }

    [Fact]
    public void AnOctahedronHasSixCornersAndSitsInItsBox()
    {
        ConvexTool oct = ConvexTool.Octahedron(2);
        Assert.Equal(8, oct.PlaneCount);
        Assert.Equal(6, oct.CornersMm.Count);
        Assert.Equal((2, 2, 2), (oct.BoxMm.MaxX, oct.BoxMm.MaxY, oct.BoxMm.MaxZ));
        // Every corner sits on one axis: |x| + |y| + |z| = r there, and no other point is further out.
        Assert.All(oct.CornersMm, c => Assert.Equal(2, Math.Abs(c.X) + Math.Abs(c.Y) + Math.Abs(c.Z), 12));
    }

    [Fact]
    public void ABallBecomesAPolytopeInsideIt()
    {
        ConvexTool ball = ConvexTool.Ball(3, 12);
        Assert.Equal(12, ball.PlaneCount);
        // The inscribed polyhedron stays inside the sphere, and with 12 planes it fills most of it.
        Assert.All(ball.CornersMm, c => Assert.True(Math.Sqrt(c.X * c.X + c.Y * c.Y + c.Z * c.Z) <= 3 + 1e-9));
        output.WriteLine($"{ball.CornersMm.Count} corners, radius {ball.RadiusMm:F6} of 3");
        Assert.True(ball.RadiusMm > 2.99, "12 planes barely dent a sphere of 3 mm, the corner radius should stay near 3");
    }

    [Fact]
    public void ANormWithoutLengthIsNormalised()
    {
        // The normals are four, five and six times too long. Scaling a normal must not move its plane, so the distance
        // stays at 1 mm and the box comes out the same one a plain Box(-1, -1, -1, 1, 1, 1) gives.
        ConvexTool box = ConvexTool.FromPlanes(
            new HalfSpace(-4, 0, 0, 1), new HalfSpace(4, 0, 0, 1),
            new HalfSpace(0, -5, 0, 1), new HalfSpace(0, 5, 0, 1),
            new HalfSpace(0, 0, -6, 1), new HalfSpace(0, 0, 6, 1));
        Assert.Equal((-1, -1, -1, 1, 1, 1), box.BoxMm);
    }

    [Fact]
    public void APlanesetWithoutACornerIsRefused()
    {
        // Six box planes and a seventh that never touches the box. It is the redundant kind: the body stays the same
        // box, but the plane carries none of its corners, and a half-space that carries none is a sign the tool is
        // redundant or unbounded there.
        var e = Assert.Throws<ArgumentException>(() => ConvexTool.FromPlanes(
            new HalfSpace(-1, 0, 0, -1), new HalfSpace(1, 0, 0, 1),
            new HalfSpace(0, -1, 0, -1), new HalfSpace(0, 1, 0, 1),
            new HalfSpace(0, 0, -1, -1), new HalfSpace(0, 0, 1, 1),
            new HalfSpace(1, 1, 0, 3)));
        Assert.Contains("carries no corner", e.Message, StringComparison.Ordinal);

        // Five planes still bound the body from above, in x and y, but there is nothing below z = 0.9: the tool is
        // a slab without a bottom, and the kernel would read -inf as its lowest point and take the whole column.
        var open = Assert.Throws<ArgumentException>(() => ConvexTool.FromPlanes(
            new HalfSpace(-1, 0, 0, -1), new HalfSpace(1, 0, 0, 1),
            new HalfSpace(0, -1, 0, -1), new HalfSpace(0, 1, 0, 1),
            new HalfSpace(0, 0, 1, 0.9)));
        Assert.Contains("unbounded", open.Message, StringComparison.Ordinal);

        // One half-space bounds a slab, not a body: there is no triple to solve and no corner to find.
        Assert.Throws<ArgumentException>(() => ConvexTool.FromPlanes([new HalfSpace(0, 0, 1, 1)]));
        Assert.Throws<ArgumentException>(() => ConvexTool.FromPlanes(new HalfSpace(0, 0, 0, 1)));
    }

    [Fact]
    public void ABoxNeedsRoomOnEveryAxis()
    {
        Assert.Throws<ArgumentException>(() => ConvexTool.Box(0, 0, 0, 0, 1, 1));
        Assert.Throws<ArgumentException>(() => ConvexTool.Box(0, 0, 0, 1, 0, 1));
        Assert.Throws<ArgumentException>(() => ConvexTool.Box(0, 0, 0, 1, 1, -1));
    }

    // ---- the interval on a column ----------------------------------------------------------------------

    [Fact]
    public void AStandingBoxMeetsAColumnInItsOwnTopAndBottom()
    {
        ConvexTool box = BoxTool(1, 1.5, 0.5);
        float[] packed = ConvexProfile.Pack([ConvexStep.At((10, 10, 10))], box, (0, 0, 0));
        float[] planes = ConvexProfile.PackPlanes(box);

        bool At(float x, float y, out float low, out float high) =>
            ConvexProfile.Span(x, y, packed, planes, 0, out low, out high);

        Assert.True(At(10f, 10f, out float low, out float high));
        Assert.Equal(9.5f, low, 5);
        Assert.Equal(10.5f, high, 5);

        // Just outside the rim the column misses, and "misses" must come back as an empty interval, never a cut.
        Assert.False(At(11.02f, 10f, out _, out _));
        Assert.False(At(10f, 11.53f, out _, out _));

        // A column is cut as long as its centre is inside the footprint, and then all the way through: unlike a ball, a box
        // leaves no column half-cut. That is what the octahedron shows on the other side.
        Assert.True(At(10.999f, 10f, out low, out high));
        Assert.Equal(1f, high - low, 5);
        Assert.Equal(10f, (low + high) / 2, 5);
    }

    [Fact]
    public void ABoxSweptSidewaysGrowsOnlyAlongTheMove()
    {
        // A prism 7 long and 2 × 2 in section: every column inside the sweep must meet the full 2 mm of z.
        ConvexTool box = BoxTool(1, 1, 1);
        float[] packed = ConvexProfile.Pack([ConvexStep.Move((5, 10, 10), (12, 10, 10))], box, (0, 0, 0));
        float[] planes = ConvexProfile.PackPlanes(box);

        Assert.True(ConvexProfile.Span(5.01f, 10f, packed, planes, 0, out float lo, out float hi));
        Assert.Equal(9f, lo, 5);
        Assert.Equal(11f, hi, 5);

        Assert.True(ConvexProfile.Span(11.99f, 10f, packed, planes, 0, out lo, out hi));
        Assert.Equal(9f, lo, 5);
        Assert.Equal(11f, hi, 5);

        // The box is 2 across, so the sweep runs from 4 to 13 in x -- the tool's own width at both ends, not just the
        // 7 mm of travel.
        Assert.False(ConvexProfile.Span(3.98f, 10f, packed, planes, 0, out _, out _));
        Assert.False(ConvexProfile.Span(13.02f, 10f, packed, planes, 0, out _, out _));
    }

    [Fact]
    public void ADiagonalMoveLiftsTheIntervalWithIt()
    {
        // A column on the diagonal is only inside the box for the stretch of t where both its x and its y can absorb
        // the offset, and the height rides that stretch: the interval is shorter than the box and tilted.
        ConvexTool box = BoxTool(1, 1, 1);
        float[] packed = ConvexProfile.Pack([ConvexStep.Move((5, 5, 5), (12, 12, 12))], box, (0, 0, 0));
        float[] planes = ConvexProfile.PackPlanes(box);

        // The column sits 3.5 mm from the start in x and y and the box is 1 mm across, so it is inside for
        // 3.5 - 1 to 3.5 + 1 along the move: t from 2.5/7 to 4.5/7. The height rides that stretch, and it is
        // the stretch's ends that give the ends of the interval: 5 + 2.5 - 1 and 5 + 4.5 + 1.
        Assert.True(ConvexProfile.Span(8.5f, 8.5f, packed, planes, 0, out float lo, out float hi));
        Assert.Equal(6.5f, lo, 4);
        Assert.Equal(10.5f, hi, 4);
        output.WriteLine($"on the diagonal: [{lo:F4}, {hi:F4}]");

        // Six mm off the diagonal the box cannot follow at all: it is 2 across, not a diagonal slab.
        Assert.False(ConvexProfile.Span(8.5f, 11f, packed, planes, 0, out _, out _));
    }

    [Fact]
    public void ATurnedToolCutsMoreThanTheBoxAtEitherPose()
    {
        // A saw tooth turning about z. A ConvexStep carries one orientation and two positions, so the turn is not
        // a pose pair but a run of steps, and what the tool sweeps is the union of their boxes. That union is not any
        // single box: the far corner rides a circle of radius sqrt(2² + 0.4²) = 2.0396, while the box on its own
        // reaches 0.4 sideways and no single angle reaches the whole circle either.
        ConvexTool box = BoxTool(2, 0.4, 0.4);
        ConvexStep[] poses = Turn(box, Math.PI / 4, (10, 10, 10));
        Assert.True(poses.Length >= 5, $"{poses.Length} poses cannot resolve a 45° turn at 0.005 mm");

        var straight = Stock(400);
        straight.ApplyConvexSteps(box, [ConvexStep.At((10, 10, 10))]);

        var turned = Stock(400);
        turned.ApplyConvexSteps(box, poses);

        double one = straight.RemovedVolumeMm3;
        double swept = turned.RemovedVolumeMm3;
        double cap = Math.PI * box.RadiusMm * box.RadiusMm * 0.8;   // the disk the corners ride, times the height
        output.WriteLine($"{poses.Length} poses: box {one:F4} mm³, turned {swept:F4} mm³, disk cap {cap:F4} mm³");

        // The grid counts a column when its centre is cut, so the measured volume sits a little over the exact one.
        Assert.True(swept > one * 1.5, $"turning removed {swept:F4}, barely more than the box's {one:F4}");
        Assert.True(swept < cap * 1.1, $"{swept:F4} is past the {cap:F4} the corners' disk can hold");
    }

    /// <summary>The pose run of a tool turning about z by <paramref name="sweep"/> at a fixed point.</summary>
    private static ConvexStep[] Turn(ConvexTool tool, double sweep, (double X, double Y, double Z) at, double tolMm = 0.005)
    {
        int n = ConvexStep.StepsForRotation(sweep, tool.RadiusMm, tolMm);
        var steps = new List<ConvexStep>();
        for (int i = 0; i <= n; i++) steps.Add(new ConvexStep(Orientation3.AboutZ(sweep * i / n), at, at));
        return [.. steps];
    }

    [Fact]
    public void AStepThatStaysPutIsASphereLikeSpan()
    {
        // The degenerate move: w = 0 has to fall out of the same code, not a special case. What the octahedron shows
        // on a column is its diamond: through the centre it spans the full radius, and every millimetre sideways
        // costs a millimetre at each end.
        ConvexTool oct = ConvexTool.Octahedron(2);
        float[] packed = ConvexProfile.Pack([ConvexStep.At((10, 10, 10))], oct, (0, 0, 0));
        float[] planes = ConvexProfile.PackPlanes(oct);

        bool At(float x, float y, out float low, out float high) =>
            ConvexProfile.Span(x, y, packed, planes, 0, out low, out high);

        Assert.True(At(10f, 10f, out float lo, out float hi));
        Assert.Equal(8f, lo, 5);
        Assert.Equal(12f, hi, 5);

        Assert.True(At(10.5f, 10f, out lo, out hi));
        Assert.Equal(8.5f, lo, 5);
        Assert.Equal(11.5f, hi, 5);

        // On the plane y = 10 the octahedron is the diamond |x - 10| + |z - 10| <= 2, so a column's section is
        // 2 - |x - 10| tall. It is the vertex two millimetres out that ends it, not the perpendicular face distance
        // 2 / sqrt(3): a column is a vertical line, not the face's own normal, and it goes on past the face plane.
        float face = (float)(2 / Math.Sqrt(3));
        float half = 2f - face;
        Assert.True(At(10f + face, 10f, out lo, out hi));
        Assert.Equal(10f - half, lo, 3);
        Assert.Equal(10f + half, hi, 3);
        Assert.False(At(12f, 10f, out _, out _));
    }

    [Fact]
    public void EveryColumnAStepReachesIsInsideItsOwnBox()
    {
        // The early-out in Span and in convex_span settles a column against the step's own bounding box before it runs
        // the linear program. That is only allowed if the box contains every column the sweep reaches, so this walks
        // exactly the (step, column) pairs the binned launch hands to the kernel -- StepBins decides which tiles a step
        // belongs to -- and judges every hit against the box the packing wrote. The premise, not the code using it.
        ConvexTool grain = ConvexTool.Octahedron(0.65);          // a grinding grain, 1.3 mm across
        float[] planes = ConvexProfile.PackPlanes(grain);
        ConvexStep[] steps =
        [
            .. Turn(grain, 0.2, (10, 10, 10), tolMm: 0.0005),   // turning where it stands
            new ConvexStep(Orientation3.AboutZ(0.4), (9, 9, 10), (11, 11, 10)),   // and travelling while it turns
        ];
        float[] packed = ConvexProfile.Pack(steps, grain, (0, 0, 0));

        const int cells = 200;                                    // 20 mm of map at 0.1 mm cells
        const float cell = 0.1f;
        var bins = StepBins.Build(packed, ConvexProfile.StepFloats, true, cells, cells, cell, cell);
        var assigned = new bool[steps.Length * cells * cells];
        for (int t = 0; t < bins.TileCount; t++)
        {
            int tx = t % bins.TilesX, ty = t / bins.TilesX;
            for (int q = bins.TileStart[t]; q < bins.TileStart[t + 1]; q++)
            {
                int s = bins.TileSteps[q];
                for (int j = ty * StepBins.Tile; j < Math.Min((ty + 1) * StepBins.Tile, cells); j++)
                    for (int i = tx * StepBins.Tile; i < Math.Min((tx + 1) * StepBins.Tile, cells); i++)
                        assigned[s * cells * cells + j * cells + i] = true;
            }
        }

        int hits = 0, pairs = 0;
        for (int s = 0; s < steps.Length; s++)
        {
            int o = s * ConvexProfile.StepFloats;
            float bx = packed[o] - StepBins.MarginMm, by = packed[o + 1] - StepBins.MarginMm;
            float bx1 = bx + packed[o + 2] + 2 * StepBins.MarginMm, by1 = by + packed[o + 3] + 2 * StepBins.MarginMm;
            for (int j = 0; j < cells; j++)
                for (int i = 0; i < cells; i++)
                {
                    if (!assigned[s * cells * cells + j * cells + i]) continue;
                    pairs++;
                    float x = (i + 0.5f) * cell, y = (j + 0.5f) * cell;
                    if (!ConvexProfile.Span(x, y, packed, planes, s, out _, out _)) continue;
                    hits++;
                    Assert.InRange(x, bx, bx1);
                    Assert.InRange(y, by, by1);
                }
        }

        Assert.True(hits > 100, $"{hits} hits over {pairs} pairs is not the walk this needs to mean anything");
        // And the early-out is not dead code: a grain 13 columns across sits in a tile 16 wide, so the binning hands the
        // kernel most of the tile and the box throws it away again. The share is a measurement, so it is only here
        // to be non-zero.
        Assert.True(pairs > 4 * hits, $"{pairs} pairs for {hits} hits leaves nothing for the box to reject");
        output.WriteLine($"{steps.Length} steps, {pairs} (step, column) pairs binned, {hits} of them reach the tool, " +
                         $"{100.0 * (pairs - hits) / pairs:F1} % rejected by the box");
    }

    // ---- the volumes, against closed forms ---------------------------------------------------------------

    [Fact]
    public void ABuriedBoxRemovesItsOwnVolume()
    {
        var map = Stock(600, k: 6);
        map.ApplyConvexSteps(BoxTool(1, 1.5, 0.5), [ConvexStep.At((10, 10, 10))]);
        double exact = 2 * 3 * 1;
        output.WriteLine($"removed {map.RemovedVolumeMm3:F6} mm³, box {exact:F6} mm³, roof columns {map.Counts.Count(c => c == 2)}");
        // The columns that hold a roof are the ones the box reaches but does not fill, so the rest is untouched.
        Assert.All(map.TopHeights(), h => Assert.Equal(20f, h));
        Assert.Equal(0, map.Overflows);
        Assert.True(Math.Abs(map.RemovedVolumeMm3 - exact) < 0.004 * exact,
            $"preview {map.RemovedVolumeMm3:F4} against box {exact:F4}");
    }

    [Fact]
    public void ABoxSweptSidewaysRemovesAPrism()
    {
        // The tool is 2 across and travels 7, so the prism it sweeps is 9 long: the 7 of travel plus the tool's own
        // width at each end. Writing 7 here is the classic way to be wrong by 30 %.
        var map = Stock(600, k: 6);
        map.ApplyConvexSteps(BoxTool(1, 1, 1), [ConvexStep.Move((5, 10, 10), (12, 10, 10))]);
        double exact = 9 * 2 * 2;
        output.WriteLine($"removed {map.RemovedVolumeMm3:F6} mm³, prism {exact:F6} mm³");
        Assert.True(Math.Abs(map.RemovedVolumeMm3 - exact) < 0.004 * exact,
            $"preview {map.RemovedVolumeMm3:F4} against prism {exact:F4}");
    }

    [Fact]
    public void AnOctahedronRemovesFourThirdsOfItsRadiusCubed()
    {
        var map = Stock(700, k: 6);
        map.ApplyConvexSteps(ConvexTool.Octahedron(2), [ConvexStep.At((10, 10, 10))]);
        double exact = 4.0 / 3 * 8;
        output.WriteLine($"removed {map.RemovedVolumeMm3:F6} mm³, octahedron {exact:F6} mm³");
        Assert.True(Math.Abs(map.RemovedVolumeMm3 - exact) < 0.004 * exact,
            $"preview {map.RemovedVolumeMm3:F4} against octahedron {exact:F4}");
    }

    // ---- against the exact kernel ----------------------------------------------------------------------

    /// <summary>
    /// The volume <see cref="Process3"/> cuts out of the stock, which it works out with exact Booleans over convex
    /// hulls — a completely different road to the same body. The preview sees the sweep column by column and samples
    /// each column at its centre, so it cannot land on the exact number; what it can do is stay inside the volume the
    /// sampling itself can be off by, which is what <see cref="SamplingBound"/> measures.
    /// </summary>
    private double PreviewAgainstProcess3(Solid tool, Motion3 motion, ConvexTool convex, ConvexStep[] steps, int cells,
        Tolerance? tol = null)
    {
        const double size = 20;
        var stock = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(size, size, size));
        var shape = ToolShape.FromConvexParts(tool);
        Solid[] cut = Process3.Cut([stock], shape, motion, tol, out Process3.Stats stats);
        double exact = stock.VolumeMm3 - cut[0].VolumeMm3;

        var map = new DexelMap(0, 0, 0, size, size, size, cells, cells, maxIntervals: 8);
        map.ApplyConvexSteps(convex, steps);
        double got = map.RemovedVolumeMm3;
        double bound = SamplingBound(convex, steps, size / cells);

        output.WriteLine($"{cells,4} cells (cell {size / cells:F4} mm), {steps.Length} preview steps against {stats.Intervals} " +
                         $"exact: preview {got:F4} mm³, exact {exact:F4} mm³, " +
                         $"difference {got - exact:F4} mm³ ({100 * (got - exact) / exact:F3} %), " +
                         $"sampling bound {bound:F4} mm³");
        Assert.True(Math.Abs(got - exact) <= bound + 1e-6,
            $"preview {got:F4} against exact {exact:F4}: {got - exact:F4} mm³ is more than the {bound:F4} mm³ " +
            $"the column sampling can account for at {cells} cells");
        return got - exact;
    }

    /// <summary>
    /// The most volume the column model can be off by on a grid of <paramref name="cell"/>: the volume of the rim it
    /// samples, and nothing else.
    /// </summary>
    /// <remarks>
    /// A column is cut when its centre lies in the sweep, so the sampled silhouette is the true one grown or shrunk
    /// by half a cell, which over a perimeter P is P·h/2 of area (plus a cell squared for the corners). The rule is
    /// closed, so both signs are possible and a face that lands on a centre line takes a whole extra row there. The
    /// height is exact -- nothing is sampled in z -- so the volume is that area times the height the tool sweeps.
    /// <para>
    /// P is bounded by the perimeter of the swept body's bounding box, which the packing already carries, and the
    /// height comes from the corners under each step's own orientation, so a tool that turns about more than z is
    /// covered too. The bound is O(h): it falls with the cell, so a preview that is cut wrong rather than coarsely
    /// sampled cannot stay inside it at every grid, which is what the old convergence check was there to say.
    /// </para>
    /// </remarks>
    private static double SamplingBound(ConvexTool convex, ConvexStep[] steps, double cell)
    {
        float[] packed = ConvexProfile.Pack(steps, convex, (0, 0, 0));
        double width = 0, height = 0;
        for (int s = 0; s < steps.Length; s++)
        {
            width = Math.Max(width, packed[s * ConvexProfile.StepFloats + 2]);
            height = Math.Max(height, packed[s * ConvexProfile.StepFloats + 3]);
        }

        double lowZ = double.MaxValue, highZ = double.MinValue;
        foreach (var step in steps)
        {
            double rise = step.ToMm.Z - step.FromMm.Z;
            foreach (var (cx, cy, cz) in convex.CornersMm)
            {
                double rz = step.Orientation.Apply(cx, cy, cz).Z;
                lowZ = Math.Min(lowZ, Math.Min(rz, rz + rise));
                highZ = Math.Max(highZ, Math.Max(rz, rz + rise));
            }
        }
        return (2 * (width + height) * cell / 2 + cell * cell) * (highZ - lowZ);
    }

    /// <summary>Runs a case on three grids; on each of them the preview has to stay inside the sampling bound.</summary>
    /// <remarks>
    /// The bound falls with the cell, so a difference that sits at the tool's real volume -- a body cut wrong -- does
    /// not fit it at all three sizes. What it does not do is shrink on its own: a face that lands exactly on the
    /// centres of the finest grid takes a full row of columns there, and no grid refinement takes that away. The
    /// 1000-cell case of the box below is exactly that, and it sits on the bound to the last digit.
    /// </remarks>
    private void ThreeGrids(Solid tool, Motion3 motion, ConvexTool convex, ConvexStep[] steps)
    {
        foreach (int cells in new[] { 250, 500, 1000 })
            PreviewAgainstProcess3(tool, motion, convex, steps, cells);
    }

    [Fact]
    public void ABoxAgreesWithTheExactCut()
    {
        // The box's faces are off the column centres on purpose. The rule is closed -- a centre that sits on the
        // tool's edge counts as inside -- so a face on a centre line takes a whole extra row of columns there. At
        // 1000 cells the centres are on a 0.01 mm grid and these faces are at 4.13, 13.13, 9.07 and 11.07 mm, which
        // is where the 1.2 % of the 1000-cell row comes from: 451 x 101 columns instead of 450 x 100. That is the
        // sampling bound itself and no grid takes it away, which is why the bound is what is checked.
        var motion = Motion3.Linear(Vec3.Mm(5.13, 10.07, 10.21), Vec3.Mm(12.13, 10.07, 10.21));
        var steps = new[] { ConvexStep.Move((5.13, 10.07, 10.21), (12.13, 10.07, 10.21)) };
        ThreeGrids(BoxSolid(1, 1, 1), motion, BoxTool(1, 1, 1), steps);
    }

    [Fact]
    public void AnOctahedronAgreesWithTheExactCut()
    {
        // The octahedron in the kernel's own terms is the same eight planes, so both sides sweep the same body.
        var tool = ConvexTool.Octahedron(2.5);
        Solid solid = SolidFromConvex(tool);
        var motion = Motion3.Linear(Vec3.Mm(6, 8, 9), Vec3.Mm(13, 11, 12));
        var steps = new[] { ConvexStep.Move((6, 8, 9), (13, 11, 12)) };
        ThreeGrids(solid, motion, tool, steps);
    }

    [Fact]
    public void ATiltedToolThatTravelsAgreesWithTheExactCut()
    {
        // Where asks now whether the bound moves before it divides, and the pair loop only has a slope to ask about
        // when the tool travels. This case is here for the combination the other ones do not have: a tilt about x by
        // 0.3 rad and 4 mm of travel along y, which gives nLo = nHi = 2 and four pairs whose s is non-zero on both
        // sides. Travelling along x instead would leave every dot at zero - w is parallel to the tilt's own axis -
        // and the new branch would go unreached, so the direction of the travel is the point of the case.
        ConvexTool tool = BoxTool(1, 2, 0.5);
        var from = (X: 10.0, Y: 6.0, Z: 10.0);
        var to = (X: 10.0, Y: 10.0, Z: 10.0);
        var motion = Motion3.Between(
            Pose3.Rotation(0.3, 1, 0, 0, Vec3.Mm(from.X, from.Y, from.Z)) with
            { TxNm = (long)(from.X * 1e6), TyNm = (long)(from.Y * 1e6), TzNm = (long)(from.Z * 1e6) },
            Pose3.Rotation(0.3, 1, 0, 0, Vec3.Mm(from.X, from.Y, from.Z)) with
            { TxNm = (long)(to.X * 1e6), TyNm = (long)(to.Y * 1e6), TzNm = (long)(to.Z * 1e6) });
        var steps = new[]
        {
            new ConvexStep(Orientation3.AboutAxis(0.3, 1, 0, 0), (from.X, from.Y, from.Z), (to.X, to.Y, to.Z)),
        };
        ThreeGrids(BoxSolid(1, 2, 0.5), motion, tool, steps);
    }

    [Fact]
    public void ARotatingToolAgreesWithTheExactCut()
    {
        // A turn about z on a wheel that also travels: every step carries its own orientation, so the preview has to
        // rotate the half-spaces rather than move the body alone.
        ConvexTool tooth = BoxTool(1.2, 0.3, 0.3);
        Solid solid = BoxSolid(1.2, 0.3, 0.3);
        const double turn = 1.2;                       // about 69 degrees, sampled into steps of its own
        int stepsForTurn = ConvexStep.StepsForRotation(turn, tooth.RadiusMm, 0.002);
        output.WriteLine($"{stepsForTurn} steps for {turn:F3} rad at {tooth.RadiusMm:F3} mm");

        var at = (X: 10.0, Y: 10.0, Z: 10.0);
        var end = (X: 13.0, Y: 10.0, Z: 10.0);
        var motion = Motion3.Between(
            Pose3.Identity with { TxNm = (long)(at.X * 1e6), TyNm = (long)(at.Y * 1e6), TzNm = (long)(at.Z * 1e6) },
            Pose3.Rotation(turn, 0, 0, 1, Vec3.Mm(at.X, at.Y, at.Z)) with { TxNm = (long)(end.X * 1e6), TyNm = (long)(end.Y * 1e6), TzNm = (long)(end.Z * 1e6) });

        // The preview samples the turn; the exact kernel hulls both poses of each of its own, finer intervals. The
        // preview's steps are the coarser of the two, so it cuts a hair less where the wheel turns.
        var steps = new ConvexStep[stepsForTurn];
        for (int i = 0; i < stepsForTurn; i++)
        {
            double t0 = (double)i / stepsForTurn, t1 = (double)(i + 1) / stepsForTurn;
            steps[i] = new ConvexStep(
                Orientation3.AboutZ(turn * 0.5 * (t0 + t1)),
                (at.X + (end.X - at.X) * t0, at.Y, at.Z),
                (at.X + (end.X - at.X) * t1, at.Y, at.Z));
        }

        // A rotation is the one motion the exact kernel samples linearly in the angle: it holds diameter · dθ / 2
        // under its sweep tolerance on top of the chord, so the default 30 nm asks for ~50 000 poses for this turn
        // (65 536 with the binary subdivision it uses) -- one exact hull and one Boolean each. 4 µm is the preview's
        // own sampling (2 µm chord, 11 steps) with room for the hull to overcut, and both are far below the 40 µm
        // cell, so it is what the comparison runs on: 512 exact intervals, 72 s.
        const int sweepNm = 4000;
        PreviewAgainstProcess3(solid, motion, tooth, steps, 500, Tolerance.Budget(totalUm: 5, chordNm: 50, sweepNm));
    }

    [Fact]
    public void ManySmallStepsCutTheSameAsTheOneBigStep()
    {
        // Splitting a move into pieces is what a long program does; the sweep is convex, so the pieces together must
        // remove what the one step does.
        ConvexTool oct = ConvexTool.Octahedron(1.5);
        var one = Stock(500, k: 8);
        var many = Stock(500, k: 8);
        one.ApplyConvexSteps(oct, [ConvexStep.Move((4, 4, 6), (16, 16, 14))]);

        var pieces = new List<ConvexStep>();
        const int n = 40;
        for (int i = 0; i < n; i++)
        {
            double t0 = (double)i / n, t1 = (double)(i + 1) / n;
            pieces.Add(ConvexStep.Move((4 + 12 * t0, 4 + 12 * t0, 6 + 8 * t0), (4 + 12 * t1, 4 + 12 * t1, 6 + 8 * t1)));
        }
        many.ApplyConvexSteps(oct, [.. pieces]);

        output.WriteLine($"one step {one.RemovedVolumeMm3:F6} mm³, {n} pieces {many.RemovedVolumeMm3:F6} mm³");
        Assert.Equal(one.Counts, many.Counts);
        Assert.Equal(one.Overflows, many.Overflows);
    }

    [Fact]
    public void WithOneIntervalPerColumnEveryColumnIsCutDownToTheSweepsBottom()
    {
        // The same invariant the sphere has: k = 1 has no room for a roof, so every subtraction cuts through to the
        // top and what is left is the lowest bottom of all the steps -- the height field again. Each step's bottom is
        // taken from the packed half-spaces here and put through a plain loop, so what is checked is the whole path:
        // the packing, the binning, and the order of the subtractions.
        ConvexTool box = BoxTool(1.2, 0.8, 1.5);
        var steps = new[] { ConvexStep.Move((4, 10, 9), (16, 10, 12)), ConvexStep.Move((16, 10, 12), (6, 11, 17)) };
        var dexel = Stock(97, k: 1);
        dexel.ApplyConvexSteps(box, steps);
        Assert.True(dexel.Overflows > 0);
        Assert.All(dexel.Counts, c => Assert.Equal(1, c));

        float[] packed = ConvexProfile.Pack(steps, box, (0, 0, 0));
        float[] planes = ConvexProfile.PackPlanes(box);
        float[] tops = dexel.TopHeights();
        int compared = 0;
        for (int j = 0; j < dexel.CellsY; j++)
        {
            float y = (float)((j + 0.5) * dexel.CellSizeYMm);
            for (int i = 0; i < dexel.CellsX; i++)
            {
                float x = (float)((i + 0.5) * dexel.CellSizeXMm);
                // A column no step reaches keeps the stock, so the walk starts at the stock's top and not at zero.
                float lowest = dexel.TopRelative;
                for (int s = 0; s < steps.Length; s++)
                    if (ConvexProfile.Span(x, y, packed, planes, s, out float lo, out _))
                        lowest = MathF.Min(lowest, lo);
                Assert.Equal(lowest, tops[(long)j * dexel.CellsX + i], 5);
                compared++;
            }
        }
        Assert.True(compared > 9_000, $"only {compared} columns");
    }

    [Fact]
    public void ThreadCountDoesNotMatter()
    {
        ConvexTool box = BoxTool(1, 1.4, 0.6);
        var steps = new[] { ConvexStep.Move((2, 2, 8), (18, 15, 13)), ConvexStep.Move((18, 15, 13), (3, 17, 5)), ConvexStep.At((10, 10, 6)) };
        var a = Stock(123, k: 6, backend: new CpuBackend(1));
        var b = Stock(123, k: 6, backend: new CpuBackend(16));
        a.ApplyConvexSteps(box, steps);
        b.ApplyConvexSteps(box, steps);
        Assert.Equal(a.Counts, b.Counts);
        Assert.Equal(a.Intervals, b.Intervals);
        Assert.Equal(a.Overflows, b.Overflows);
    }

    [Fact]
    public void AnEmptyStepListChangesNothing()
    {
        var map = Stock(32);
        double before = map.RemovedVolumeMm3;
        map.ApplyConvexSteps(BoxTool(1, 1, 1), []);
        Assert.Equal(before, map.RemovedVolumeMm3);
        Assert.Equal(0, map.AppliedSteps);
    }

    // ---- the GPU ---------------------------------------------------------------------------------------

    private static ConvexTool[] MixedTools() =>
        [BoxTool(1.5, 0.6, 0.6), ConvexTool.Octahedron(2.2), ConvexTool.Ball(2.0, 12), BoxTool(0.8, 0.8, 2.5)];

    private static ConvexStep[] MixedSteps(ConvexTool tool)
    {
        var rng = new Random(91);
        double R(double a, double b) => a + rng.NextDouble() * (b - a);
        var steps = new List<ConvexStep>();
        var at = (X: 10.0, Y: 10.0, Z: 20.0);
        for (int i = 0; i < 220; i++)
        {
            var next = i % 6 == 0 ? (R(2, 18), R(2, 18), R(4, 17))
                     : (at.X + R(-3, 3), at.Y + R(-3, 3), at.Z + R(-2, 2));
            steps.Add(new ConvexStep(Orientation3.AboutZ(R(-0.6, 0.6)), at, next));
            at = next;
        }
        _ = tool;
        return [.. steps];
    }

    private static (int Columns, double Worst, int Differing) Compare(DexelMap a, DexelMap b)
    {
        int k2 = a.MaxIntervals * 2, differing = 0;
        double worst = 0;
        for (long c = 0; c < a.Counts.Length; c++)
        {
            if (a.Counts[c] != b.Counts[c]) { differing++; continue; }
            for (int q = 0; q < 2 * a.Counts[c]; q++)
                worst = Math.Max(worst, Math.Abs(a.Intervals[c * k2 + q] - b.Intervals[c * k2 + q]));
        }
        return (a.Counts.Length, worst, differing);
    }

    /// <summary>
    /// CUDA against the CPU reference, on every tool the plan names. fma contraction moves a column by a few float
    /// units, so only columns at a rim can then differ in their interval count; everywhere else they agree to float
    /// rounding.
    /// </summary>
    [Fact]
    public void CudaAgreesWithTheCpuReference()
    {
        var cuda = new CudaBackend();
        if (!cuda.IsAvailable)
        {
            output.WriteLine($"not run: {cuda.UnavailableReason}");
            return;
        }
        foreach (ConvexTool tool in MixedTools())
        {
            ConvexStep[] steps = MixedSteps(tool);
            var cpu = Stock(241, k: 6);
            var gpu = Stock(241, k: 6, backend: cuda);
            cpu.ApplyConvexSteps(tool, steps, ZMapReadBack.Always);
            gpu.ApplyConvexSteps(tool, steps, ZMapReadBack.Always);
            var (columns, worst, differing) = Compare(cpu, gpu);
            output.WriteLine($"{tool.PlaneCount,2} planes: {columns} columns, worst {worst:E2} mm, {differing} differ in " +
                             $"count; volume cpu {cpu.RemovedVolumeMm3:F6} cuda {gpu.BackendRemovedVolumeMm3:F6}; " +
                             $"overflows {cpu.Overflows} / {gpu.Overflows}");

            // The ends of an interval come out of a division by mz, which is where the conditioning goes: a half-space
            // whose normal lies near the horizontal divides by a small number, and fma contraction moves the end by an
            // ulop times that. A hundredth of a cell is what it is worth, and it only ever shows on a rim column --
            // nowhere in the map do the two backends cut a different set of columns.
            float cell = (float)cpu.CellSizeXMm;
            Assert.True(worst < cell / 100, $"intervals differ by {worst:E2} mm, over a hundredth of a {cell:F4} mm cell");
            Assert.True(differing <= columns / 10_000, $"{differing} columns differ in their interval count");
            Assert.Equal(cpu.RemovedVolumeMm3, gpu.BackendRemovedVolumeMm3, 1e-4 * cpu.RemovedVolumeMm3);
            Assert.Equal(cpu.Overflows, gpu.Overflows);
        }
    }

    /// <summary>The binned launch against the unbinned one, bit for bit, for the convex layout as well.</summary>
    [Fact]
    public void BinnedLaunchGivesTheSameBitsAsTheUnbinnedOne()
    {
        var binned = new CudaBackend { BinSteps = true };
        var plain = new CudaBackend { BinSteps = false };
        if (!binned.IsAvailable)
        {
            output.WriteLine($"not run: {binned.UnavailableReason}");
            return;
        }
        ConvexTool tool = ConvexTool.Ball(1.6, 12);
        ConvexStep[] steps = MixedSteps(tool);
        long overflows = 0;
        foreach (int cells in new[] { 16, 32, 64, 241 })
        {
            var a = Stock(cells, k: 6, backend: binned);
            var b = Stock(cells, k: 6, backend: plain);
            a.ApplyConvexSteps(tool, steps, ZMapReadBack.Always);
            b.ApplyConvexSteps(tool, steps, ZMapReadBack.Always);
            var (columns, worst, differing) = Compare(a, b);
            overflows += a.Overflows;
            output.WriteLine($"{cells,3} cells: {differing} of {columns} columns differ in count, worst {worst:E2} mm, " +
                             $"volume {a.RemovedVolumeMm3:F4} against {b.RemovedVolumeMm3:F4}, " +
                             $"overflows {a.Overflows} / {b.Overflows}, bin {a.LastTiming.BinMs:F3} ms");

            Assert.Equal(0, differing);
            Assert.Equal(0, worst);
            Assert.Equal(b.Overflows, a.Overflows);
        }

        // Without this the comparison is 0 == 0 on a scene that never reaches the capacity guard, and says nothing
        // about it. If it fails, the scene is the thing that is wrong -- not the assertion.
        Assert.True(overflows > 0, $"no column overflowed at k = 6 over the four sizes, so the bit-for-bit " +
                                   $"comparison never reaches the guard it is read as covering ({overflows})");
    }

    /// <summary>The two launches in a row on one device: the stride changes, so the step buffer has to follow it.</summary>
    [Fact]
    public void ASphereThenAConvexToolOnTheSameDevice()
    {
        var cuda = new CudaBackend();
        if (!cuda.IsAvailable)
        {
            output.WriteLine($"not run: {cuda.UnavailableReason}");
            return;
        }
        var map = Stock(129, k: 6, backend: cuda);
        map.ApplySteps([new BallStep((5, 10, 10), (15, 10, 10), 1.5)]);
        var alone = Stock(129, k: 6, backend: cuda);
        alone.ApplyConvexSteps(BoxTool(1, 1, 1), [ConvexStep.Move((5, 10, 10), (15, 10, 10))], ZMapReadBack.Always);

        // The sphere ran first in both maps, so the convex part starts from the same state on both.
        var fresh = Stock(129, k: 6, backend: cuda);
        fresh.ApplyConvexSteps(BoxTool(1, 1, 1), [ConvexStep.Move((5, 10, 10), (15, 10, 10))], ZMapReadBack.Always);
        Assert.Equal(fresh.Counts, alone.Counts);
        Assert.Equal(fresh.Intervals, alone.Intervals);
        Assert.True(map.Counts.Length > 0);
    }

    /// <summary>The other order on one device: a convex program, and a sphere program on the same map after it.</summary>
    /// <remarks>
    /// The device dexel lives as long as its <see cref="DexelMap"/>, and the kernel tells a sphere from a polytope
    /// by the plane pointer it is handed. So the half-spaces of the convex run are still in the device when the ball
    /// program arrives -- and without a plane count of its own the ball is cut with none, which is no tool at all: it
    /// takes the whole map with it, silently, with the overflow counter still at zero. The reference is the same two
    /// programs on the CPU, which has no such state to leak, compared the way
    /// <see cref="CudaAgreesWithTheCpuReference"/> compares the two arms.
    /// </remarks>
    [Fact]
    public void AConvexToolThenASphereOnTheSameDevice()
    {
        var cuda = new CudaBackend();
        if (!cuda.IsAvailable)
        {
            output.WriteLine($"not run: {cuda.UnavailableReason}");
            return;
        }
        ConvexTool box = BoxTool(1, 1, 1);
        ConvexStep[] convex = [ConvexStep.Move((5, 10, 10), (15, 10, 10))];
        BallStep[] ball = [new BallStep((5, 10, 10), (15, 10, 10), 1.5)];

        var gpu = Stock(129, k: 6, backend: cuda);
        gpu.ApplyConvexSteps(box, convex, ZMapReadBack.Always);
        gpu.ApplySteps(ball);

        var cpu = Stock(129, k: 6);
        cpu.ApplyConvexSteps(box, convex);
        cpu.ApplySteps(ball);
        var (columns, worst, differing) = Compare(cpu, gpu);
        output.WriteLine($"convex then sphere: {gpu.RemovedVolumeMm3:F4} against {cpu.RemovedVolumeMm3:F4} mm³, " +
                         $"{differing} of {columns} columns differ in count, worst {worst:E2} mm, " +
                         $"overflows {cpu.Overflows} / {gpu.Overflows}");

        // The programs have to have removed something, or the comparison is empty against empty.
        Assert.True(cpu.RemovedVolumeMm3 > 0);
        float cell = (float)cpu.CellSizeXMm;
        Assert.True(worst < cell / 100, $"intervals differ by {worst:E2} mm, over a hundredth of a {cell:F4} mm cell");
        Assert.True(differing <= columns / 10_000, $"{differing} columns differ in their interval count");
    }

    /// <summary>The solid the octahedron is, so both sides of the comparison sweep the very same body.</summary>
    private static Solid SolidFromConvex(ConvexTool tool)
    {
        // The six corners of the octahedron are what its eight planes meet in, and a hull of them is exactly the body
        // the planes describe — so the exact kernel and the kernel's own half-spaces cannot drift apart.
        return ConvexHull3.Compute(tool.CornersMm.Select(c => Vec3.Mm(c.X, c.Y, c.Z)).ToArray());
    }
}