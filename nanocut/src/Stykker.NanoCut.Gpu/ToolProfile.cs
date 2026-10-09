namespace Stykker.NanoCut.Gpu;

/// <summary>
/// The tool profile both backends evaluate, so that CPU and CUDA compute the same surface from the same numbers.
/// A step is packed into <see cref="StepFloats"/> floats, in millimetres relative to the grid origin, and the
/// bottom of the swept ball is then found with float arithmetic only.
/// </summary>
internal static class ToolProfile
{
    /// <summary>Floats per packed step: (x0, y0, z0, r), (wx, wy, wz, r²), (w2, 1/w2, c, zLow).</summary>
    /// <remarks>
    /// w2 = wx² + wy² is the squared horizontal length, c = wz / √(w2·(w2 + wz²)) fixes the stationary point of the
    /// bottom curve (see <see cref="Bottom"/>), zLow = min(z0, z0 + wz) is the lower end of a vertical step. A step
    /// whose horizontal motion is below 10⁻⁹ mm is packed as vertical (w2 = 0).
    /// </remarks>
    internal const int StepFloats = 12;

    /// <summary>Squared horizontal length below which a step counts as vertical: 10⁻⁹ mm of motion.</summary>
    private const double MinHorizontalSquared = 1e-18;

    /// <summary>Packs the steps into the flat layout the CPU loop and the CUDA kernel both read.</summary>
    internal static float[] Pack(ReadOnlySpan<BallStep> steps, (double X, double Y, double Z) origin)
    {
        var buf = new float[steps.Length * StepFloats];
        for (int s = 0; s < steps.Length; s++)
        {
            var st = steps[s];
            int o = s * StepFloats;
            float x0 = (float)(st.From.X - origin.X), y0 = (float)(st.From.Y - origin.Y), z0 = (float)(st.From.Z - origin.Z);
            float wx = (float)(st.To.X - origin.X) - x0;
            float wy = (float)(st.To.Y - origin.Y) - y0;
            float wz = (float)(st.To.Z - origin.Z) - z0;
            float r = (float)st.RadiusMm;
            double w2 = (double)wx * wx + (double)wy * wy;
            if (w2 < MinHorizontalSquared)
            {
                wx = 0f;
                wy = 0f;
                w2 = 0;
            }
            double c = w2 > 0 ? wz / Math.Sqrt(w2 * (w2 + (double)wz * wz)) : 0;
            buf[o] = x0; buf[o + 1] = y0; buf[o + 2] = z0; buf[o + 3] = r;
            buf[o + 4] = wx; buf[o + 5] = wy; buf[o + 6] = wz; buf[o + 7] = r * r;
            buf[o + 8] = (float)w2; buf[o + 9] = w2 > 0 ? (float)(1 / w2) : 0f; buf[o + 10] = (float)c;
            buf[o + 11] = MathF.Min(z0, z0 + wz);
        }
        return buf;
    }

