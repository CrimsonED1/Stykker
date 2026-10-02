namespace Stykker.NanoCut.Gpu;

/// <summary>
/// The tool profile both backends evaluate, so that CPU and CUDA compute the same surface from the same numbers.
/// A step is packed into <see cref="StepFloats"/> floats, in millimetres relative to the grid origin, and the
/// bottom of the swept ball is then found with float arithmetic only.
/// </summary>
internal static class ToolProfile
{
    /// <summary>Floats per packed step: (x0, y0, z0, r), (wx, wy, wz, r²), (w2, 1/w2, wz²+w2, wz²).</summary>
    internal const int StepFloats = 12;

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
            float w2 = wx * wx + wy * wy;
            float wz2 = wz * wz;
            buf[o] = x0; buf[o + 1] = y0; buf[o + 2] = z0; buf[o + 3] = r;
            buf[o + 4] = wx; buf[o + 5] = wy; buf[o + 6] = wz; buf[o + 7] = r * r;
            buf[o + 8] = w2; buf[o + 9] = w2 > 0f ? 1f / w2 : 0f; buf[o + 10] = wz2 + w2; buf[o + 11] = wz2;
        }
        return buf;
    }

    /// <summary>
    /// The lowest z of the ball swept from one step position to the other, at the column (<paramref name="x"/>,
    /// <paramref name="y"/>), in the same relative units as the packed step. Returns <see cref="float.PositiveInfinity"/>
    /// when the column is farther than the radius from the whole segment, that is when the step cannot reach it.
    /// </summary>
    /// <remarks>
    /// The bottom of the ball at parameter t along the segment is g(t) = z0 + wz·t − √S(t) with
    /// S(t) = r² − p² + 2·d·t − w2·t², where p² is the squared distance from the column to the segment start and
    /// d the dot product of that offset with the segment direction. g is continuous wherever S ≥ 0, so its minimum
    /// over the valid part of [0, 1] sits at an endpoint of that interval or at a stationary point; all of those are
    /// evaluated. Every candidate t with S(t) ≥ 0 describes a real ball position, so extra candidates can only be
    /// too high, never too low.
    /// </remarks>
    internal static float Bottom(float x, float y, ReadOnlySpan<float> steps, int s)
    {
        int o = s * StepFloats;
        float x0 = steps[o], y0 = steps[o + 1], z0 = steps[o + 2], r2 = steps[o + 7];
        float wx = steps[o + 4], wy = steps[o + 5], wz = steps[o + 6];
        float w2 = steps[o + 8], invW2 = steps[o + 9], k = steps[o + 10], wz2 = steps[o + 11];

        float px = x - x0, py = y - y0;
        float p2 = px * px + py * py;
        float d = px * wx + py * wy;

        // Cheap reject: the horizontal distance from the column to the segment is the smallest one over all t,
        // so when it exceeds the radius no ball position of this step touches the column.
        float tc = w2 > 0f ? Math.Clamp(d * invW2, 0f, 1f) : 0f;
        float ex = px - tc * wx, ey = py - tc * wy;
        if (ex * ex + ey * ey > r2) return float.PositiveInfinity;

        float best = float.PositiveInfinity;

        // A ball position at parameter t, when the column is inside it.
        void Consider(float t)
        {
            if (t < 0f || t > 1f) return;
            float sv = r2 - p2 + t * (2f * d - w2 * t);
            if (sv < 0f) return;
            float g = z0 + wz * t - MathF.Sqrt(sv);
            if (g < best) best = g;
        }

        // An endpoint of the valid interval, where the column is exactly on the ball equator (S = 0).
        void ConsiderTangent(float t)
        {
            if (t < 0f || t > 1f) return;
            float g = z0 + wz * t;
            if (g < best) best = g;
        }

        Consider(0f);
        Consider(1f);

        if (w2 > 0f)
        {
            // The reject above guarantees that S(t) >= 0 for some t in [0, 1], so both discriminants are >= 0 in
            // exact arithmetic. In float they can come out slightly negative through cancellation -- above all the
            // stationary one for a horizontal step, where it is exactly zero in theory but is computed as the
            // difference of two equal products of size 4·d²·w2². Clamping is safe: every candidate t with S(t) >= 0
            // is a real ball position, so an extra one can only be too high, never too low.
            float sq = MathF.Sqrt(MathF.Max(d * d - w2 * (p2 - r2), 0f));
            ConsiderTangent((d - sq) * invW2);
            ConsiderTangent((d + sq) * invW2);

            // Stationary points of g: wz·√S = d − w2·t, squared into a quadratic.
            float alpha = w2 * k;
            float beta = -2f * d * k;
            float sq2 = MathF.Sqrt(MathF.Max(beta * beta - 4f * alpha * (d * d - wz2 * (r2 - p2)), 0f));
            float inv = 0.5f / alpha;
            Consider((-beta - sq2) * inv);
            Consider((-beta + sq2) * inv);
        }

        return best;
    }
}
