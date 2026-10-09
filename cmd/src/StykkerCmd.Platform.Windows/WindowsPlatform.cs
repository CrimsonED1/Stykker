using StykkerCmd.Core.Abstractions;

namespace StykkerCmd.Platform.Windows;

// Baut die Windows-Dienste für die Oberfläche und den Helfer.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class WindowsPlatform
{
    public static IPlatformServices Create() => new PlatformServices(
        new WindowsFileSystem(),
        new WindowsTrash(),
        new WindowsLockInspector(),
        new WindowsElevation(),
        new WindowsShell(),
        new WindowsWindowFocus());
}
