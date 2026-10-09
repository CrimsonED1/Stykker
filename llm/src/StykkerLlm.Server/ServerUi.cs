using System.Diagnostics;

namespace StykkerLlm.Server;

// Browser öffnen (Start des Servers, zweiter Start, Knopf im Fenster/TUI später). Ein Tray-Symbol folgt ohne WinForms.
internal static class ServerUi
{
    public static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
