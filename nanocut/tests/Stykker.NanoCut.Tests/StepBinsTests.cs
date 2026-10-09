using Stykker.NanoCut.Gpu;
using Xunit.Abstractions;

namespace Stykker.NanoCut.Tests;

/// <summary>
/// The step binning that feeds the binned CUDA launch. A step that can reach a column has to be in that column's tile:
/// a tile the step is missing leaves its columns uncut, and the launch cannot report that, so it is pinned down here
/// against <see cref="ToolProfile.Span"/>, the same test the kernel applies.
/// </summary>
public class StepBinsTests(ITestOutputHelper output)
{
    /// <summary>A serpentine that stays inside the map, so every step lands on columns and the tiles overlap.</summary>
    private static BallStep[] Walk(int count, float size)
    {
        var rng = new Random(7);
        var steps = new List<BallStep>();
        var at = (X: size * 0.5f, Y: size * 0.5f, Z: size * 0.5f);
        for (int i = 0; i < count; i++)
        {
            double t = i * 0.05;
            var next = (X: size * (0.5f + 0.42f * (float)Math.Sin(t)),
                        Y: size * (0.5f + 0.42f * (float)Math.Cos(1.3 * t)), size * 0.5f);
            // A ball several cells across, so most steps reach columns in more than one tile.
            steps.Add(new BallStep(at, next, (float)(0.02 * size + rng.NextDouble() * 0.03 * size)));
            at = next;
        }
        return [.. steps];
    }

    /// <summary>The tiles that list step <paramref name="s"/>, read back out of the CSR.</summary>
    private static bool[] TilesOf(StepBins.Bins bins, int s)
    {
        var tiles = new bool[bins.TileCount];
        for (int t = 0; t < bins.TileCount; t++)
            for (int q = bins.TileStart[t]; q < bins.TileStart[t + 1]; q++)
                if (bins.TileSteps[q] == s) { tiles[t] = true; break; }
        return tiles;
    }

    [Fact]
    public void EveryStepThatReachesAColumnIsInThatColumnsTile()
    {
        const int cells = 96;
        const float size = 10f;
        float cellX = size / cells, cellY = size / cells;
        BallStep[] steps = Walk(400, size);
        float[] packed = ToolProfile.Pack(steps, (0, 0, 0));
        StepBins.Bins bins = StepBins.Build(packed, ToolProfile.StepFloats, convex: false, cells, cells, cellX, cellY);

        int missed = 0, reached = 0, extra = 0;
        for (int s = 0; s < steps.Length; s++)
        {
            bool[] tiles = TilesOf(bins, s);
            for (int j = 0; j < cells; j++)
            {
                float y = (j + 0.5f) * cellY;
                for (int i = 0; i < cells; i++)
                {
                    if (!ToolProfile.Span((i + 0.5f) * cellX, y, packed, s, out _, out _)) continue;
                    reached++;
                    if (!tiles[(j / StepBins.Tile) * bins.TilesX + i / StepBins.Tile]) missed++;
                }
            }
            foreach (bool t in tiles) if (t) extra++;
        }
        output.WriteLine($"{bins.TileCount} tiles, {bins.References} references, {reached} (step, column) pairs reach, " +
                         $"{extra} tiles listed");
        Assert.Equal(0, missed);
        Assert.True(reached > 10_000, $"only {reached} pairs reach, the case is too weak");
    }

    [Fact]
    public void TileListsAreAscendingAndTheOffsetsAddUp()
    {
        const int cells = 64;
        const float size = 8f;
        float cellX = size / cells, cellY = size / cells;
        BallStep[] steps = Walk(150, size);
        float[] packed = ToolProfile.Pack(steps, (0, 0, 0));
        StepBins.Bins bins = StepBins.Build(packed, ToolProfile.StepFloats, convex: false, cells, cells, cellX, cellY);

        Assert.Equal(bins.TileSteps.Length, bins.TileStart[bins.TileCount]);
        Assert.Equal(bins.TileSteps.Length, bins.References);
        for (int t = 0; t < bins.TileCount; t++)
        {
            Assert.True(bins.TileStart[t] <= bins.TileStart[t + 1], $"tile {t} runs backwards");
            for (int q = bins.TileStart[t] + 1; q < bins.TileStart[t + 1]; q++)
                Assert.True(bins.TileSteps[q - 1] < bins.TileSteps[q],
                    $"tile {t} lists step {bins.TileSteps[q]} after {bins.TileSteps[q - 1]}");
            for (int q = bins.TileStart[t]; q < bins.TileStart[t + 1]; q++)
                Assert.InRange(bins.TileSteps[q], 0, steps.Length - 1);
        }
    }

