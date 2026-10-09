using StykkerCmd.Core.Abstractions;
using StykkerCmd.Platform.Linux;
using StykkerCmd.Platform.Windows;

namespace StykkerCmd.UI;

// Wählt die Dienste des laufenden Betriebssystems.
public static class PlatformFactory
{
    public static IPlatformServices Create()
    {
        if (OperatingSystem.IsWindows())
            return WindowsPlatform.Create();
        if (OperatingSystem.IsLinux())
            return LinuxPlatform.Create();
        throw new PlatformNotSupportedException("StykkerCMD unterstützt Windows und Linux.");
    }
}
