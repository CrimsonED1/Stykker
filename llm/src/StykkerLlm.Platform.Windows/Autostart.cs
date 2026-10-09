using System.Runtime.Versioning;
using Microsoft.Win32;

namespace StykkerLlm.Platform.Windows;

// „Mit Windows starten“ für den angemeldeten Benutzer: ein Eintrag unter HKCU\…\Run. Keine Adminrechte, nur auf
// ausdrücklichen Wunsch (Schalter im Tray). Fehler sind nicht fatal: dann bleibt es eben, wie es war.
[SupportedOSPlatform("windows")]
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsOn(string name, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(name) is string v && v.Trim('"').Equals(exePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
    }

    public static bool Set(string name, string exePath, bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (on) key.SetValue(name, Command(exePath));
            else key.DeleteValue(name, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
    }

    // In Anführungszeichen, damit Pfade mit Leerzeichen funktionieren
    public static string Command(string exePath) => "\"" + exePath + "\"";
}
