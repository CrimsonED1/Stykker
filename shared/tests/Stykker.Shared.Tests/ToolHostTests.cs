using Stykker.Shared.Web;

namespace Stykker.Shared.Tests;

public class ToolHostTests
{
    [Fact]
    public void ASecondClaimOnTheSamePortIsNotTheFirst()
    {
        var tool = "StykkerTest" + Guid.NewGuid().ToString("N")[..8];
        using var first = ToolHost.ClaimPort(tool, 8077, out bool firstClaim);
        using var second = ToolHost.ClaimPort(tool, 8077, out bool secondClaim);
        Assert.True(firstClaim);
        Assert.False(secondClaim);
    }
}
