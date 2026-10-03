namespace Stykker.NanoCut.Gpu;

/// <summary>
/// A set of points the backend has taken over, so that asking about the same points again costs nothing to set up.
/// Where the points live is the backend's business: on the CUDA backend they sit in device memory from
/// <see cref="IZMapQueryBackend.UploadPoints"/> until this object is disposed, on the CPU backend in an array instead
/// of the span the caller passed in.
/// </summary>
/// <remarks>
/// This is for a caller that asks the same question again and again — a viewer sampling its pixel grid after every
/// batch of steps, or a stock check sampling one grid every time a program changes. For a single query it buys
/// nothing: the upload happens either way.
/// </remarks>
public sealed class PointSet : IDisposable
{
    private nint _handle;
    private SamplePoint[]? _points;

    private PointSet(int count, int deviceIndex)
    {
        Count = count;
        DeviceIndex = deviceIndex;
    }

    /// <summary>Number of points in the set, also the number of heights a query writes.</summary>
    public int Count { get; }

    /// <summary>CUDA device the set belongs to, or −1 when the backend keeps it on the host.</summary>
    internal int DeviceIndex { get; }

    /// <summary>The device handle, or an exception once the set has been disposed.</summary>
    internal nint Handle => _handle != 0
        ? _handle
        : throw new ObjectDisposedException(nameof(PointSet));

    /// <summary>The points as an array, or an exception once the set has been disposed.</summary>
    internal SamplePoint[] Points => _points ?? throw new ObjectDisposedException(nameof(PointSet));

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed => _handle == 0 && _points is null;

    /// <summary>A set in device memory, owned by the native library.</summary>
    internal static PointSet OnDevice(nint handle, int count, int deviceIndex) =>
        new(count, deviceIndex) { _handle = handle };

    /// <summary>A set in a managed array, for a backend that answers queries on the host.</summary>
    internal static PointSet OnHost(SamplePoint[] points) => new(points.Length, -1) { _points = points };

    /// <summary>Frees what the backend holds for this set. Disposing twice is not an error.</summary>
    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the set when the caller never disposed it.</summary>
    ~PointSet() => Release();

    private void Release()
    {
        nint handle = _handle;
        if (handle != 0)
        {
            _handle = 0;
            CudaNative.PointSetDestroy(handle);
        }
        _points = null;
    }
}