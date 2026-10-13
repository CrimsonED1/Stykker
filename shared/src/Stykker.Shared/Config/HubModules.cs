using System.Text.Json;

namespace Stykker.Shared.Config;

// Ein Eintrag der Zentrale: ein eigenes Modul (über seinen Port, unter dem Rechnernamen der Zentrale) oder ein
// fremder Web-Dienst (über seine volle Adresse).
public sealed record HubModule(string Id, string Name, int? Port, string? Url, bool External)
{
    // Die Adresse für den Frame. Eigene Module hängen am Rechnernamen der Anfrage: nur unter demselben Namen gibt der
    // Browser die Anmeldung in den Frame mit (docs/spike-frame-proxy.md). null = der Eintrag ist die Zentrale selbst.
    public string? AddressFor(string scheme, string host)
    {
        if (!string.IsNullOrEmpty(Url)) return Url;
        return Port is > 0 ? $"{scheme}://{host}:{Port}/" : null;
    }
}

// Die Module der Zentrale aus stykker.json (docs/plan-stykkernode.md, Abschnitt 4.2). Ohne Datei gilt die Vorgabe:
// die Suite auf ihren festen Ports. Die Datei ist lesbar und enthält keine Geheimnisse.
//
//   { "modules": [ { "id": "llm", "name": "LLM", "port": 8078 },
//                  { "id": "qwen", "name": "Qwen Code", "url": "http://pc:8076/", "external": true } ] }
public static class HubModules
{
    public const string FileName = "stykker.json";
    public const string MonitorId = "monitor";

    // Der Monitor ist die Zentrale selbst (kein Port); SYS und LLM auf ihren festen Ports.
    public static readonly IReadOnlyList<HubModule> Defaults =
    [
        new(MonitorId, "Monitor", null, null, false),
        new("sys", "SYS", 8077, null, false),
        new("llm", "LLM", 8078, null, false),
    ];

    // %APPDATA%\Stykker\stykker.json (unter Linux ~/.config/Stykker/stykker.json)
    public static string DefaultFile() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stykker", FileName);

    // Fehlt die Datei, ist sie unlesbar oder nennt sie kein gültiges Modul, gilt die Vorgabe: ein Tippfehler in der
    // Konfiguration darf die Zentrale nicht leeren.
    public static IReadOnlyList<HubModule> Load(string? file = null)
    {
        file ??= DefaultFile();
        try
        {
            if (!File.Exists(file)) return Defaults;
            var parsed = Parse(File.ReadAllText(file));
            return parsed.Count > 0 ? parsed : Defaults;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return Defaults; }
    }

    public static IReadOnlyList<HubModule> Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("modules", out var modules)
            || modules.ValueKind != JsonValueKind.Array) return [];

        var result = new List<HubModule>();
        foreach (var entry in modules.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var id = Text(entry, "id")?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(id) || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || result.Any(m => m.Id == id)) continue;

            int? port = entry.TryGetProperty("port", out var p) && p.TryGetInt32(out var number) && number is > 0 and <= 65535 ? number : null;
            var url = HttpUrl(Text(entry, "url"));
            bool external = entry.TryGetProperty("external", out var e) && e.ValueKind == JsonValueKind.True;
            // Ein Eintrag ohne Port und ohne Adresse ist nur als Monitor sinnvoll (die Zentrale selbst)
            if (port == null && url == null && id != MonitorId) continue;

            var name = Text(entry, "name")?.Trim();
            result.Add(new HubModule(id, string.IsNullOrEmpty(name) ? id.ToUpperInvariant() : name[..Math.Min(40, name.Length)], port, url, external));
        }
        return result;
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Nur http und https: die Adresse landet als src eines Frames in der Seite
    private static string? HttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.ToString() : null;
}
