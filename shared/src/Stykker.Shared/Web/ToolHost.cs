using System.ComponentModel;
using System.Diagnostics;

namespace Stykker.Shared.Web;

// Was jedes Werkzeug beim Start gleich macht: nur ein Server je Port, und der Browser öffnet sich auf Wunsch.
public static class ToolHost
{
    // Ein Server je Port. Die Mutex gehört dem Server: solange er lebt, hält sie den Port für ihn.
    public static Mutex ClaimPort(string toolName, int port, out bool first) =>
        new(true, @"Local\" + toolName + "-server-" + port, out first);

    public static void OpenBrowser(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or PlatformNotSupportedException) { }
    }
}
