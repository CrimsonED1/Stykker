namespace Stykker.NanoCut.Gpu;

/// <summary>
/// A point in the plane of the workpiece to ask the height field about, in absolute millimetres. The CUDA backend
/// hands the array to the kernel as it is, which reads it as two doubles; adding a field would change the layout
/// the kernel assumes.
/// </summary>
/// <param name="X">Absolute x in mm.</param>
/// <param name="Y">Absolute y in mm.</param>
public readonly record struct SamplePoint(double X, double Y);