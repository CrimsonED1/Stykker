using System.Runtime.Versioning;
using StykkerCmd.Core.Abstractions;

namespace StykkerCmd.Platform.Linux;

// Baut die Linux-Dienste für die Oberfläche und den Helfer.
[SupportedOSPlatform("linux")]
public static class LinuxPlatform
{
    public static IPlatformServices Create() => new PlatformServices(
        new LinuxFileSystem(),
        new LinuxTrash(),
        new LinuxLockInspector(),
        new LinuxElevation(),
        new LinuxShell(),
        new LinuxWindowFocus());
}
