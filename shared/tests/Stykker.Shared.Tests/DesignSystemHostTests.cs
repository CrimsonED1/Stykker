using Stykker.Shared.Web;

namespace Stykker.Shared.Tests;

public class DesignSystemHostTests
{
    [Fact]
    public void TheFlagWinsOverTheEnvironmentAndTheDefault()
    {
        var variable = NewVariable();
        Environment.SetEnvironmentVariable(variable, @"D:\from-env");
        try
        {
            Assert.Equal(@"D:\from-flag", DesignSystemHost.Resolve(new[] { "--design-system", @"D:\from-flag" }, variable));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public void TheEnvironmentIsUsedWithoutAFlag()
    {
        var variable = NewVariable();
        Environment.SetEnvironmentVariable(variable, @"D:\from-env");
        try { Assert.Equal(@"D:\from-env", DesignSystemHost.Resolve(Array.Empty<string>(), variable)); }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public void TheMonorepoFolderIsTheDefault() =>
        Assert.Equal(DesignSystemHost.DefaultFolder, DesignSystemHost.Resolve(Array.Empty<string>(), NewVariable()));

    [Fact]
    public void AFlagWithoutAValueFallsBack() =>
        Assert.Equal(DesignSystemHost.DefaultFolder, DesignSystemHost.Resolve(new[] { "--design-system" }, NewVariable()));

    private static string NewVariable() => "STYKKER_TEST_" + Guid.NewGuid().ToString("N");
}
