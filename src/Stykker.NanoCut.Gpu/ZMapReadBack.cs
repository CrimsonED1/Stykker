namespace Stykker.NanoCut.Gpu;

/// <summary>
/// Whether a backend copies the height field back to the host after it has lowered it. This matters because the
/// field is by far the largest thing that moves between the device and the host: for a 512 × 384 map it is 3 MB
/// per read-back, against 12 floats per step.
/// </summary>
/// <remarks>
/// A GPU backend keeps the field on the device for the life of the map, so a caller that only wants to know how much
/// material is gone can use <see cref="Never"/> for every batch and never pay for a read-back — see
/// <see cref="ZMap.BackendRemovedVolumeMm3"/>, which the CUDA backend reduces on the device. The CPU backend has no
/// second copy of the field, so there the choice has no effect.
/// </remarks>
public enum ZMapReadBack
{
    /// <summary>
    /// Copy the height field to <see cref="ZMap.Heights"/> after the batch. The default, because everything that
    /// reads the field on the host — <see cref="ZMap.RemovedVolumeMm3"/>, <see cref="ZMap.ToMesh"/>, a viewer —
    /// needs it.
    /// </summary>
    Always,

    /// <summary>
    /// Leave the height field on the backend and leave <see cref="ZMap.IsHeightsCurrent"/> false, so a caller that
    /// forgets to ask for it back gets an exception instead of a stale surface. <see cref="ZMap.ReadHeights"/>
    /// brings the field back when it is needed.
    /// </summary>
    Never,
}