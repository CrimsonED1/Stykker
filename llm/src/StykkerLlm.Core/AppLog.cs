using System.Text;

namespace StykkerLlm.Core;

// Ein Protokoll je Programm: StykkerLLM-Server.log, StykkerUI.log, stykker.log. Es liegt bei der App (Ordner "logs"
// neben der exe), wenn dort geschrieben werden darf – so findet man es neben dem Programm, das es geschrieben hat.
// Sonst (z. B. unter Programme) im Datenordner unter "logs". Über 2 MB wird die Datei zu <name>.1.log und neu begonnen.
// Nichts hier ist fatal: kann nicht geschrieben werden, geht die Zeile verloren, das Programm läuft weiter.
public static class AppLog
{
    public const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _file;

    public static string? File => _file;
    public static string? App { get; private set; }

    // Einmal beim Start: wählt den Ordner und schreibt die Kopfzeile (Programm, Version, Betriebssystem)
    public static string? Init(string app, AppPaths paths, string? appDir = null)
    {
        App = app;
        lock (Gate) _file = PickFile(app, paths, appDir ?? AppContext.BaseDirectory);
        Write($"── {app} {Version()} · {Environment.OSVersion.VersionString} · .NET {Environment.Version} · data {paths.Root}");
        return _file;
    }

    // Kandidaten für Protokolle (bei der App, im Datenordner) – der Fehlerbericht sammelt aus beiden
    public static IEnumerable<string> Dirs(AppPaths paths, string? appDir = null)
    {
        yield return Path.Combine(appDir ?? AppContext.BaseDirectory, "logs");
        yield return paths.LogsDir;
    }

    public static void Write(string line)
    {
        var file = _file;
        if (file == null) return;
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var old = Path.ChangeExtension(file, ".1.log");
                    System.IO.File.Delete(old);
                    System.IO.File.Move(file, old);
                }
                System.IO.File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}", Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void Error(string what, Exception ex) => Write($"ERROR {what}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    // Abstürze und vergessene Task-Fehler landen im Protokoll statt im Nichts
    public static void CatchUnhandled()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Error("unhandled", ex); };
        TaskScheduler.UnobservedTaskException += (_, e) => { Error("unobserved task", e.Exception); e.SetObserved(); };
    }

    public static string Version() =>
        typeof(AppLog).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?";

    // Bei der App, wenn schreibbar (Probe: Ordner anlegen und an die Datei anhängen), sonst im Datenordner
    internal static string? PickFile(string app, AppPaths paths, string appDir)
    {
        foreach (var dir in new[] { Path.Combine(appDir, "logs"), paths.LogsDir })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, app + ".log");
                using (new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
                return file;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return null;
    }
}
