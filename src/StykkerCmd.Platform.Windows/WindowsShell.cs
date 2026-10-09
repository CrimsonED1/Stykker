using System.Diagnostics;
using StykkerCmd.Core.Abstractions;

namespace StykkerCmd.Platform.Windows;

// Öffnen, Anzeigen im Explorer und das native Kontextmenü der Shell.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsShell : IShell
{
    public bool SupportsNativeMenu => true;

    // Öffnet eine Datei mit dem Standardprogramm (Enter auf einer Datei).
    public void Open(string path)
        => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    // explorer.exe /select öffnet den Ordner und markiert den Eintrag.
    public void Reveal(string path)
        => Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"", UseShellExecute = false })?.Dispose();

    public void ShowNativeMenu(IReadOnlyList<string> paths, IntPtr owner)
        => ShellContextMenu.Show(paths, owner);
}
