namespace Stykker.NanoCut.Gpu;

/// <summary>
/// The convex-tool profile both backends evaluate, next to <see cref="ToolProfile"/> for the sphere: the same packing,
/// the same layout, so that the CPU reference and the CUDA kernel cannot drift apart.
/// <para>
/// A step carries the tool's orientation and its two positions; the planes themselves are the same for every step of a
/// program and are uploaded once. The swept body is convex, so where a vertical line (a column) meets it is one
/// interval [low, high], and both ends come out of a small linear program in (z, t): for the half-spaces rotated into
/// world coordinates the constraint is
/// </para>
/// <code>
///     mᵢ·(p(z) − T<sub>A</sub>) − t·(mᵢ·w) ≤ dᵢ      ⟺      mzᵢ·z ≤ cᵢ + gᵢ·t,     0 ≤ t ≤ 1
/// </code>
/// <para>
/// where m = R·n, w = T<sub>B</sub> − T<sub>A</sub> and c = d + m·T<sub>A</sub> − (m<sub>x</sub>·x + m<sub>y</sub>·y). The
/// half-spaces with mz &lt; 0 bound z from below and give z<sub>min</sub>(t) = max (c + g·t)/mz, the ones with mz &gt; 0
/// from above, and the ones with mz = 0 bound t alone. Then
/// </para>
/// <code>
///     low  = min over t in [tLo, tHi] of z<sub>min</sub>(t)
///     high = max over t in [tLo, tHi] of z<sub>max</sub>(t)
/// </code>
/// <para>
/// and both are the same as the minimum and maximum z of the swept body on that column, because the sweep is convex:
/// the vertical section of a convex body is one segment, so a low above a high can only mean the column misses the
/// tool. Both ends are read at the same t, which is what keeps them honest: a column that misses the tool gets
/// low &gt; high, never a cut.
/// </para>
/// </summary>
/// <remarks>
/// The minimum of z<sub>min</sub> over t is not at an arbitrary t. z<sub>min</sub> is convex and piecewise linear, so
/// its minimum on an interval is either at an end or at a corner where the active line changes, that is at an
/// intersection of two of the lines. Those are the candidates this walks: both ends, then every pair. The value at a
/// candidate has to be the envelope's, not the two lines' — a line can cross another below the envelope, and taking
/// the pair's own value would cut away more than the tool ever did. That is what costs the inner loop over all
/// lines, and it is why this is O(m³) in the number of half-spaces rather than O(m²); the alternative, building the
/// envelope itself, is O(m) but needs a sorted slope list and a stack, and is left for the step that measures this.
/// <para>
/// Both ends are read at those same candidates, but over the t the column is really inside the body at: z has to be
/// over the lower bound and under the upper one, so every line from below has to sit under every line from above,
/// and each of those pairs is one bound on t. The lines are affine, so what they leave of t is one interval, and both
/// ends are walked inside it. Reading them over all of t instead -- the lowest of z<sub>min</sub> and the highest of
/// z<sub>max</sub>, each on its own -- is what a column that misses the tool reports as a cut: the two ends then come
/// from two different t, and they cross even though the column was outside the body at every one of them.
/// </para>
/// <para>
/// The division by mz is where the conditioning goes: a half-space whose normal is nearly horizontal gives a
/// constraint that mostly bounds t, not z, and dividing by a small mz loses that. It cannot happen for a turn about
/// the z-axis, where m<sub>z</sub> = n<sub>z</sub> regardless of the angle, so a spindle does not touch it; for a
/// tilted rotation it does, and the caller should keep the tool's normals away from the horizontal.
/// </para>
/// </remarks>
internal static class ConvexProfile
{
    /// <summary>Floats per half-space: the unit normal and the distance.</summary>
    internal const int PlaneFloats = 4;

    /// <summary>
    /// Floats per packed step: the four values of the swept body's bounding box that the binning reads, then
    /// T<sub>A</sub> (3), the move w (3), the half-space count (1) and the rotation (9) — 20 of them. The layout takes
    /// 32, so a step is 128 bytes and every one of them starts on a 64-byte boundary.
    /// </summary>
    internal const int StepFloats = 32;

