using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using StykkerCmd.Core.Abstractions;
using StykkerCmd.UI.Theming;

namespace StykkerCmd.UI;

public partial class App : Application
{
    // Dienste des Betriebssystems. Hauptfenster und Dialoge holen sie von hier.
    public IPlatformServices Services { get; private set; } = null!;

    // Aktives Thema; es legt die Farben als Ressourcen unter den Token-Namen ab.
    public ThemeService Themes { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composition Root: Plattform, Design-Tokens, Schriften, Thema und Hauptfenster.
            Services = PlatformFactory.Create();

            var design = DesignTokens.LoadEmbedded();
            Resources["SansFont"] = FontStack.Pick(design.SansFamilies, FontManager.Current.SystemFonts);
            Resources["MonoFont"] = FontStack.Pick(design.MonoFamilies, FontManager.Current.SystemFonts);

            Themes = new ThemeService(this, design.Themes);
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
