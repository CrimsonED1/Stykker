namespace Stykker.NanoCut.Gpu;

/// <summary>
/// Where the time of one <see cref="IZMapBackend.Apply"/> call went, in milliseconds. The parts are measured
/// separately because the first call of a GPU backend creates the CUDA context and loads the module (hundreds of
/// milliseconds), which has nothing to do with the cost per step.
/// </summary>
/// <param name="KernelMs">Compute time: the device kernel, or the parallel host loop of the CPU backend.</param>
/// <param name="UploadMs">Host to device copies (steps and heights). Zero for the CPU backend.</param>
/// <param name="DownloadMs">Device to host copies (the heights read back). Zero for the CPU backend.</param>
/// <param name="FirstCallMs">One-off initialisation: CUDA context creation and module load. Zero afterwards.</param>
/// <param name="WallMs">Wall clock time of the whole call, as seen by the caller.</param>
/// <param name="BinMs">Host-side cost of binning the steps into tiles before the launch
/// (<see cref="CudaBackend.BinSteps"/>). Zero for the CPU backend and for the unbinned launch. It is part of
/// <paramref name="WallMs"/>, reported apart so the saving of a binned launch is not credited with work it did not
/// save.</param>
public readonly record struct ZMapTiming(
    double KernelMs,
    double UploadMs,
    double DownloadMs,
    double FirstCallMs,
    double WallMs,
    double BinMs = 0)
{
    /// <summary>The sum of two timings, for accumulating over several calls.</summary>
    public static ZMapTiming operator +(ZMapTiming a, ZMapTiming b) =>
        new(a.KernelMs + b.KernelMs, a.UploadMs + b.UploadMs, a.DownloadMs + b.DownloadMs,
            a.FirstCallMs + b.FirstCallMs, a.WallMs + b.WallMs, a.BinMs + b.BinMs);

    /// <summary>Every part zero.</summary>
    public static ZMapTiming Zero { get; }
}
