using StykkerSys.Platform.Windows;

namespace StykkerSys.Tests;

public class WindowsProcessProbeTests
{
    [Fact]
    public void TheOwnProcessHasAFilePath()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var probe = new WindowsProcessProbe();
        Assert.False(string.IsNullOrEmpty(probe.PathOf(Environment.ProcessId)));
    }

    [Fact]
    public void AnUnknownPidHasNoPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var probe = new WindowsProcessProbe();
        Assert.Null(probe.PathOf(2147483));
    }
}
