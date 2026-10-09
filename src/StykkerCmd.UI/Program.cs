using Avalonia;
using StykkerCmd.Core.Elevation;

namespace StykkerCmd.UI;

internal static class Program
{
    // Helfer-Modus: Die Anwendung läuft ohne Oberfläche mit erhöhten Rechten und führt nur die übergebenen Schritte aus.
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == ElevatedHelper.Flag)
            return ElevatedHelper.Run(args[1], PlatformFactory.Create());

        StartupOptions.Parse(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia-Konfiguration; auch der Designer nutzt sie. Bewusst ohne WithInterFont(): Schriften kommen vom System.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
