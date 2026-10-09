using Stykker.NanoCut.Gpu;

namespace Stykker.NanoCut.Tests;

/// <summary>
/// The same packed convex steps with every swept body's bounding box widened past the whole map, which switches off
/// the per-column early-out in <see cref="ConvexProfile.Span"/> and leaves the linear program it guards untouched.
/// </summary>
/// <remarks>
/// Two tests want this, and for the same reason. <see cref="ConvexProfile.Span"/> now answers "does this step reach
/// this column" partly from the box, so any test that uses it as the oracle for a question *about* the box has stopped
/// checking anything: it would be comparing the box against itself. Run against this copy instead, the answer comes
/// from the half-spaces, which is the independent side of the comparison.
/// </remarks>
internal static class BluntedSteps
{
    /// <summary>A copy of <paramref name="packed"/> whose bounding boxes contain every column of any sane map.</summary>
    /// <remarks>
    /// The four floats per step are the box and nothing else, so overwriting them reaches no other part of the step.
    /// The extremes are chosen so the arithmetic stays exact: at 1e9 one ulp is 64 mm, so the margin the kernel adds
    /// and subtracts vanishes rather than moving the edge.
    /// </remarks>
    /// <param name="packed">The steps, as <see cref="ConvexProfile.Pack"/> writes them.</param>
    internal static float[] WithoutBoundingBoxes(float[] packed)
    {
        float[] blunt = (float[])packed.Clone();
        for (int o = 0; o + ConvexProfile.StepFloats <= blunt.Length; o += ConvexProfile.StepFloats)
        {
            blunt[o + ConvexProfile.BoxOffset] = -1e9f;
            blunt[o + ConvexProfile.BoxOffset + 1] = -1e9f;
            blunt[o + ConvexProfile.BoxOffset + 2] = 2e9f;
            blunt[o + ConvexProfile.BoxOffset + 3] = 2e9f;
        }
        return blunt;
    }
}