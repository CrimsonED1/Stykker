using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Stykker.NanoCut.Gpu;

/// <summary>
/// Runs the steps on a CUDA device through the native library <c>nanocut_gpu</c>, which is built from
/// <c>src/Stykker.NanoCut.Gpu.Native/zmap.cu</c> with nvcc. The library is optional, so check
/// <see cref="IsAvailable"/> (or <see cref="CudaRuntime.UnavailableReason"/>) before using it.
/// </summary>
/// <remarks>
/// The height field of a map lives on the device for as long as the map does, so applying steps in several batches
/// costs only the step upload and the height read-back per call, not a new allocation. The first call also creates
/// the CUDA context, which is reported separately as <see cref="ZMapTiming.FirstCallMs"/>. With
/// <see cref="ZMapReadBack.Never"/> not even the read-back happens: the field stays on the device, where
/// <see cref="RemovedVolumeMm3(ZMap)"/> reduces it and <see cref="IZMapQueryBackend"/> reads it.
/// </remarks>
public sealed class CudaBackend : IZMapBackend, IZMapQueryBackend, IDexelBackend
{
    private readonly ConditionalWeakTable<ZMap, DeviceMap> _maps = new();
    private readonly ConditionalWeakTable<DexelMap, DeviceDexel> _dexels = new();
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

    /// <summary>
    /// Whether the height read-back copies into pinned host memory. Pinned pages are not staged through a driver
    /// bounce buffer on the way out, so a 3 MB field arrives at the full link speed instead of the pageable rate; the
    /// price is that the page stays pinned for the duration of the copy, which blocks the collector from moving it.
    /// On by default; turn it off to measure what the staging costs.
    /// </summary>
    public bool PinnedReadBack { get; init; } = true;

    /// <summary>
    /// Whether the steps are binned into tiles of columns before a dexel launch (on by default). A binned launch gives
    /// each block only the steps that reach its columns, so the work follows what the tool touches instead of
    /// columns × steps; the price is the host-side binning, reported as <see cref="ZMapTiming.BinMs"/>. Turn it off to
    /// measure the unbinned launch, which is what the CPU backend and <see cref="Apply(ZMap, ReadOnlySpan{BallStep},
    /// ZMapReadBack)"/> always use.
    /// </summary>
    public bool BinSteps { get; init; } = true;

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
    public bool KeepsHeightsOnHost => false;

    /// <inheritdoc />
    public ZMapTiming Apply(ZMap map, ReadOnlySpan<BallStep> steps, ZMapReadBack readBack)
    {
        string? why = UnavailableReason;
        if (why is not null) throw new GpuNativeException("nc_zmap_apply_steps", -1, why);

        var wall = Stopwatch.StartNew();
        float[] packed = ToolProfile.Pack(steps, map.OriginMm);
        DeviceMap device = MapOf(map, out double firstCall);

        CudaNative.Check(CudaNative.ZMapApplySteps(device.Handle, packed, steps.Length,
            out double kernelMs, out double uploadMs), "nc_zmap_apply_steps");

        double downloadMs = readBack == ZMapReadBack.Always ? ReadDevice(device, map) : 0;
        wall.Stop();

        return new ZMapTiming(kernelMs, uploadMs, downloadMs, firstCall, wall.Elapsed.TotalMilliseconds);
    }

    /// <inheritdoc />
    public double ReadHeights(ZMap map)
    {
        string? why = UnavailableReason;
        if (why is not null) throw new GpuNativeException("nc_zmap_read", -1, why);
        return ReadDevice(MapOf(map, out _), map);
    }

    /// <inheritdoc />
    public double RemovedVolumeMm3(ZMap map)
    {
        string? why = UnavailableReason;
        if (why is not null) throw new GpuNativeException("nc_zmap_volume", -1, why);

        DeviceMap device = MapOf(map, out _);
        double volume = 0;
        CudaNative.Check(CudaNative.ZMapVolume(device.Handle, out volume, out _), "nc_zmap_volume");
        return volume;
    }

