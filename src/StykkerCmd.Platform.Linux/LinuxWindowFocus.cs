using System.Runtime.Versioning;
using StykkerCmd.Core.Abstractions;

namespace StykkerCmd.Platform.Linux;

// Unter Wayland darf eine Anwendung den Fokus nicht selbst setzen. Unter X11 wäre das möglich, hier bleibt es bei dem, was das System anbietet.
[SupportedOSPlatform("linux")]
public sealed class LinuxWindowFocus : IWindowFocus
{
    public void BringToFront(IntPtr windowHandle)
    {
        // Bewusst ohne Wirkung: der Fenstermanager entscheidet über den Vordergrund.
    }
}
