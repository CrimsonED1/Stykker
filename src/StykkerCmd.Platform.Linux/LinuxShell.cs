using System.Diagnostics;
using System.Runtime.Versioning;
using StykkerCmd.Core.Abstractions;

namespace StykkerCmd.Platform.Linux;

// Öffnet eine Datei über xdg-open.
[SupportedOSPlatform("linux")]
public sealed class LinuxShell : IShell
{
    public void Open(string path)
    {
        var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(path);
        Process.Start(start)?.Dispose();
    }
}