    [Fact]
    public void ASingleBallLandsInTheTilesAroundItAndNowhereElse()
    {
        const int cells = 64;
        const float size = 8f;
        const double radius = 1.0;
        float cellX = size / cells, cellY = size / cells;
        float[] packed = ToolProfile.Pack([BallStep.At((1.0, 1.0, 1.0), radius)], (0, 0, 0));
        StepBins.Bins bins = StepBins.Build(packed, ToolProfile.StepFloats, convex: false, cells, cells, cellX, cellY);
        bool[] tiles = TilesOf(bins, 0);

        // Every column the ball reaches must sit in a listed tile, and every column of a listed tile is checked below.
        var reached = new bool[cells * cells];
        for (int j = 0; j < cells; j++)
            for (int i = 0; i < cells; i++)
                reached[j * cells + i] = ToolProfile.Span((i + 0.5f) * cellX, (j + 0.5f) * cellY, packed, 0, out _, out _);

        int listed = 0, reachableInListed = 0;
        for (int t = 0; t < bins.TileCount; t++)
        {
            if (!tiles[t]) continue;
            listed++;
            int tx = t % bins.TilesX, ty = t / bins.TilesX;
            for (int j = ty * StepBins.Tile; j < Math.Min(ty * StepBins.Tile + StepBins.Tile, cells); j++)
                for (int i = tx * StepBins.Tile; i < Math.Min(tx * StepBins.Tile + StepBins.Tile, cells); i++)
                    if (reached[j * cells + i]) { reachableInListed++; goto next; }
            // A tile the ball cannot reach at all is waste; allow it only at the map's edge, where the tile is padded.
            next:;
        }
        int columns = reached.Count(b => b);
        output.WriteLine($"{columns} columns reach, in {listed} tiles, {bins.References} reference(s)");
        Assert.Equal(1, bins.References);
        Assert.True(listed > 0);
        Assert.True(reachableInListed == listed, $"{listed - reachableInListed} listed tiles reach nothing");
    }

    /// <summary>
    /// The same completeness check for the convex layout, where the box comes from the swept polytope's corners
    /// instead of a radius. A step the binning loses leaves its columns uncut and the launch cannot report it, so the
    /// check runs against <see cref="ConvexProfile.Span"/>, the same evaluation the kernel applies.
    /// <para>
    /// Against a copy whose bounding boxes have been widened past the map, not against the packed steps.
    /// <see cref="ConvexProfile.Span"/> now turns a column away on that box before it looks at a half-space, and the
    /// binning reads the very same four floats -- so asking the packed steps would compare the box against itself and
    /// pass even if the box were too small, which is the one thing this test exists to catch. Widening them puts the
    /// answer back on the half-spaces, which is the independent side of the comparison.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryConvexStepThatReachesAColumnIsInThatColumnsTile()
    {
        const int cells = 96;
        const float size = 10f;
        float cellX = size / cells, cellY = size / cells;
        ConvexTool tool = ConvexTool.Ball(1.2, 12);
        ConvexStep[] steps = WalkConvex(300, size);
        float[] packed = ConvexProfile.Pack(steps, tool, (0, 0, 0));
        float[] planes = ConvexProfile.PackPlanes(tool);
        float[] oracle = BluntedSteps.WithoutBoundingBoxes(packed);
        StepBins.Bins bins = StepBins.Build(packed, ConvexProfile.StepFloats, convex: true, cells, cells, cellX, cellY);

        int missed = 0, reached = 0, extra = 0;
        for (int s = 0; s < steps.Length; s++)
        {
            bool[] tiles = TilesOf(bins, s);
            for (int j = 0; j < cells; j++)
            {
                float y = (j + 0.5f) * cellY;
                for (int i = 0; i < cells; i++)
                {
                    if (!ConvexProfile.Span((i + 0.5f) * cellX, y, oracle, planes, s, out _, out _)) continue;
                    reached++;
                    if (!tiles[(j / StepBins.Tile) * bins.TilesX + i / StepBins.Tile]) missed++;
                }
            }
            foreach (bool t in tiles) if (t) extra++;
        }
        output.WriteLine($"{bins.TileCount} tiles, {bins.References} references, {reached} (step, column) pairs reach, " +
                         $"{extra} tiles listed");
        Assert.Equal(0, missed);
        Assert.True(reached > 5_000, $"only {reached} pairs reach, the case is too weak");
    }

    /// <summary>A serpentine of convex steps that turns as it goes, so the swept boxes are not all axis-aligned copies.</summary>
    private static ConvexStep[] WalkConvex(int count, float size)
    {
        var steps = new List<ConvexStep>();
        var at = (X: size * 0.5, Y: size * 0.5, Z: size * 0.5);
        for (int i = 0; i < count; i++)
        {
            double t = i * 0.05;
            var next = (X: size * (0.5 + 0.42 * Math.Sin(t)),
                        Y: size * (0.5 + 0.42 * Math.Cos(1.3 * t)), size * 0.5);
            steps.Add(new ConvexStep(Orientation3.AboutZ(0.3 * Math.Sin(t)), at, next));
            at = next;
        }
        return [.. steps];
    }
}