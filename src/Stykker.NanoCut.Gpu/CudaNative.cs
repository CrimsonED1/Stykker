using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Stykker.NanoCut.Gpu;

/// <summary>
/// The C boundary of the native library <c>nanocut_gpu</c>, built from <c>src/Stykker.NanoCut.Gpu.Native/zmap.cu</c>
/// with nvcc. <see cref="LibraryImportAttribute"/> keeps the marshalling source generated, so this stays AOT and
/// trimming compatible. Every entry point returns an error code and never throws across the boundary;
/// <see cref="LastError"/> explains a failure.
/// </summary>
/// <remarks>
/// The library is optional: it is not part of the normal build, because neither CI nor a machine without the CUDA
/// toolkit can build it. <see cref="CudaRuntime"/> probes it once and reports the outcome instead of crashing.
/// </remarks>
internal static partial class CudaNative
{
    internal const string LibraryName = "nanocut_gpu";

    /// <summary>Loads the library and creates the CUDA context on <paramref name="device"/>. Called once.</summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_gpu_init")]
    internal static partial int Init(int device);

    /// <summary>Number of CUDA devices, negative on error.</summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_gpu_device_count")]
    internal static partial int DeviceCount();

    /// <summary>Fills the name and the properties of one device. Returns 0 on success.</summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_gpu_device_info")]
    internal static partial int DeviceInfo(int device, Span<byte> name, int nameLength,
        out int ccMajor, out int ccMinor, out ulong memoryBytes);

    /// <summary>Text of the last error, valid until the next call on the same thread.</summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_last_error")]
    internal static partial nint LastError();

    /// <summary>Allocates a height field of nx × ny cells on the device and fills it with <paramref name="top"/>.</summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_zmap_create")]
    internal static partial nint ZMapCreate(int nx, int ny, float cellX, float cellY, float bottom, float top);

    /// <summary>
    /// Lowers the device height field by <paramref name="stepCount"/> steps of
    /// <see cref="ToolProfile.StepFloats"/> floats each. Reports the kernel time and the host to device copy time.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_zmap_apply_steps")]
    internal static partial int ZMapApplySteps(nint zmap, ReadOnlySpan<float> steps, int stepCount,
        out double kernelMs, out double uploadMs);

    /// <summary>Copies the height field back to the host and reports the device to host copy time.</summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_zmap_read")]
    internal static partial int ZMapRead(nint zmap, Span<float> heights, out double downloadMs);

    /// <summary>
    /// Sums the removed depth over the device height field and returns it in mm³, reduced on the device so the
    /// height field itself does not have to come back.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_zmap_volume")]
    internal static partial int ZMapVolume(nint zmap, out double volumeMm3, out double kernelMs);

    /// <summary>
    /// Reads the height of the field at <paramref name="count"/> points, two doubles each in absolute mm, bilinear
    /// between cell centres, and reports where the time went. The points are converted to field coordinates on the
    /// device, so the caller does not have to pack them.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_zmap_sample")]
    internal static partial int ZMapSample(nint zmap, ReadOnlySpan<SamplePoint> points, int count, double originX,
        double originY, Span<float> outHeights, out double kernelMs, out double uploadMs, out double downloadMs);

    /// <summary>
    /// Copies <paramref name="count"/> points, two doubles each in absolute mm, into a point set that stays on the
    /// device until <see cref="PointSetDestroy"/>, and reports the copy time. Returns the set, or zero on failure.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_pointset_create")]
    internal static partial nint PointSetCreate(ReadOnlySpan<SamplePoint> points, int count, out double uploadMs);

    /// <summary>
    /// Reads the height of the field at every point of a set that is already on the device, the same kernel as
    /// <see cref="ZMapSample"/> with no upload. <paramref name="uploadMs"/> is the cost of the timing events.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_zmap_sample_set")]
    internal static partial int ZMapSampleSet(nint zmap, nint pointSet, double originX, double originY,
        Span<float> outHeights, out double kernelMs, out double uploadMs, out double downloadMs);

    /// <summary>Frees a point set. A zero handle is ignored.</summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_pointset_destroy")]
    internal static partial void PointSetDestroy(nint pointSet);

    /// <summary>
    /// Reads how far a ball reaches into the material at <paramref name="count"/> poses, four floats (x, y, z, r)
    /// each, and reports where the time went.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_zmap_probe")]
    internal static partial int ZMapProbe(nint zmap, ReadOnlySpan<float> poses, int count,
        Span<float> outPenetrationMm, out double kernelMs, out double uploadMs, out double downloadMs);

    /// <summary>Frees the height field.</summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_zmap_destroy")]
    internal static partial void ZMapDestroy(nint zmap);

    /// <summary>Allocates a dexel map of nx × ny columns with room for k intervals each, all set to [0, top].</summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_dexel_create")]
    internal static partial nint DexelCreate(int nx, int ny, float cellX, float cellY, float top, int k);

    /// <summary>Subtracts the swept balls of the packed steps from every column; reports kernel and upload time.</summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_dexel_apply_steps")]
    internal static partial int DexelApplySteps(nint dexel, ReadOnlySpan<float> steps, int stepCount,
        out double kernelMs, out double uploadMs);

    /// <summary>
    /// The same, but a tile of columns only sees the steps its part of the CSR assigns to it. <paramref name="tileStart"/>
    /// holds tileCount + 1 offsets into <paramref name="tileSteps"/>, which carries the step indices of each tile.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_dexel_apply_steps_binned")]
    internal static partial int DexelApplyStepsBinned(nint dexel, ReadOnlySpan<float> steps, int stepCount,
        ReadOnlySpan<int> tileStart, int tileCount, ReadOnlySpan<int> tileSteps, int tileStepCount,
        out double kernelMs, out double uploadMs);

    /// <summary>Copies intervals, counts and the overflow count to the host.</summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_dexel_read")]
    internal static partial int DexelRead(nint dexel, Span<float> intervals, Span<byte> counts, out long overflows,
        out double downloadMs);

    /// <summary>The removed volume in mm³, reduced on the device, and the overflow count.</summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_dexel_volume")]
    internal static partial int DexelVolume(nint dexel, out double volumeMm3, out long overflows, out double kernelMs);

    /// <summary>Frees the dexel map.</summary>
    [LibraryImport(LibraryName, EntryPoint = "nc_dexel_destroy")]
    internal static partial void DexelDestroy(nint dexel);

    /// <summary>Text of the last error as a managed string, or a placeholder when there is none.</summary>
    internal static string LastErrorMessage()
    {
        nint p = LastError();
        return p == 0 ? "unknown native error" : Marshal.PtrToStringUTF8(p) ?? "unknown native error";
    }

    /// <summary>Throws <see cref="GpuNativeException"/> unless <paramref name="code"/> is zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Check(int code, string what)
    {
        if (code != 0) throw new GpuNativeException(what, code, LastErrorMessage());
    }
}

/// <summary>A call into the native GPU library failed.</summary>
public sealed class GpuNativeException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="operation">What was attempted.</param>
    /// <param name="code">The error code the native side returned.</param>
    /// <param name="nativeMessage">The text of <c>nc_last_error</c>.</param>
    public GpuNativeException(string operation, int code, string nativeMessage)
        : base($"{operation} failed with code {code}: {nativeMessage}")
    {
        Operation = operation;
        Code = code;
        NativeMessage = nativeMessage;
    }

    /// <summary>What was attempted.</summary>
    public string Operation { get; }

    /// <summary>The error code the native side returned.</summary>
    public int Code { get; }

    /// <summary>The text of <c>nc_last_error</c>.</summary>
    public string NativeMessage { get; }
}
