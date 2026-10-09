using System.Reflection;
using Xunit;

namespace StykkerCmd.Platform.Tests;

public class PlatformBoundaryTests
{
    // Platform-Schichten dürfen weder Avalonia noch die UI-Schicht kennen (PLAN.md, Abschnitt 2).
    [Theory]
    [InlineData("StykkerCmd.Platform.Windows")]
    [InlineData("StykkerCmd.Platform.Linux")]
    public void Platform_assembly_does_not_reference_Avalonia_or_UI(string assemblyName)
    {
        var references = Assembly.Load(assemblyName).GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(references, name => name.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name == "StykkerCmd.UI");
    }
}
