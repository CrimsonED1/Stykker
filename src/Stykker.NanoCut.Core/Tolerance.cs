namespace Stykker.NanoCut;

/// <summary>
/// Error budget of an operation. The total allowed deviation from the exact geometry is split
/// into independently checkable parts (see the accuracy concept in docs/accuracy.md).
/// All values are in nanometres.
/// </summary>
public sealed record Tolerance
{
    /// <summary>Error caused by the number representation: rounding to the 1 nm grid (±0.5 nm per axis).</summary>
    public const double NumericNm = 0.5;

    private Tolerance(double totalNm, double chordNm, double sweepNm)
    {
        if (!(totalNm > 0)) throw new ArgumentOutOfRangeException(nameof(totalNm), "Total budget must be positive.");
        if (!(chordNm > 0)) throw new ArgumentOutOfRangeException(nameof(chordNm), "Chord error must be positive.");
        if (!(sweepNm > 0)) throw new ArgumentOutOfRangeException(nameof(sweepNm), "Sweep error must be positive.");
        double reserve = totalNm - NumericNm - chordNm - sweepNm;
        if (reserve < 0)
            throw new ArgumentException(
                $"Budget overrun: numeric {NumericNm} + chord {chordNm} + sweep {sweepNm} nm exceeds total {totalNm} nm.");
        TotalNm = totalNm;
        ChordNm = chordNm;
        SweepNm = sweepNm;
        ReserveNm = reserve;
    }

    /// <summary>Total allowed deviation in nm.</summary>
    public double TotalNm { get; }

    /// <summary>Maximum chord error when arcs and curved surfaces are discretised, in nm.</summary>
    public double ChordNm { get; }

    /// <summary>Maximum hull error of path discretisation (sweep step), in nm.</summary>
    public double SweepNm { get; }

    /// <summary>Remaining reserve (import rounding, special cases), in nm.</summary>
    public double ReserveNm { get; }

    /// <summary>Total allowed deviation in mm.</summary>
    public double TotalMm => TotalNm / Units.NmPerMm;

    /// <summary>
    /// Creates a budget. Defaults follow the plan: 0.1 µm total, 50 nm chord error, 30 nm sweep error;
    /// the rest (after 0.5 nm numeric error) is reserve.
    /// </summary>
    public static Tolerance Budget(double totalUm = 0.1, double chordNm = 50, double sweepNm = 30) =>
        new(totalUm * 1000.0, chordNm, sweepNm);

    /// <summary>The default budget: 0.1 µm total, 50 nm chord, 30 nm sweep.</summary>
    public static Tolerance Default { get; } = Budget();
}
