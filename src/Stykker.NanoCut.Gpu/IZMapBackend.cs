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
    /// Whether <see cref="ZMap.Heights"/> holds the current field no matter what <see cref="ZMapReadBack"/> asks
    /// for. True for a backend whose only copy of the field is that array, so it has nothing to read back and
    /// ignoring <see cref="ZMapReadBack.Never"/> is not a lie; false for one that can leave the field on a device.
    /// </summary>
    bool KeepsHeightsOnHost { get; }

    /// <summary>
    /// Lowers <see cref="ZMap.Heights"/> by every step, in order, and reports where the time went. Implementations
    /// clamp each height to the stock bottom and never raise a height.
    /// </summary>
    /// <param name="map">The height field, in relative mm.</param>
    /// <param name="steps">The steps, in absolute mm.</param>
    /// <param name="readBack">
    /// Whether the height field is copied to <see cref="ZMap.Heights"/>. With <see cref="ZMapReadBack.Never"/> the
    /// implementation keeps the field to itself and <see cref="ZMap.IsHeightsCurrent"/> stays false until
    /// <see cref="ReadHeights"/> is called.
    /// </param>
    ZMapTiming Apply(ZMap map, ReadOnlySpan<BallStep> steps, ZMapReadBack readBack);

    /// <summary>
    /// Copies the height field back to <see cref="ZMap.Heights"/> and reports how long the copy took, in
    /// milliseconds. Does nothing on a backend whose only copy of the field is that array.
    /// </summary>
    /// <param name="map">The height field, in relative mm.</param>
    double ReadHeights(ZMap map);

    /// <summary>
    /// The material the steps took out of the stock in mm³, as the backend itself computes it: on the device while
    /// the field lives there, on the host otherwise. Unlike <see cref="ZMap.RemovedVolumeMm3"/> it needs no read-back,
    /// so it stays available while the field stays on the backend.
    /// </summary>
    /// <param name="map">The height field.</param>
    double RemovedVolumeMm3(ZMap map);
}
