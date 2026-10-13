using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.FileProviders;

namespace Stykker.Shared.Web;

// Das Design-System der Stykker-Familie wird nicht kopiert: der Server liefert es unter /ds aus seiner Quelle. Eine
// Änderung dort wirkt beim nächsten Laden, und es gibt genau eine Fassung für die ganze Familie.
public static class DesignSystemHost
{
    // Früher der feste Ort der Monorepo. Sie ist seither umgezogen; der Wert bleibt als letzte Rückfallstufe.
    public const string LegacyFolder = @"C:\_AI\Stykker\MonoRepo\shared\design-system";

    // Die Vorgabe: shared/design-system der Monorepo, in der das Programm liegt – gesucht vom Programmordner aufwärts.
    // So folgt sie der Repo, wohin die auch zieht, statt an einem Laufwerk zu hängen.
    public static readonly string DefaultFolder = FindFrom(AppContext.BaseDirectory) ?? LegacyFolder;

    // Der erste Ordner oberhalb von start, der shared/design-system/tokens.css enthält; null, wenn es keinen gibt.
    public static string? FindFrom(string start)
    {
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "shared", "design-system");
            if (File.Exists(Path.Combine(candidate, "tokens.css"))) return candidate;
        }
        return null;
    }

    // Reihenfolge: --design-system <Ordner>, dann die Umgebungsvariable des Werkzeugs, dann die Vorgabe.
    public static string Resolve(string[] args, string environmentVariable) =>
        args.SkipWhile(a => a != "--design-system").Skip(1).FirstOrDefault()
        ?? Environment.GetEnvironmentVariable(environmentVariable)
        ?? DefaultFolder;

    // Fehlt der Ordner, läuft die Seite ohne ihre Stile weiter – und die Konsole sagt, wo gesucht wurde.
    public static void Map(WebApplication app, string folder)
    {
        if (Directory.Exists(folder))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(folder),
                RequestPath = "/ds",
            });
        }
        else
        {
            Console.Error.WriteLine($"[design] not found: {folder} – the page will load without its styles. Use --design-system <folder>.");
        }
    }
}
