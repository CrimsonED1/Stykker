namespace Stykker.NanoCut.Gpu;

/// <summary>Runs ball-tool steps on a <see cref="DexelMap"/>: the CPU reference or the CUDA library.</summary>
public interface IDexelBackend
{
    /// <summary>Short name for reports, for example "cpu-16" or "cuda:0".</summary>
    string Name { get; }

    /// <summary>Whether the backend can run on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>Why <see cref="IsAvailable"/> is false, or null.</summary>
    string? UnavailableReason { get; }

    /// <summary>True when <see cref="DexelMap.Intervals"/> is the state itself, so a read-back costs nothing.</summary>
    bool KeepsDexelsOnHost { get; }

    /// <summary>Subtracts the swept balls of all steps, in order, from every column.</summary>
    /// <param name="map">The map to change.</param>
    /// <param name="steps">The steps in absolute mm.</param>
    /// <param name="readBack">Whether the intervals are copied to the host afterwards.</param>
    /// <returns>Kernel, upload, download and wall time of the call.</returns>
    ZMapTiming ApplyDexels(DexelMap map, ReadOnlySpan<BallStep> steps, ZMapReadBack readBack);

    /// <summary>Copies the intervals, their counts and the overflow count to the host; returns the copy time in ms.</summary>
    /// <param name="map">The map to read.</param>
    double ReadDexels(DexelMap map);

    /// <summary>The removed volume in mm³ as the backend computes it, without a read-back where it can.</summary>
    /// <param name="map">The map to measure.</param>
    double RemovedVolumeMm3(DexelMap map);
}
