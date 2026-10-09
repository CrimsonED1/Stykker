namespace Stykker.NanoCut;

/// <summary>
/// Unit conversion between the public millimetre API and the internal 1 nm integer grid.
/// </summary>
public static class Units
{
    /// <summary>Grid units (nanometres) per millimetre.</summary>
    public const long NmPerMm = 1_000_000;

    /// <summary>
    /// Largest absolute coordinate in nm (2^31 nm ≈ 2.147 m). Keeping all coordinates in
    /// [-2^31, 2^31] bounds every coordinate difference by 2^32, which is what the fixed-width
    /// predicates rely on (see docs/bit-budget.md).
    /// </summary>
    public const long MaxCoordinate = 1L << 31;

    /// <summary>Converts millimetres to the nearest grid point (round half away from zero).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite or outside ±<see cref="MaxCoordinate"/>.</exception>
    public static long MmToNm(double mm)
    {
        if (!double.IsFinite(mm))
            throw new ArgumentOutOfRangeException(nameof(mm), mm, "Coordinate must be finite.");
        double nm = Math.Round(mm * NmPerMm, MidpointRounding.AwayFromZero);
        if (Math.Abs(nm) > MaxCoordinate)
            throw new ArgumentOutOfRangeException(nameof(mm), mm, "Coordinate exceeds ±2^31 nm (±2147.483648 mm).");
        return (long)nm;
    }

    /// <summary>Converts a grid value in nm to millimetres.</summary>
    public static double NmToMm(long nm) => nm / (double)NmPerMm;

    /// <summary>Converts a length in nm (not necessarily integral) to millimetres.</summary>
    public static double NmToMm(double nm) => nm / NmPerMm;

    /// <summary>Throws if <paramref name="nm"/> lies outside the supported coordinate range.</summary>
    public static long CheckCoordinate(long nm, string paramName)
    {
        if (nm > MaxCoordinate || nm < -MaxCoordinate)
            throw new ArgumentOutOfRangeException(paramName, nm, "Coordinate exceeds ±2^31 nm.");
        return nm;
    }
}
