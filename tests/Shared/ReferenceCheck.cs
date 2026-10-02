namespace Stykker.NanoCut.Testing;

/// <summary>One analytic reference check: measured value, exact value and allowed deviation.</summary>
public sealed record ReferenceCheck(string Name, double Actual, double Expected, double Allowed, string Unit)
{
    /// <summary>Absolute deviation from the exact value.</summary>
    public double Deviation => Math.Abs(Actual - Expected);

    /// <summary>True if the deviation is within the budget.</summary>
    public bool Passed => Deviation <= Allowed;

    /// <inheritdoc />
    public override string ToString() =>
        $"{(Passed ? "PASS" : "FAIL")} {Name}: {Actual:0.000000000} {Unit} (exact {Expected:0.000000000}, |Δ| {Deviation:0.###e+0} ≤ {Allowed:0.###e+0})";
}
