using System.Runtime.Versioning;
using StykkerCmd.Core.Abstractions;

namespace StykkerCmd.Platform.Windows;

// Holt das Fenster per SetForegroundWindow nach vorn. Die Anwendung hat das Recht dazu, solange sie zuletzt Eingaben bekam.
[SupportedOSPlatform("windows")]
public sealed class WindowsWindowFocus : IWindowFocus
{
    public void BringToFront(IntPtr windowHandle)
    {
        if (windowHandle != IntPtr.Zero)
            NativeMethods.SetForegroundWindow(windowHandle);
    }
}
