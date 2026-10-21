using System.Security.Cryptography;
using System.Text;
using StykkerStarter.Core;

namespace StykkerStarter.App;

// StykkerStarter.exe: läuft im Tray weiter. Das Fenster entsteht beim Öffnen und endet beim Schließen ganz (nicht
// versteckt). Die Seite ist eingebettet und wird ohne Server und ohne Port geladen. Ohne Grafikkarte zeichnet das
// Fenster wie StykkerUI; --gpu schaltet das ab.
//   StykkerStarter.exe [--data-dir <Ordner>] [--tray] [--gpu] [--check-every <Sekunden>]
//     --data-dir      eigener Datenordner (Tests, Probeläufe); dann schreibt der Starter nie in die Registry
//     --tray          nur das Tray-Symbol, kein Fenster (der Autostart nutzt das)
//     --check-every   Prüfabstand in Sekunden statt der Einstellung (nur für Probeläufe)
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var logPath = Path.Combine(Path.GetDirectoryName(AppContext.BaseDirectory) ?? ".", "startup-error.txt");
        try
        {
            var opt = Options.Parse(args);
            if (!opt.Gpu && OperatingSystem.IsWindows())
                Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-gpu --disable-gpu-compositing");

            var layout = new DataLayout(opt.DataDir ?? DefaultDataDir());
            Directory.CreateDirectory(layout.Root);

            // Ein Starter je Benutzer (und je Datenordner): ein zweiter Start holt das Fenster des ersten und endet still
            var name = SingleInstanceName(layout.Root, isDefault: opt.DataDir == null);
            using var guard = new Mutex(initiallyOwned: true, name, out bool first);
            if (!first)
            {
                SingleInstance.SignalOpen(name);
                return 0;
            }

            using var shell = new Shell(layout, opt, name);
            return shell.Run(openWindow: !opt.TrayOnly);
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, ex.ToString());
            return -1;
        }
    }

    // %LOCALAPPDATA%\Stykker\Starter – nur ohne --data-dir
    private static string DefaultDataDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stykker", "Starter");

    private static string SingleInstanceName(string root, bool isDefault)
    {
        if (isDefault) return "Local\\StykkerStarter.Single";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..16];
        return "Local\\StykkerStarter.Single." + hash;
    }

    internal sealed record Options(string? DataDir, bool TrayOnly, bool Gpu, int? CheckEverySeconds)
    {
        public static Options Parse(string[] args)
        {
            string? dataDir = null;
            bool trayOnly = false, gpu = false;
            int? checkEvery = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--data-dir" when i + 1 < args.Length: dataDir = args[++i]; break;
                    case "--tray": trayOnly = true; break;
                    case "--gpu": gpu = true; break;
                    case "--check-every" when i + 1 < args.Length && int.TryParse(args[i + 1], out var s) && s >= 1:
                        checkEvery = s;
                        i++;
                        break;
                }
            }
            return new Options(dataDir, trayOnly, gpu, checkEvery);
        }
    }
}