    /// <summary>Offset of the swept body's bounding box in x and y: (min x, min y, width, width).</summary>
    internal const int BoxOffset = 0;

    /// <summary>Grown onto that box before a column is tested against it, so a centre exactly on the boundary is not
    /// lost to the rounding of a float. The same value as <c>StepBins.MarginMm</c> — which is this constant — and
    /// <c>kConvexBoxMarginMm</c> in the kernel.
    /// <para>
    /// The margin has to outlast the coordinate's own float resolution, because that is what eats it: fl(c - margin)
    /// == c as soon as ulp(c) exceeds twice the margin, which for 1e-6 mm is already the case at 32 mm, and — because
    /// the rounding accumulates over the corner, the width and the two ends — the far edge falls inside the body
    /// already at 17 mm. 1e-4 mm survives to about a metre and is a fifth of a 0.05 mm cell.
    /// </para>
    /// <para>
    /// Even so it is not a tolerance that absorbs the whole float chain. The box is stored as independently rounded
    /// floats and the consumer adds them, so a reconstructed edge sits a few ulps of the coordinate off, and past
    /// this margin once the coordinates leave single-digit millimetres. That is inherited from the binning, which
    /// reads the same four floats and reaches the same pairs, and no fixed margin absorbs it: see
    /// CudaLongProgramsFindings.md §9.4.
    /// </para></summary>
    internal const float BoxMarginMm = 1e-4f;

    /// <summary>Offset of T<sub>A</sub>, relative to the grid origin.</summary>
    private const int FromOffset = 4;

    /// <summary>Offset of the move w = T<sub>B</sub> − T<sub>A</sub>.</summary>
    private const int MoveOffset = 7;

    /// <summary>Offset of the half-space count.</summary>
    private const int CountOffset = 10;

    /// <summary>Offset of the rotation matrix, row-major.</summary>
    private const int RotationOffset = 11;

    /// <summary>
    /// Half-spaces the kernel accepts. What this sits on is the runtime: finding the interval walks every crossing
    /// of two of the m lines and evaluates the envelope there over all m of them, which is O(m³) per column and step
    /// -- eight times more per tool from 16 to 32. A bigger tool is split instead, which is what <c>ToolShape</c>
    /// does for it anyway. The local arrays fit either way: the lower and the upper bound each keep 2 · m floats
    /// on top of the column's 2 · 16 intervals, which stays under the 512 bytes a thread may use.
    /// </summary>
    internal const int MaxPlanes = 16;

    /// <summary>Packs the half-spaces of the tool; every step of a program reads the same ones.</summary>
    internal static float[] PackPlanes(ConvexTool tool)
    {
        var buf = new float[tool.PlaneCount * PlaneFloats];
        for (int i = 0; i < tool.PlaneCount; i++)
        {
            var p = tool.Planes[i];
            int o = i * PlaneFloats;
            buf[o] = (float)p.Nx;
            buf[o + 1] = (float)p.Ny;
            buf[o + 2] = (float)p.Nz;
            buf[o + 3] = (float)p.D;
        }
        return buf;
    }

