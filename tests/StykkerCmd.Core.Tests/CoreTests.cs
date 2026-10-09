using StykkerCmd.Core;
using Xunit;

namespace StykkerCmd.Core.Tests;

public class CoreTests
{
    [Fact]
    public void Product_name_is_StykkerCMD()
    {
        Assert.Equal("StykkerCMD", AppInfo.Name);
    }

    // PLAN.md, Meilenstein 1: Core referenziert kein Avalonia.
    [Fact]
    public void Core_does_not_reference_Avalonia()
    {
        var references = typeof(AppInfo).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(references, name => name.StartsWith("Avalonia", StringComparison.Ordinal));
    }

    // Abhängigkeiten zeigen nur nach innen: UI -> Platform -> Core.
    [Fact]
    public void Core_does_not_reference_the_UI_layer()
    {
        var references = typeof(AppInfo).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(references, name => name == "StykkerCmd.UI");
    }
}
