namespace Stykker.NanoCut.Gpu;

/// <summary>
/// The steps of a batch binned into tiles of columns, as a CSR: tile <c>t</c> owns the step indices
/// <see cref="Bins.TileStart"/>[t] … <see cref="Bins.TileStart"/>[t + 1] of <see cref="Bins.TileSteps"/>. One tile is one
/// thread block of the CUDA launch, so a block only walks the steps that can reach its columns instead of all of them —
/// that is what turns the launch from columns × steps into what the tool actually touches.
/// </summary>
/// <remarks>
/// A step is binned by the horizontal box of what it sweeps, which both layouts write into the first fields: the
/// sphere's is the segment from (x0, y0) to (x0 + wx, y0 + wy) grown by the radius r, the convex one's is the
/// bounding box of the polytope at both ends of the step. Every column the body can reach lies inside that box — the
/// kernel measures exactly the vertical interval it has there — so the box never loses a step, and it is
/// deliberately generous: a step lands in more tiles than it strictly needs, never in fewer.
/// <para>
/// The step indices inside a tile come out ascending, because the steps are walked in order. That is what keeps the
/// result independent of the binning: each column still sees its steps in the same order as the unbinned launch, so
/// both the intervals and the overflow count are the same bits.
/// </para>
/// </remarks>
internal static class StepBins
{
    /// <summary>Columns and rows per tile. One tile is one thread block, so this has to match the kernel's launch.</summary>
    internal const int Tile = 16;

    /// <summary>Grown onto the box so a column exactly on the boundary cannot be lost to the rounding of a float. The
    /// convex early-out in <see cref="ConvexProfile.Span"/> compares a column against the same window, so it can never
    /// be stricter than the binning that assigned the step to this column's tile.</summary>
    internal const float MarginMm = 1e-6f;

    /// <summary>Where the steps of a batch live, per tile.</summary>
    /// <param name="TileStart">Offsets into <paramref name="TileSteps"/>, tileCount + 1 of them.</param>
    /// <param name="TileSteps">Step indices, ascending inside each tile.</param>
    /// <param name="TileCount">Number of tiles, tilesX × tilesY.</param>
    /// <param name="TilesX">Tiles per row, which is what splits a tile index into column and row.</param>
    /// <param name="References">How many step indices there are over all tiles.</param>
    internal readonly record struct Bins(int[] TileStart, int[] TileSteps, int TileCount, int TilesX, int References);

    /// <summary>Bins packed steps into the tiles of a map of the given geometry.</summary>
    /// <param name="steps">The packed steps, as <see cref="ToolProfile.Pack"/> or <see cref="ConvexProfile.Pack"/> writes them.</param>
    /// <param name="stride">Floats per step, which differs between the sphere and the convex layout.</param>
    /// <param name="convex">True for the convex layout, whose steps start with the swept body's bounding box.</param>
    /// <param name="cellsX">Columns of the map.</param>
    /// <param name="cellsY">Rows of the map.</param>
    /// <param name="cellX">Column width in mm.</param>
    /// <param name="cellY">Row height in mm.</param>
    internal static Bins Build(ReadOnlySpan<float> steps, int stride, bool convex,
        int cellsX, int cellsY, float cellX, float cellY)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellsX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellsY);
        if (!(cellX > 0f)) throw new ArgumentOutOfRangeException(nameof(cellX));
        if (!(cellY > 0f)) throw new ArgumentOutOfRangeException(nameof(cellY));

        int stepCount = steps.Length / stride;
        int tilesX = (cellsX + Tile - 1) / Tile;
        int tilesY = (cellsY + Tile - 1) / Tile;
        int tileCount = tilesX * tilesY;

        // Count first, then place: one pass over the steps per phase, no per-step buffer in between.
        var counts = new int[tileCount];
        for (int s = 0; s < stepCount; s++)
        {
            Range r = Tiles(steps, s, stride, convex, cellX, cellY, tilesX, tilesY);
            if (!r.Touches) continue;
            for (int ty = r.Y0; ty <= r.Y1; ty++)
                for (int tx = r.X0; tx <= r.X1; tx++)
                    counts[ty * tilesX + tx]++;
        }

        var tileStart = new int[tileCount + 1];
        for (int t = 0; t < tileCount; t++) tileStart[t + 1] = tileStart[t] + counts[t];
        var tileSteps = new int[tileStart[tileCount]];
        var cursor = (int[])tileStart.Clone();

        // Ascending s is what makes every tile's list ascending.
        for (int s = 0; s < stepCount; s++)
        {
            Range r = Tiles(steps, s, stride, convex, cellX, cellY, tilesX, tilesY);
            if (!r.Touches) continue;
            for (int ty = r.Y0; ty <= r.Y1; ty++)
                for (int tx = r.X0; tx <= r.X1; tx++)
                    tileSteps[cursor[ty * tilesX + tx]++] = s;
        }

        return new Bins(tileStart, tileSteps, tileCount, tilesX, tileSteps.Length);
    }

    /// <summary>The tiles a step touches, as tile coordinates; <see cref="Touches"/> is false when it reaches no column.</summary>
    private readonly record struct Range(bool Touches, int X0, int Y0, int X1, int Y1);

    /// <summary>The tiles the body swept by one step can reach.</summary>
    /// <remarks>
    /// The sphere's box is the segment from (x0, y0) to (x0 + wx, y0 + wy) grown by the radius r, because the
    /// swept capsule is a ball around it and every column it reaches lies inside. The convex layout has no radius:
    /// <see cref="ConvexProfile.Pack"/> already put the axis-aligned box of the swept polytope in the first four
    /// fields, corners rotated and then moved to both ends of the step, so that box is what is read here. Both grow
    /// the result by the margin, so a column exactly on the boundary cannot be lost to the rounding of a float.
    /// </remarks>
    private static Range Tiles(ReadOnlySpan<float> steps, int s, int stride, bool convex,
        float cellX, float cellY, int tilesX, int tilesY)
    {
        int o = s * stride;
        float x0, x1, y0, y1;
        if (convex)
        {
            x0 = steps[o] - MarginMm;
            y0 = steps[o + 1] - MarginMm;
            x1 = x0 + steps[o + 2] + 2 * MarginMm;
            y1 = y0 + steps[o + 3] + 2 * MarginMm;
        }
        else
        {
            float r = steps[o + 3] + MarginMm;
            float ax = steps[o], ay = steps[o + 1], bx = ax + steps[o + 4], by = ay + steps[o + 5];
            x0 = MathF.Min(ax, bx) - r;
            x1 = MathF.Max(ax, bx) + r;
            y0 = MathF.Min(ay, by) - r;
            y1 = MathF.Max(ay, by) + r;
        }

        // Column i has its centre at (i + 0.5) · cell, so it is inside the box when i is between these two.
        int c0 = (int)MathF.Ceiling(x0 / cellX - 0.5f);
        int c1 = (int)MathF.Floor(x1 / cellX - 0.5f);
        int j0 = (int)MathF.Ceiling(y0 / cellY - 0.5f);
        int j1 = (int)MathF.Floor(y1 / cellY - 0.5f);
        if (c1 < 0 || j1 < 0 || c0 >= tilesX * Tile || j0 >= tilesY * Tile) return new Range(false, 0, 0, 0, 0);
        c0 = Math.Max(c0, 0);
        j0 = Math.Max(j0, 0);
        c1 = Math.Min(c1, tilesX * Tile - 1);
        j1 = Math.Min(j1, tilesY * Tile - 1);
        return new Range(true, c0 / Tile, j0 / Tile, c1 / Tile, j1 / Tile);
    }
}