    /// <summary>
    /// Packs the steps, including the bounding box of each sweep: the corners of the tool put through the rotation,
    /// then through the position and both ends of the move. That box is what the binning hands to the tiles, so a
    /// step that can reach a column is in that column's tile.
    /// </summary>
    internal static float[] Pack(ReadOnlySpan<ConvexStep> steps, ConvexTool tool, (double X, double Y, double Z) origin)
    {
        if (tool.PlaneCount > MaxPlanes)
            throw new ArgumentOutOfRangeException(nameof(tool), tool.PlaneCount,
                $"the kernel keeps two lines per half-space per thread and reads {MaxPlanes}; split the tool");
        var corners = tool.CornersMm;
        var buf = new float[steps.Length * StepFloats];
        for (int s = 0; s < steps.Length; s++)
        {
            var step = steps[s];
            var (ax, ay, az) = step.FromMm;
            double wx = step.ToMm.X - ax, wy = step.ToMm.Y - ay, wz = step.ToMm.Z - az;

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var (cx, cy, cz) in corners)
            {
                var (rx, ry, rz) = step.Orientation.Apply(cx, cy, cz);
                for (int t = 0; t < 2; t++)
                {
                    // The position is part of it: R·c is the corner in the tool's own frame, and the box of the sweep
                    // is where that corner stands, not where the tool is. Leaving ax and ay out puts the box at the
                    // map's own origin and the binning loses every step that walks away from it.
                    double f = t == 0 ? 0 : 1;
                    double x = ax + rx + f * wx, y = ay + ry + f * wy;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            }

            int o = s * StepFloats;
            buf[o + BoxOffset] = (float)(minX - origin.X);
            buf[o + BoxOffset + 1] = (float)(minY - origin.Y);
            buf[o + BoxOffset + 2] = (float)(maxX - minX);
            buf[o + BoxOffset + 3] = (float)(maxY - minY);
            buf[o + FromOffset] = (float)(ax - origin.X);
            buf[o + FromOffset + 1] = (float)(ay - origin.Y);
            buf[o + FromOffset + 2] = (float)(az - origin.Z);
            buf[o + MoveOffset] = (float)wx;
            buf[o + MoveOffset + 1] = (float)wy;
            buf[o + MoveOffset + 2] = (float)wz;
            buf[o + CountOffset] = tool.PlaneCount;
            buf[o + RotationOffset] = (float)step.Orientation.M00;
            buf[o + RotationOffset + 1] = (float)step.Orientation.M01;
            buf[o + RotationOffset + 2] = (float)step.Orientation.M02;
            buf[o + RotationOffset + 3] = (float)step.Orientation.M10;
            buf[o + RotationOffset + 4] = (float)step.Orientation.M11;
            buf[o + RotationOffset + 5] = (float)step.Orientation.M12;
            buf[o + RotationOffset + 6] = (float)step.Orientation.M20;
            buf[o + RotationOffset + 7] = (float)step.Orientation.M21;
            buf[o + RotationOffset + 8] = (float)step.Orientation.M22;
        }
        return buf;
    }

