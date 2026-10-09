using Avalonia;
using Avalonia.Media;

namespace StykkerCmd.UI.Theming;

// Legt die Farben des aktiven Themas als SolidColorBrush unter ihrem Token-Namen in die App-Ressourcen.
// Views binden mit {DynamicResource <token>}; ein Themenwechsel wirkt dann ohne Neuaufbau der Fenster.
public sealed class ThemeService
{
    private readonly Application _app;
    private readonly IReadOnlyList<ThemePalette> _themes;
    private int _index;

    public ThemeService(Application app, IReadOnlyList<ThemePalette> themes)
    {
        if (themes.Count == 0)
            throw new ArgumentException("Mindestens ein Thema ist nötig.", nameof(themes));

        _app = app;
        _themes = themes;
        Apply();
    }

    public ThemePalette Current => _themes[_index];

    public void Next()
    {
        _index = (_index + 1) % _themes.Count;
        Apply();
    }

    private void Apply()
    {
        foreach (var (name, color) in Current.Colors)
            _app.Resources[name] = new SolidColorBrush(color);

        // Eckenradius je Thema: Dark ist rund, Titan eine harte Platte (bundle.css setzt dort 2px).
        _app.Resources["PanelRadius"] = new CornerRadius(Current.Id == "titan" ? 2 : 8);
    }
}
