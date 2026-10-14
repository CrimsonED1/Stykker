using System.Text.RegularExpressions;

namespace StykkerStarter.App;

// Die Seite aus den eingebetteten Dateien: die eigene Seite (Web\index.html, app.css, app.js) und das Design-System
// (ds\…) werden zu einem HTML-Text zusammengesetzt und mit LoadRawString geladen. Es gibt keinen Server, keinen Port
// und keine Datei auf der Platte. Das Schema-Verfahren aus dem Auftrag ließ sich mit Photino 4.0.16 nicht nutzen: der
// Handler bekam keine Anfrage (siehe DEVIATIONS im Bericht).
internal static partial class WebContent
{
    private const string PageFile = "index.html";

    // Das Ergebnis ist eine feste Zeichenkette; sie wird je Fensteröffnung gebaut.
    public static string Page(bool gpu)
    {
        var html = ReadResource(PageFile);
        html = Stylesheet().Replace(html, m => "<style>\n" + ReadResource(m.Groups[1].Value) + "\n</style>");
        html = Script().Replace(html, m => "<script>\n" + ReadResource(m.Groups[1].Value) + "\n</script>");
        // Ohne Grafikkarte: die Oberfläche bleibt ohne Glanz und Übergänge (wie im Design-System beschrieben)
        if (!gpu) html = html.Replace("<html lang=\"en\" data-theme=\"dark\">", "<html lang=\"en\" data-theme=\"dark\" class=\"shell\">");
        return html;
    }

    // Nur eingebettete Dateien: alles, was nicht unter web/ liegt, gibt es nicht.
    private static string ReadResource(string name)
    {
        if (name.Contains("..") || name.Contains(':') || name.StartsWith('/') || name.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"not an embedded file: {name}");
        using var stream = typeof(WebContent).Assembly.GetManifestResourceStream("web/" + name)
            ?? throw new InvalidOperationException($"embedded file missing: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex("<link rel=\"stylesheet\" href=\"([^\"]+)\">")]
    private static partial Regex Stylesheet();

    [GeneratedRegex("<script src=\"([^\"]+)\"></script>")]
    private static partial Regex Script();
}