    /// <summary>
    /// The interval in which the column (x, y) meets the tool swept by step s, or false when it does not.
    /// <paramref name="low"/> is the lowest and <paramref name="high"/> the highest z of the sweep on that column.
    /// </summary>
    /// <param name="x">Column centre in x, relative to the grid origin.</param>
    /// <param name="y">Column centre in y, relative to the grid origin.</param>
    /// <param name="steps">The packed steps.</param>
    /// <param name="planes">The packed half-spaces of the tool.</param>
    /// <param name="s">The step to evaluate.</param>
    /// <param name="low">Lowest z of the sweep on the column, −∞ when nothing bounds it below.</param>
    /// <param name="high">Highest z of the sweep on the column, +∞ when nothing bounds it above.</param>
    internal static bool Span(float x, float y, ReadOnlySpan<float> steps, ReadOnlySpan<float> planes, int s,
        out float low, out float high)
    {
        int o = s * StepFloats;

        // The step's own bounding box, grown by the margin, and the column centre against it. The swept body is the
        // convex hull of the two endpoint positions and this box is that hull's, so a centre outside it cannot meet
        // the body, and four comparisons replace the walk over every crossing below. The comparison is strict, so a
        // centre exactly on the boundary still goes in. Same margin as the binning, so the two filters cannot part
        // company over a column; see BoxMarginMm for what the margin does and does not absorb.
        float boxX = steps[o + BoxOffset] - BoxMarginMm, boxY = steps[o + BoxOffset + 1] - BoxMarginMm;
        float boxW = steps[o + BoxOffset + 2] + 2 * BoxMarginMm, boxH = steps[o + BoxOffset + 3] + 2 * BoxMarginMm;
        if (x < boxX || x > boxX + boxW || y < boxY || y > boxY + boxH) { low = high = 0f; return false; }

        int m = (int)steps[o + CountOffset];

        // The swept body's own box, the first four floats of the step, on the window StepBins tiles with. The binning
        // hands a step to every tile of 16 columns it touches, so a column in an assigned tile but outside the box runs
        // the whole linear program below to be told "no" -- and for a tool smaller than a tile that is most of them.
        // Four comparisons settle it. Mirrors convex_span, and it cannot be stricter than the binning.
        float bx = steps[o] - StepBins.MarginMm, by = steps[o + 1] - StepBins.MarginMm;
        if (x < bx || y < by ||
            x > bx + steps[o + 2] + 2 * StepBins.MarginMm || y > by + steps[o + 3] + 2 * StepBins.MarginMm)
        { low = high = 0f; return false; }

        float ax = steps[o + FromOffset], ay = steps[o + FromOffset + 1], az = steps[o + FromOffset + 2];
        float wx = steps[o + MoveOffset], wy = steps[o + MoveOffset + 1], wz = steps[o + MoveOffset + 2];
        float r00 = steps[o + RotationOffset], r01 = steps[o + RotationOffset + 1], r02 = steps[o + RotationOffset + 2];
        float r10 = steps[o + RotationOffset + 3], r11 = steps[o + RotationOffset + 4], r12 = steps[o + RotationOffset + 5];
        float r20 = steps[o + RotationOffset + 6], r21 = steps[o + RotationOffset + 7], r22 = steps[o + RotationOffset + 8];

        // Lines of the bounds, two floats each: the intercepts first, the slopes second. The planes with a negative
        // normal's z give the lower bound, the rest the upper. They go in separate lists because where a line lands
        // depends on how many of the other kind there are, and that count is only known at the end of the loop.
        Span<float> below = stackalloc float[2 * MaxPlanes];
        Span<float> above = stackalloc float[2 * MaxPlanes];
        int nLo = 0, nHi = 0;
        float tLo = 0f, tHi = 1f;

        for (int i = 0; i < m; i++)
        {
            int p = i * PlaneFloats;
            float nx = planes[p], ny = planes[p + 1], nz = planes[p + 2], d = planes[p + 3];
            float mx = r00 * nx + r01 * ny + r02 * nz;
            float my = r10 * nx + r11 * ny + r12 * nz;
            float mz = r20 * nx + r21 * ny + r22 * nz;
            float dot = mx * wx + my * wy + mz * wz;
            float c = d + (mx * ax + my * ay + mz * az) - mx * x - my * y;

            if (mz == 0f)
            {
                // A horizontal half-space bounds t alone: -dot·t ≤ c.
                if (dot > 0f) tLo = MathF.Max(tLo, -c / dot);
                else if (dot < 0f) tHi = MathF.Min(tHi, -c / dot);
                else if (c < 0f) { low = high = 0f; return false; }
                continue;
            }
            float inv = 1f / mz;
            Span<float> g = mz < 0f ? below : above;
            int at = 2 * (mz < 0f ? nLo++ : nHi++);
            g[at] = c * inv;
            g[at + 1] = dot * inv;
        }

        if (tLo > tHi || !Where(below, nLo, above, nHi, ref tLo, ref tHi)) { low = high = 0f; return false; }

        low = Extremum(below, nLo, tLo, tHi, low: true);
        high = Extremum(above, nHi, tLo, tHi, low: false);
        if (!(high > low)) { low = high = 0f; return false; }
        return true;
    }

