using Microsoft.Win32;

namespace StykkerStarter.Platform.Windows;

// Mit Windows starten: ein Eintrag unter HKCU\…\CurrentVersion\Run, nur für den Benutzer. Der Eintrag startet den
// Starter im Tray (--tray), ohne Fenster. Tests schreiben nie hierhin; die App schreibt nur mit dem Standard-Datenordner.
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Stykker Starter";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{exePath}\" --tray", RegistryValueKind.String);
        else if (key.GetValue(ValueName) != null)
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
