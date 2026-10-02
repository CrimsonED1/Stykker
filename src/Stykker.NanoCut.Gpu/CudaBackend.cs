using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Stykker.NanoCut.Gpu;

/// <summary>
/// Runs the steps on a CUDA device through the native library <c>nanocut_gpu</c>, which is built from
/// <c>src/Stykker.NanoCut.Gpu.Native/zmap.cu</c> with nvcc. The library is optional, so check
/// <see cref="IsAvailable"/> (or <see cref="CudaRuntime.UnavailableReason"/>) before using it.
/// </summary>
/// <remarks>
/// The height field of a map lives on the device for as long as the map does, so applying steps in several batches
/// costs only the step upload and the height read-back per call, not a new allocation. The first call also creates
/// the CUDA context, which is reported separately as <see cref="ZMapTiming.FirstCallMs"/>.
/// </remarks>
public sealed class CudaBackend : IZMapBackend
{
    private readonly ConditionalWeakTable<ZMap, DeviceMap> _maps = new();
    private readonly object _gate = new();

    /// <summary>Creates a backend on the given CUDA device.</summary>
    /// <param name="deviceIndex">CUDA device ordinal, 0 by default.</param>
    public CudaBackend(int deviceIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(deviceIndex);
        DeviceIndex = deviceIndex;
    }

    /// <summary>CUDA device this backend uses.</summary>
    public int DeviceIndex { get; }

    /// <inheritdoc />
    public string Name => $"cuda:{DeviceIndex}";

    /// <inheritdoc />
    public bool IsAvailable => CudaRuntime.IsAvailable && DeviceIndex < CudaRuntime.Devices.Count;

    /// <inheritdoc />
    public string? UnavailableReason => !CudaRuntime.IsAvailable
        ? CudaRuntime.UnavailableReason
        : DeviceIndex >= CudaRuntime.Devices.Count
            ? $"device {DeviceIndex} does not exist, {CudaRuntime.Devices.Count} found"
            : null;

    /// <inheritdoc />
    public ZMapTiming Apply(ZMap map, ReadOnlySpan<BallStep> steps)
    {
        string? why = UnavailableReason;
        if (why is not null) throw new GpuNativeException("nc_zmap_apply_steps", -1, why);

        var wall = Stopwatch.StartNew();
        float[] packed = ToolProfile.Pack(steps, map.OriginMm);
        double firstCall = CudaRuntime.EnsureInitialised(DeviceIndex);

        DeviceMap device;
        lock (_gate) device = _maps.GetValue(map, m => DeviceMap.Create(m));
        firstCall += device.PendingCreateMs;
        device.PendingCreateMs = 0;

        CudaNative.Check(CudaNative.ZMapApplySteps(device.Handle, packed, steps.Length,
            out double kernelMs, out double uploadMs), "nc_zmap_apply_steps");
        CudaNative.Check(CudaNative.ZMapRead(device.Handle, map.Heights, out double downloadMs), "nc_zmap_read");
        wall.Stop();

        return new ZMapTiming(kernelMs, uploadMs, downloadMs, firstCall, wall.Elapsed.TotalMilliseconds);
    }

    /// <summary>Allocates and frees the device memory of the maps this backend has seen.</summary>
    private sealed class DeviceMap
    {
        internal nint Handle;
        internal double PendingCreateMs;

        private DeviceMap(nint handle, double createMs)
        {
            Handle = handle;
            PendingCreateMs = createMs;
        }

        internal static DeviceMap Create(ZMap map)
        {
            var sw = Stopwatch.StartNew();
            nint handle = CudaNative.ZMapCreate(map.CellsX, map.CellsY, (float)map.CellSizeXMm,
                (float)map.CellSizeYMm, map.BottomRelative, map.TopRelative);
            sw.Stop();
            if (handle == 0) throw new GpuNativeException("nc_zmap_create", -1, CudaNative.LastErrorMessage());
            return new DeviceMap(handle, sw.Elapsed.TotalMilliseconds);
        }

        ~DeviceMap()
        {
            nint h = Handle;
            if (h != 0) CudaNative.ZMapDestroy(h);
        }
    }
}
