using System.Diagnostics;
using StykkerCmd.Core.Abstractions;

namespace StykkerCmd.Platform.Windows;

// Öffnet eine Datei mit dem Standardprogramm (Enter auf einer Datei).
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsShell : IShell
{
    public void Open(string path)
        => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
}
