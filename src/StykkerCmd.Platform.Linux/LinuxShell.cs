using System.Diagnostics;
using System.Runtime.Versioning;
using StykkerCmd.Core.Abstractions;

namespace StykkerCmd.Platform.Linux;

// Öffnet Dateien und Ordner über xdg-open. Ein natives Kontextmenü gibt es hier nicht.
[SupportedOSPlatform("linux")]
public sealed class LinuxShell : IShell
{
    public bool SupportsNativeMenu => false;

    public void Open(string path) => Start(path);

    // Unter Linux gibt es kein Markieren im Dateimanager über einen Befehl: der Ordner des Eintrags öffnet sich.
    public void Reveal(string path) => Start(Path.GetDirectoryName(path) ?? "/");

    public void ShowNativeMenu(IReadOnlyList<string> paths, IntPtr owner)
        => throw new PlatformNotSupportedException("Das native Kontextmenü gibt es nur unter Windows.");

    private static void Start(string target)
    {
        var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(target);
        Process.Start(start)?.Dispose();
    }
}