    /// <inheritdoc />
    public ZMapTiming SampleHeights(ZMap map, ReadOnlySpan<SamplePoint> points, Span<float> outHeights)
    {
        string? why = UnavailableReason;
        if (why is not null) throw new GpuNativeException("nc_zmap_sample", -1, why);

        DeviceMap device = MapOf(map, out double firstCall);
        var wall = Stopwatch.StartNew();
        // The points go to the device as they are: the kernel subtracts the origin and narrows them to float, which
        // is cheaper than a host-side packing loop (2.8 ms for a million points against 0.9 ms of extra transfer).
        // The lock is the same one the pose query takes, because both write their answer through queryOut and both
        // stage their input in queryIn: two queries on one map at the same time would otherwise overwrite each other.
        double kernelMs, uploadMs, downloadMs;
        lock (device.Gate)
        {
            CudaNative.Check(CudaNative.ZMapSample(device.Handle, points, points.Length, map.OriginMm.X,
                map.OriginMm.Y, outHeights, out kernelMs, out uploadMs, out downloadMs), "nc_zmap_sample");
        }
        wall.Stop();

        return new ZMapTiming(kernelMs, uploadMs, downloadMs, firstCall, wall.Elapsed.TotalMilliseconds);
    }

    /// <inheritdoc />
    public PointSet UploadPoints(ReadOnlySpan<SamplePoint> points)
    {
        string? why = UnavailableReason;
        if (why is not null) throw new GpuNativeException("nc_pointset_create", -1, why);

        nint handle = CudaNative.PointSetCreate(points, points.Length, out _);
        if (handle == 0) throw new GpuNativeException("nc_pointset_create", -1, CudaNative.LastErrorMessage());
        return PointSet.OnDevice(handle, points.Length, DeviceIndex);
    }

    /// <inheritdoc />
    public ZMapTiming SampleHeights(ZMap map, PointSet points, Span<float> outHeights)
    {
        string? why = UnavailableReason;
        if (why is not null) throw new GpuNativeException("nc_zmap_sample_set", -1, why);
        // Device memory belongs to the device it was allocated on, so a set from another one is a caller error, not
        // a slow query.
        if (points.DeviceIndex != DeviceIndex)
        {
            throw new ArgumentException(
                $"the point set is on device {points.DeviceIndex}, this backend is on device {DeviceIndex}",
                nameof(points));
        }
        if (outHeights.Length < points.Count)
        {
            throw new ArgumentException($"outHeights has {outHeights.Length} entries for {points.Count} points.",
                nameof(outHeights));
        }

        DeviceMap device = MapOf(map, out double firstCall);
        var wall = Stopwatch.StartNew();
        // No upload: the points have been on the device since UploadPoints. Only the answer comes back, so this is
        // the query a viewer can afford after every batch of steps.
        double kernelMs, uploadMs, downloadMs;
        lock (device.Gate)
        {
            CudaNative.Check(CudaNative.ZMapSampleSet(device.Handle, points.Handle, map.OriginMm.X, map.OriginMm.Y,
                outHeights, out kernelMs, out uploadMs, out downloadMs), "nc_zmap_sample_set");
        }
        wall.Stop();

        return new ZMapTiming(kernelMs, uploadMs, downloadMs, firstCall, wall.Elapsed.TotalMilliseconds);
    }

    /// <inheritdoc />
    public ZMapTiming ProbeMaterial(ZMap map, ReadOnlySpan<ToolPose> poses, Span<float> outPenetrationMm)
    {
        string? why = UnavailableReason;
        if (why is not null) throw new GpuNativeException("nc_zmap_probe", -1, why);

        DeviceMap device = MapOf(map, out double firstCall);
        var wall = Stopwatch.StartNew();
        double kernelMs, uploadMs, downloadMs;
        lock (device.Gate)
        {
            float[] packed = PackPoses(poses, map, device.PackedPoses);
            device.PackedPoses = packed;
            CudaNative.Check(CudaNative.ZMapProbe(device.Handle, packed, poses.Length, outPenetrationMm,
                out kernelMs, out uploadMs, out downloadMs), "nc_zmap_probe");
        }
        wall.Stop();

        return new ZMapTiming(kernelMs, uploadMs, downloadMs, firstCall, wall.Elapsed.TotalMilliseconds);
    }

    /// <summary>Packs the poses into the flat (x, y, z, r) float layout the kernel reads, relative to the origin.</summary>
    /// <remarks>
    /// Unlike the points, a pose carries four values and the poses of a program are few, so packing them is
    /// negligible. The buffer is still kept with the device map, because the largest query in a program can be
    /// millions of poses.
    /// </remarks>
    private static float[] PackPoses(ReadOnlySpan<ToolPose> poses, ZMap map, float[]? reuse)
    {
        int length = poses.Length * 4;
        var buf = Grow(reuse, length);
        float ox = (float)map.OriginMm.X, oy = (float)map.OriginMm.Y, oz = (float)map.OriginMm.Z;
        for (int i = 0; i < poses.Length; i++)
        {
            var p = poses[i];
            buf[4 * i] = (float)p.X - ox;
            buf[4 * i + 1] = (float)p.Y - oy;
            buf[4 * i + 2] = (float)p.Z - oz;
            buf[4 * i + 3] = (float)p.RadiusMm;
        }
        return buf;
    }

