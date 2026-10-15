using System.Text;

namespace StykkerLlm.Core;

// Ein Protokoll je Programm: StykkerLLM-Server.log, StykkerUI.log, stykker.log. Es liegt im Protokollordner (AppPaths.LogsDir):
// bei der App (Ordner "logs" neben der exe), wenn dort geschrieben werden darf – so findet man es neben dem Programm, das es
// geschrieben hat. Sonst (z. B. unter Programme) im Datenordner unter "logs". Mit --data-dir alles im Datenordner.
// Über 2 MB wird die Datei zu <name>.1.log und neu begonnen.
// Nichts hier ist fatal: kann nicht geschrieben werden, geht die Zeile verloren, das Programm läuft weiter.
public static class AppLog
{
    public const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _file;

    // Die Programme, die ein Protokoll schreiben (Dateiname <name>.log und <name>.1.log nach dem Umbruch)
    private static readonly string[] Programs = { "StykkerLLM-Server", "StykkerUI", "stykker", "StykkerHost", "StykkerHost-service" };

    public static string? File => _file;
    public static string? App { get; private set; }

    // Einmal beim Start: wählt den Ordner und schreibt die Kopfzeile (Programm, Version, Betriebssystem)
    public static string? Init(string app, AppPaths paths, string? appDir = null)
    {
        App = app;
        lock (Gate) _file = PickFile(app, paths, appDir ?? paths.AppDir);
        Write($"── {app} {Version()} · {Environment.OSVersion.VersionString} · .NET {Environment.Version} · data {paths.Root}");
        return _file;
    }

    // Kandidaten für Protokolle (neben der App, im Protokollordner, alte Protokolle im Datenordner) – der Fehlerbericht sammelt aus allen
    public static IEnumerable<string> Dirs(AppPaths paths, string? appDir = null)
    {
        if ((appDir ?? paths.AppDir) is { } app) yield return Path.Combine(app, "logs");
        yield return paths.LogsDir;
        yield return Path.Combine(paths.Root, "logs");
    }

    // Die Protokolle der Programme selbst (StykkerUI.log, StykkerLLM-Server.log …, auch <name>.1.log): das Aufräumen der
    // Server-Logs im selben Ordner lässt sie in Ruhe
    public static bool IsProgramLog(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return Programs.Any(p => name.Equals(p + ".log", StringComparison.OrdinalIgnoreCase)
                              || name.Equals(p + ".1.log", StringComparison.OrdinalIgnoreCase));
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

    // Der Protokollordner (AppPaths.PickLogsDir: bei der App, wenn schreibbar, sonst im Datenordner). appDir null = der Ordner
    // des Datenordners (--data-dir). Die Datei wird angelegt und zum Anhängen geöffnet; geht das nicht, gibt es kein Protokoll.
    internal static string? PickFile(string app, AppPaths paths, string? appDir)
    {
        try
        {
            var dir = appDir == null ? paths.LogsDir : AppPaths.PickLogsDir(appDir, paths.Root);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, app + ".log");
            using (new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }
}
