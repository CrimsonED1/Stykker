using System.Diagnostics;

namespace Stykker.NanoCut.Gpu;

/// <summary>
/// What this machine offers the CUDA backend: whether the native library <c>nanocut_gpu</c> is there, which devices
/// it sees, and how long the one-off initialisation took. Everything is probed once and cached; a missing library or
/// a missing device is reported as a reason, never as a crash, because the native build is optional.
/// </summary>
public static class CudaRuntime
{
    private static readonly object Gate = new();
    private static bool _probed;
    private static string? _reason;
    private static GpuDevice[] _devices = [];
    private static double _initMs;
    private static int _initialised = -1;

    /// <summary>Whether a native library and at least one CUDA device were found.</summary>
    public static bool IsAvailable
    {
        get { Probe(); return _reason is null; }
    }

    /// <summary>Why <see cref="IsAvailable"/> is false, or null when CUDA can be used.</summary>
    public static string? UnavailableReason
    {
        get { Probe(); return _reason; }
    }

    /// <summary>The CUDA devices the native library reports, empty when it is unavailable.</summary>
    public static IReadOnlyList<GpuDevice> Devices
    {
        get { Probe(); return _devices; }
    }

    /// <summary>
    /// Milliseconds the one-off initialisation took (loading the library, creating the context on the device), or 0
    /// before <see cref="EnsureInitialised"/> ran. Reported as the first-call cost, not as time per step.
    /// </summary>
    public static double InitMs
    {
        get { Probe(); return _initMs; }
    }

    /// <summary>
    /// Creates the CUDA context on <paramref name="device"/> unless that already happened. Returns the milliseconds
    /// this call took, which is zero for every call after the first one.
    /// </summary>
    /// <param name="device">CUDA device ordinal.</param>
    /// <exception cref="GpuNativeException">The library or the device is missing, or the driver refused.</exception>
    public static double EnsureInitialised(int device)
    {
        if (!IsAvailable) throw new GpuNativeException("nc_gpu_init", -1, _reason ?? "CUDA is not available");
        if (_initialised == device) return 0;
        lock (Gate)
        {
            if (_initialised == device) return 0;
            var sw = Stopwatch.StartNew();
            CudaNative.Check(CudaNative.Init(device), "nc_gpu_init");
            sw.Stop();
            _initMs = sw.Elapsed.TotalMilliseconds;
            _initialised = device;
            return _initMs;
        }
    }

    private static void Probe()
    {
        if (_probed) return;
        lock (Gate)
        {
            if (_probed) return;
            try
            {
                int count = CudaNative.DeviceCount();
                if (count < 0)
                {
                    _reason = $"nc_gpu_device_count failed: {CudaNative.LastErrorMessage()}";
                }
                else if (count == 0)
                {
                    _reason = "the native library loaded, but no CUDA device was found";
                }
                else
                {
                    var found = new List<GpuDevice>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var name = new byte[256];
                        CudaNative.Check(CudaNative.DeviceInfo(i, name, name.Length, out int major, out int minor, out ulong mem),
                            "nc_gpu_device_info");
                        int len = Array.IndexOf<byte>(name, 0);
                        found.Add(new GpuDevice(i, System.Text.Encoding.UTF8.GetString(name, 0, len < 0 ? name.Length : len),
                            major, minor, mem));
                    }
                    _devices = [.. found];
                }
            }
            catch (DllNotFoundException e)
            {
                _reason = $"the native library {CudaNative.LibraryName} was not found; build it with " +
                          $"src/Stykker.NanoCut.Gpu.Native/build.ps1 (or build.sh). {e.Message}";
            }
            catch (EntryPointNotFoundException e)
            {
                _reason = $"the native library {CudaNative.LibraryName} is missing an entry point: {e.Message}";
            }
            catch (BadImageFormatException e)
            {
                _reason = $"the native library {CudaNative.LibraryName} has the wrong architecture: {e.Message}";
            }
            catch (GpuNativeException e)
            {
                _reason = e.Message;
            }
            _probed = true;
        }
    }
}
