using System.ComponentModel;
using System.Diagnostics;

namespace StykkerSys.Platform.Windows;

// Windows-Hilfen, die nichts mit Messen zu tun haben.
public static class Shell
{
    // Den Explorer öffnen und die Datei darin markieren – der Weg, den der Explorer versteht.
    public static bool RevealFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
