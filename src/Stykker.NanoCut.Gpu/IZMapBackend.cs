namespace Stykker.NanoCut.Gpu;

/// <summary>
/// What runs the steps of a <see cref="ZMap"/>. Two implementations ship with the library: <see cref="CpuBackend"/>
/// (always available, the reference) and <see cref="CudaBackend"/> (needs the native library <c>nanocut_gpu</c> and
/// a CUDA device).
/// </summary>
public interface IZMapBackend
{
    /// <summary>Name of the backend, for logs and measurement tables.</summary>
    string Name { get; }

    /// <summary>Whether this backend can run at all on the current machine.</summary>
    bool IsAvailable { get; }

    /// <summary>Why <see cref="IsAvailable"/> is false, or null when it is available.</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Lowers <see cref="ZMap.Heights"/> by every step, in order, and reports where the time went. Implementations
    /// clamp each height to the stock bottom and never raise a height.
    /// </summary>
    /// <param name="map">The height field, in relative mm.</param>
    /// <param name="steps">The steps, in absolute mm.</param>
    ZMapTiming Apply(ZMap map, ReadOnlySpan<BallStep> steps);
}
