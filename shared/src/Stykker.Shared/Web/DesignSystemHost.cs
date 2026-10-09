using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.FileProviders;

namespace Stykker.Shared.Web;

// Das Design-System der Stykker-Familie wird nicht kopiert: der Server liefert es unter /ds aus seiner Quelle. Eine
// Änderung dort wirkt beim nächsten Laden, und es gibt genau eine Fassung für die ganze Familie.
public static class DesignSystemHost
{
    public const string DefaultFolder = @"C:\_AI\Stykker\MonoRepo\shared\design-system";

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
