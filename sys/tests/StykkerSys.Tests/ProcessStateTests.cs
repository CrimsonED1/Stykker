using StykkerSys.Core;

namespace StykkerSys.Tests;

public class ProcessStateTests
{
    [Theory]
    [InlineData(0, "idle")]
    [InlineData(1.9, "idle")]
    [InlineData(2, "read")]
    [InlineData(24.9, "read")]
    [InlineData(25, "gen")]
    [InlineData(100, "gen")]
    public void ThePercentageDecidesTheState(double cpu, string expected) => Assert.Equal(expected, ProcessState.Of(cpu));
}