    /// <summary>The buffer itself when it is large enough, a larger one otherwise.</summary>
    private static float[] Grow(float[]? buf, int length) =>
        buf is { } b && b.Length >= length ? b : new float[length];

    /// <summary>The device copy of a map, created on first use, with the cost of that creation split out.</summary>
    private DeviceMap MapOf(ZMap map, out double firstCallMs)
    {
        firstCallMs = CudaRuntime.EnsureInitialised(DeviceIndex);
        DeviceMap device;
        lock (_gate) device = _maps.GetValue(map, m => DeviceMap.Create(m));
        firstCallMs += device.PendingCreateMs;
        device.PendingCreateMs = 0;
        return device;
    }

    /// <summary>Copies the device height field into <see cref="ZMap.Heights"/> and returns the copy time in ms.</summary>
    private double ReadDevice(DeviceMap device, ZMap map)
    {
        if (!PinnedReadBack)
        {
            CudaNative.Check(CudaNative.ZMapRead(device.Handle, map.Heights, out double downloadMs),
                "nc_zmap_read");
            return downloadMs;
        }

        // The handle is taken and released around the call, never kept: a pinned GCHandle held in the device map
        // would keep the whole ZMap alive for as long as the weak table holds the device map.
        var handle = GCHandle.Alloc(map.Heights, GCHandleType.Pinned);
        try
        {
            ref float first = ref MemoryMarshal.GetArrayDataReference(map.Heights);
            var pinned = MemoryMarshal.CreateSpan(ref first, map.Heights.Length);
            CudaNative.Check(CudaNative.ZMapRead(device.Handle, pinned, out double downloadMs), "nc_zmap_read");
            return downloadMs;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Allocates and frees the device memory of the maps this backend has seen.</summary>
    private sealed class DeviceMap
    {
        internal nint Handle;
        internal double PendingCreateMs;

        /// <summary>Guards the packed pose buffer, which the native library reads for the duration of a call.</summary>
        internal readonly object Gate = new();

        internal float[]? PackedPoses;

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

    /// <inheritdoc />
    public bool KeepsDexelsOnHost => false;

    /// <inheritdoc />
    public ZMapTiming ApplyDexels(DexelMap map, ReadOnlySpan<BallStep> steps, ZMapReadBack readBack)
    {
        string? why = UnavailableReason;
        if (why is not null) throw new GpuNativeException("nc_dexel_apply_steps", -1, why);

        var wall = Stopwatch.StartNew();
        var pack = Stopwatch.StartNew();
        float[] packed = ToolProfile.Pack(steps, map.OriginMm);
        pack.Stop();
        double packMs = pack.Elapsed.TotalMilliseconds;
        DeviceDexel device = DexelOf(map, out double firstCall);
        double kernelMs, uploadMs, binMs;
        if (BinSteps)
        {
            var bin = Stopwatch.StartNew();
            StepBins.Bins bins = StepBins.Build(packed, ToolProfile.StepFloats, convex: false, map.CellsX, map.CellsY,
                (float)map.CellSizeXMm, (float)map.CellSizeYMm);
            bin.Stop();
            binMs = bin.Elapsed.TotalMilliseconds;
            CudaNative.Check(CudaNative.DexelApplyStepsBinned(device.Handle, packed, steps.Length,
                bins.TileStart, bins.TileCount, bins.TileSteps, bins.References,
                out kernelMs, out uploadMs), "nc_dexel_apply_steps_binned");
        }
        else
        {
            binMs = 0;
            CudaNative.Check(CudaNative.DexelApplySteps(device.Handle, packed, steps.Length,
                out kernelMs, out uploadMs), "nc_dexel_apply_steps");
        }
        double downloadMs = readBack == ZMapReadBack.Always ? ReadDexelDevice(device, map) : 0;
        wall.Stop();
        return new ZMapTiming(kernelMs, uploadMs, downloadMs, firstCall, wall.Elapsed.TotalMilliseconds, binMs,
            packMs);
    }

    /// <inheritdoc />
    public ZMapTiming ApplyConvexDexels(DexelMap map, ConvexTool tool, ReadOnlySpan<ConvexStep> steps, ZMapReadBack readBack)
    {
        string? why = UnavailableReason;
        if (why is not null) throw new GpuNativeException("nc_dexel_apply_convex_steps", -1, why);

        var wall = Stopwatch.StartNew();
        float[] packed = ConvexProfile.Pack(steps, tool, map.OriginMm);
        float[] planes = ConvexProfile.PackPlanes(tool);
        DeviceDexel device = DexelOf(map, out double firstCall);
        double kernelMs, uploadMs, binMs;
        if (BinSteps)
        {
            var bin = Stopwatch.StartNew();
            StepBins.Bins bins = StepBins.Build(packed, ConvexProfile.StepFloats, convex: true, map.CellsX, map.CellsY,
                (float)map.CellSizeXMm, (float)map.CellSizeYMm);
            bin.Stop();
            binMs = bin.Elapsed.TotalMilliseconds;
            CudaNative.Check(CudaNative.DexelApplyConvexStepsBinned(device.Handle, packed, steps.Length, planes,
                planes.Length / ConvexProfile.PlaneFloats, bins.TileStart, bins.TileCount, bins.TileSteps,
                bins.References, out kernelMs, out uploadMs), "nc_dexel_apply_convex_steps_binned");
        }
        else
        {
            binMs = 0;
            CudaNative.Check(CudaNative.DexelApplyConvexSteps(device.Handle, packed, steps.Length, planes,
                planes.Length / ConvexProfile.PlaneFloats, out kernelMs, out uploadMs), "nc_dexel_apply_convex_steps");
        }
        double downloadMs = readBack == ZMapReadBack.Always ? ReadDexelDevice(device, map) : 0;
        wall.Stop();
        return new ZMapTiming(kernelMs, uploadMs, downloadMs, firstCall, wall.Elapsed.TotalMilliseconds, binMs);
    }

    /// <inheritdoc />
    public double ReadDexels(DexelMap map)
    {
        string? why = UnavailableReason;
        if (why is not null) throw new GpuNativeException("nc_dexel_read", -1, why);
        return ReadDexelDevice(DexelOf(map, out _), map);
    }

    /// <inheritdoc />
    public double RemovedVolumeMm3(DexelMap map)
    {
        string? why = UnavailableReason;
        if (why is not null) throw new GpuNativeException("nc_dexel_volume", -1, why);
        DeviceDexel device = DexelOf(map, out _);
        CudaNative.Check(CudaNative.DexelVolume(device.Handle, out double volume, out long overflows, out _),
            "nc_dexel_volume");
        map.Overflows = overflows;
        return volume;
    }

    private DeviceDexel DexelOf(DexelMap map, out double firstCallMs)
    {
        firstCallMs = CudaRuntime.EnsureInitialised(DeviceIndex);
        DeviceDexel device;
        lock (_gate) device = _dexels.GetValue(map, m => DeviceDexel.Create(m));
        firstCallMs += device.PendingCreateMs;
        device.PendingCreateMs = 0;
        return device;
    }

    private static double ReadDexelDevice(DeviceDexel device, DexelMap map)
    {
        CudaNative.Check(CudaNative.DexelRead(device.Handle, map.Intervals, map.Counts, out long overflows,
            out double downloadMs), "nc_dexel_read");
        map.Overflows = overflows;
        return downloadMs;
    }

    /// <summary>A dexel map on the device, freed when its <see cref="DexelMap"/> is collected.</summary>
    private sealed class DeviceDexel
    {
        internal nint Handle;
        internal double PendingCreateMs;

        private DeviceDexel(nint handle, double createMs)
        {
            Handle = handle;
            PendingCreateMs = createMs;
        }

        internal static DeviceDexel Create(DexelMap map)
        {
            var sw = Stopwatch.StartNew();
            nint handle = CudaNative.DexelCreate(map.CellsX, map.CellsY, (float)map.CellSizeXMm,
                (float)map.CellSizeYMm, map.TopRelative, map.MaxIntervals);
            sw.Stop();
            if (handle == 0) throw new GpuNativeException("nc_dexel_create", -1, CudaNative.LastErrorMessage());
            return new DeviceDexel(handle, sw.Elapsed.TotalMilliseconds);
        }

        ~DeviceDexel()
        {
            nint h = Handle;
            if (h != 0) CudaNative.DexelDestroy(h);
        }
    }
}
