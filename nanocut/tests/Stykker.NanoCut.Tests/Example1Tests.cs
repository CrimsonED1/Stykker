using Stykker.NanoCut.Testing;

namespace Stykker.NanoCut.Tests;

/// <summary>Example 1 (ball tool through block), 2D checks against the analytic solution.</summary>
public class Example1Tests
{
    public static TheoryData<string> CheckNames() => new(Example1.Run2D().Select(c => c.Name));

    [Theory]
    [MemberData(nameof(CheckNames))]
    public void WithinBudget(string name)
    {
        var check = Example1.Run2D().Single(c => c.Name == name);
        Assert.True(check.Passed, check.ToString());
    }

    [Fact]
    public void ExactValuesMatchPlan()
    {
        Assert.Equal(3.097482080, Example1.ExactSegmentArea, 9);
        Assert.Equal(61.949641602, Example1.ExactSegmentArea * 20, 8);
        Assert.Equal(4.472135955, Example1.ExactWidth, 9);
    }
}
