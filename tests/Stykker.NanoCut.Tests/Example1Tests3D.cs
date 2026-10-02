using Stykker.NanoCut.Testing;

namespace Stykker.NanoCut.Tests;

/// <summary>Example 1 (ball tool through block), 3D checks against the analytic solution (phase 4 acceptance).</summary>
public class Example1Tests3D
{
    private static readonly Lazy<IReadOnlyList<ReferenceCheck>> Checks = new(() => Example1.Run3D());

    public static TheoryData<string> CheckNames() => new(
        "3D removed volume", "3D remaining volume", "3D max depth", "3D groove width at top", "3D groove vertex distance to axis (worst)");

    [Theory]
    [MemberData(nameof(CheckNames))]
    public void WithinBudget(string name)
    {
        var check = Checks.Value.Single(c => c.Name == name);
        Assert.True(check.Passed, check.ToString());
    }
}