    /// <summary>
    /// The lowest z of the ball swept from one step position to the other, at the column (<paramref name="x"/>,
    /// <paramref name="y"/>), in the same relative units as the packed step. Returns <see cref="float.PositiveInfinity"/>
    /// when the column is farther than the radius from the whole segment, that is when the step cannot reach it.
    /// </summary>
    /// <remarks>
    /// Everything is measured from the point t* of the (infinite) step line that is horizontally closest to the
    /// column, at horizontal distance e. With a² = r² − e² the ball at t reaches the column when
    /// w2·(t − t*)² ≤ a², and its bottom there is g(t) = z0 + wz·t − √(a² − w2·(t − t*)²). g is convex and its
    /// stationary point is t* − c·a with c from <see cref="Pack"/>, so the minimum over the valid interval
    /// [max(0, t* − a/√w2), min(1, t* + a/√w2)] is that point clamped to the interval.
    /// <para>
    /// An earlier form expanded S(t) = r² − p² + 2·d·t − w2·t² around the start of the step and solved two
    /// quadratics in t. On a long step p², d·t and w2·t² are all of order L² while S is of order r², so in float the
    /// cancellation left little of S: a 100 mm step with r = 1 mm was off by 0.02 mm, a 50 mm ramp with r = 0.1 mm
    /// missed the cut by millimetres, and the unchecked tangent candidates could cut below the ball. Here a² and
    /// t − t* are formed directly and never as a difference of large terms.
    /// </para>
    /// </remarks>
    internal static float Bottom(float x, float y, ReadOnlySpan<float> steps, int s)
    {
        int o = s * StepFloats;
        float x0 = steps[o], y0 = steps[o + 1], z0 = steps[o + 2], r2 = steps[o + 7];
        float wx = steps[o + 4], wy = steps[o + 5], wz = steps[o + 6];
        float w2 = steps[o + 8], invW2 = steps[o + 9], c = steps[o + 10], zLow = steps[o + 11];

        float px = x - x0, py = y - y0;
        if (w2 == 0f)
        {
            // Vertical step (or none): every ball position has the column at the same distance.
            float p2 = px * px + py * py;
            return p2 > r2 ? float.PositiveInfinity : zLow - MathF.Sqrt(r2 - p2);
        }

        float ts = (px * wx + py * wy) * invW2;
        float ex = px - ts * wx, ey = py - ts * wy;
        float a2 = r2 - (ex * ex + ey * ey);
        if (a2 < 0f) return float.PositiveInfinity;

        float a = MathF.Sqrt(a2);
        float h = a * MathF.Sqrt(invW2);
        float lo = MathF.Max(0f, ts - h), hi = MathF.Min(1f, ts + h);
        if (lo > hi) return float.PositiveInfinity;

        float t = Math.Clamp(ts - c * a, lo, hi);
        float du = t - ts;
        return z0 + wz * t - MathF.Sqrt(MathF.Max(a2 - w2 * du * du, 0f));
    }

    /// <summary>
    /// The interval [<paramref name="low"/>, <paramref name="high"/>] in which the column (<paramref name="x"/>,
    /// <paramref name="y"/>) meets the ball swept by step <paramref name="s"/>, or false when it does not. The swept ball
    /// is convex, so a vertical line meets it in one interval: <paramref name="low"/> is <see cref="Bottom"/>, and
    /// <paramref name="high"/> is its mirror image, the maximum of the concave top curve
    /// z0 + wz·t + √(a² − w2·(t − t*)²), at the stationary point t* + c·a clamped to the same valid interval.
    /// </summary>
    internal static bool Span(float x, float y, ReadOnlySpan<float> steps, int s, out float low, out float high)
    {
        int o = s * StepFloats;
        float x0 = steps[o], y0 = steps[o + 1], z0 = steps[o + 2], r2 = steps[o + 7];
        float wx = steps[o + 4], wy = steps[o + 5], wz = steps[o + 6];
        float w2 = steps[o + 8], invW2 = steps[o + 9], c = steps[o + 10], zLow = steps[o + 11];
        low = high = 0f;

        float px = x - x0, py = y - y0;
        if (w2 == 0f)
        {
            float p2 = px * px + py * py;
            if (p2 > r2) return false;
            float half = MathF.Sqrt(r2 - p2);
            low = zLow - half;
            high = zLow + MathF.Abs(wz) + half;
            return true;
        }

        float ts = (px * wx + py * wy) * invW2;
        float ex = px - ts * wx, ey = py - ts * wy;
        float a2 = r2 - (ex * ex + ey * ey);
        if (a2 < 0f) return false;

        float a = MathF.Sqrt(a2);
        float h = a * MathF.Sqrt(invW2);
        float lo = MathF.Max(0f, ts - h), hi = MathF.Min(1f, ts + h);
        if (lo > hi) return false;

        float tb = Math.Clamp(ts - c * a, lo, hi), db = tb - ts;
        low = z0 + wz * tb - MathF.Sqrt(MathF.Max(a2 - w2 * db * db, 0f));
        float tt = Math.Clamp(ts + c * a, lo, hi), dt = tt - ts;
        high = z0 + wz * tt + MathF.Sqrt(MathF.Max(a2 - w2 * dt * dt, 0f));
        return true;
    }
}