    /// <summary>
    /// Narrows t's range to where the column is inside the sweep, in place. A point on the column is in the body at t
    /// when its z is over the lower bound and under the upper one, so every line from below has to sit under every
    /// line from above, and each of those pairs is one bound on t. The lines are affine, so what they leave of t is
    /// one interval, whose ends are where the body opens and closes on the column.
    /// </summary>
    /// <remarks>
    /// This is what keeps the two ends honest, and it is done as a bound on t rather than as a test per candidate on
    /// purpose. A candidate where the body opens or closes has the two bounds equal there, which in float they are not
    /// -- a couple of ulps apart, and which way is the compiler's business. Testing such a candidate for "is the
    /// column in the body" drops half of them at random, and the end that was to come from the one that got dropped
    /// is then taken from a different t, a whole slice of the sweep away. Narrowing t instead is a chain of min and
    /// max, so an ulop moves an end of the range by an ulop and nothing else.
    /// </remarks>
    private static bool Where(Span<float> below, int nLo, Span<float> above, int nHi, ref float tLo, ref float tHi)
    {
        for (int i = 0; i < nLo; i++)
        {
            float ai = below[2 * i], bi = below[2 * i + 1];
            for (int j = 0; j < nHi; j++)
            {
                // The pair reads ai + bi·t ≤ aj + bj·t, that is (ai − aj) + (bi − bj)·t ≤ 0.
                float k = ai - above[2 * j], s = bi - above[2 * j + 1];
                // Whether the bound moves at all can be asked without dividing: for s > 0, -k/s lies under tHi exactly
                // when k + s·tHi >= 0, and for s < 0 it lies over tLo under the same form. Mirrors convex_where, and
                // keeps the narrowing a chain of min and max, so an ulop moves an end of the range by an ulop. >= and
                // not >: at an exact tie the clamp is a no-op either way, and where the computed sum lands on zero
                // while the exact one has not, >= keeps it.
                if (s > 0f) { if (k + s * tHi >= 0f) tHi = MathF.Min(tHi, -k / s); }
                else if (s < 0f) { if (k + s * tLo >= 0f) tLo = MathF.Max(tLo, -k / s); }
                else if (k > 0f) return false;   // parallel, and the lower one sits above the upper one
            }
        }
        return tLo <= tHi;
    }

    /// <summary>
    /// The lower end of the sweep (min over t of the envelope of the lower bound) or the upper one (max over t of the
    /// envelope of the upper bound). Both walk the same candidates: the ends of t's range, then the place where two
    /// lines of the same bound cross.
    /// </summary>
    private static float Extremum(Span<float> g, int count, float tLo, float tHi, bool low)
    {
        if (count == 0) return low ? float.NegativeInfinity : float.PositiveInfinity;

        // Every candidate is scored on the envelope, the two ends included. A line's own value only bounds the
        // envelope's, so scoring the ends per line would report an end below the body the tool actually sweeps.
        float best = ExtremumAt(g, count, tLo, low);
        float end = ExtremumAt(g, count, tHi, low);
        best = low ? MathF.Min(best, end) : MathF.Max(best, end);

        for (int i = 0; i < count; i++)
        {
            float ai = g[2 * i], bi = g[2 * i + 1];
            for (int j = i + 1; j < count; j++)
            {
                float aj = g[2 * j], bj = g[2 * j + 1];
                if (bi == bj) continue;   // parallel or identical: there is no crossing to walk to
                float t = (aj - ai) / (bi - bj);
                if (t < tLo || t > tHi) continue;

                float v = ExtremumAt(g, count, t, low);
                best = low ? MathF.Min(best, v) : MathF.Max(best, v);
            }
        }
        return best;
    }

    /// <summary>
    /// The envelope of the bound's lines at <paramref name="t"/>: the highest of them for the lower bound, the
    /// lowest for the upper. At a crossing this is not the crossing pair's own value -- a line can cross another
    /// below the envelope, and taking the pair's value would put an end outside the body the tool actually sweeps.
    /// </summary>
    private static float ExtremumAt(Span<float> g, int count, float t, bool low)
    {
        float v = low ? float.NegativeInfinity : float.PositiveInfinity;
        for (int k = 0; k < count; k++)
        {
            float q = g[2 * k] + g[2 * k + 1] * t;
            v = low ? MathF.Max(v, q) : MathF.Min(v, q);
        }
        return v;
    }
}