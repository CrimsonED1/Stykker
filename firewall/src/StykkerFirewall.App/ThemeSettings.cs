using System.IO;
using System.Text.Json;

namespace StykkerFirewall.App;

// Die Themenwahl der Stykker-Familie (shared/design-system): "dark" (Standard) oder "titan".
// Gilt für beide Fenster zugleich und bleibt in %LOCALAPPDATA%\Stykker\Firewall\ui.json gespeichert.
public sealed class ThemeSettings
{
    public const string Dark = "dark", Titan = "titan";

    private readonly string _file;

    public ThemeSettings(string file)
    {
        _file = file;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("theme", out var t) && Valid(t.GetString())) Current = t.GetString()!;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Keine oder kaputte Datei: Standard.
        }
    }

    public string Current { get; private set; } = Dark;

    public event Action? Changed;

    // Hintergrund des Fensters, bis die Seite gezeichnet ist (bg aus tokens.css), damit nichts aufblitzt.
    public System.Windows.Media.Color WindowBackground => Current == Titan
        ? System.Windows.Media.Color.FromRgb(0xd9, 0xdc, 0xdb)
        : System.Windows.Media.Color.FromRgb(0x06, 0x09, 0x16);

    public static void FollowBackground(System.Windows.Window window, IServiceProvider services)
    {
        if (services.GetService(typeof(ThemeSettings)) is not ThemeSettings theme) return;
        void Paint() => window.Background = new System.Windows.Media.SolidColorBrush(theme.WindowBackground);
        Paint();
        theme.Changed += () => window.Dispatcher.BeginInvoke(Paint);
    }

    public void Set(string theme)
    {
        if (!Valid(theme) || theme == Current) return;
        Current = theme;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(new { theme }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nicht gespeichert: gilt dann nur bis zum Ende der App.
        }
        Changed?.Invoke();
    }

    private static bool Valid(string? theme) => theme is Dark or Titan;
}
