namespace Stykker.NanoCut.Gpu;

/// <summary>A CUDA device the native library can see.</summary>
/// <param name="Index">CUDA device ordinal.</param>
/// <param name="Name">Device name as reported by the driver.</param>
/// <param name="Major">Major compute capability, for example 12 for Blackwell.</param>
/// <param name="Minor">Minor compute capability.</param>
/// <param name="MemoryBytes">Total device memory.</param>
public sealed record GpuDevice(int Index, string Name, int Major, int Minor, ulong MemoryBytes)
{
    /// <summary>Compute capability as it is usually written, for example "12.0".</summary>
    public string ComputeCapability => $"{Major}.{Minor}";
}
