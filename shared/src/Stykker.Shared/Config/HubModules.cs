using System.Text.Json;
using System.Text.RegularExpressions;
using Stykker.Pairing;

namespace Stykker.Shared.Config;

// Ein "start" aus stykker.json: das Programm, seine Argumente, die Umgebung und der Name der Variable. Die Zentrale
// startet keine Dienste mehr; das Feld wird nur gelesen, damit sie das Ignorieren einmal ins Log schreiben kann.
public sealed record HubStart(string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env, string TokenEnv);

// Ein Eintrag der Zentrale: ein eigenes Modul (über seinen Port, unter dem Rechnernamen der Zentrale) oder ein
// fremder Web-Dienst (über seine volle Adresse). Ein fremder Dienst mit Target und Port leitet die Zentrale auf diesem
// Port weiter; das Target muss ServiceTargetRule erfüllen (nur dieser PC). Token ist das Geräte-Token des Dienstes, im
// Klartext in stykker.json; es setzt die Weiterleitung ein und taucht nie in einer Ausgabe auf (ToString lässt es weg).
// Start wird nicht mehr ausgeführt: die Zentrale startet keinen Dienst. Das Feld wird nur gelesen, damit sie einmal ins
// Log schreiben kann, dass es ignoriert wird.
public sealed record HubModule(string Id, string Name, int? Port, string? Url, bool External, string? Target = null, HubStart? Start = null, string? Token = null)
{
    // Die Adresse für den Frame. Eigene Module hängen am Rechnernamen der Anfrage: nur unter demselben Namen gibt der
    // Browser die Anmeldung in den Frame mit (docs/spike-frame-proxy.md). null = der Eintrag ist die Zentrale selbst.
    public string? AddressFor(string scheme, string host)
    {
        if (!string.IsNullOrEmpty(Url)) return Url;
        return Port is > 0 ? $"{scheme}://{host}:{Port}/" : null;
    }

    // Ohne das Token: ein Protokoll, ein Log oder ein Fehlertext druckt den Eintrag, nie das Geheimnis.
    public override string ToString() =>
        $"HubModule {{ Id = {Id}, Name = {Name}, Port = {Port}, External = {External}, Target = {Target}, TokenSet = {!string.IsNullOrEmpty(Token)} }}";
}

// Die Module der Zentrale aus stykker.json (docs/plan-stykkernode.md, Abschnitt 4.2). Ohne Datei gilt die Vorgabe:
// die Suite auf ihren festen Ports. Die Datei enthält das Token fremder Dienste im Klartext (eine bewusste Entscheidung).
//
//   { "hostNames": ["pc.example.ts.net"],
//     "modules": [ { "id": "llm", "name": "LLM", "port": 17401 },
//                  { "id": "qwen", "name": "Qwen Code", "url": "http://pc:17450/", "external": true },
//                  { "id": "qwen", "name": "Qwen Code", "external": true, "port": 17450,
//                    "target": "http://192.0.2.10:16001", "token": "…" } ] }
//
// Ein "start" in der Datei wird nicht mehr ausgeführt: die Zentrale startet keinen Dienst.
public static class HubModules
{
    public const string FileName = "stykker.json";
    public const string MonitorId = "monitor";

    private static readonly Regex EnvNamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);

    // Der Monitor ist die Zentrale selbst (kein Port); SYS und LLM auf ihren festen Ports.
    public static readonly IReadOnlyList<HubModule> Defaults =
    [
        new(MonitorId, "Monitor", null, null, false),
        new("sys", "SYS", StykkerPorts.Sys, null, false),
        new("llm", "LLM", StykkerPorts.Llm, null, false),
    ];

    // %APPDATA%\Stykker\stykker.json (unter Linux ~/.config/Stykker/stykker.json)
    public static string DefaultFile() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stykker", FileName);

    // Fehlt die Datei, ist sie unlesbar oder nennt sie kein gültiges Modul, gilt die Vorgabe: ein Tippfehler in der
    // Konfiguration darf die Zentrale nicht leeren.
    // ownAddresses sind die IPv4-Adressen dieses PCs (ServiceTargetRule.ReadThisPcAddresses); sie gehen in die Regel für
    // das Ziel einer Weiterleitung ein. Die Namen aus "hostNames" liest Parse selbst aus derselben Datei.
    public static IReadOnlyList<HubModule> Load(string? file = null, IReadOnlyCollection<string>? ownAddresses = null)
    {
        file ??= DefaultFile();
        try
        {
            if (!File.Exists(file)) return Defaults;
            var parsed = Parse(File.ReadAllText(file), ownAddresses);
            return parsed.Count > 0 ? parsed : Defaults;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return Defaults; }
    }

    public static IReadOnlyList<HubModule> Parse(string json, IReadOnlyCollection<string>? ownAddresses = null)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("modules", out var modules)
            || modules.ValueKind != JsonValueKind.Array) return [];

        var own = ownAddresses ?? [];
        var hostNames = HostNames.ExtraFrom(document.RootElement);
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

            // Weiterleitung nur für einen fremden Dienst mit eigenem Port. Ein anderes Ziel verwirft nur die Weiterleitung.
            var target = external && port != null ? ServiceTargetRule.Allowed(Text(entry, "target"), own, hostNames) : null;
            var start = target != null ? StartOf(entry) : null;
            // Das Token gehört nur zu einer Weiterleitung; ein unbrauchbares Token fällt nur weg (die Weiterleitung bleibt)
            var token = target != null ? TokenOf(entry) : null;

            var name = Text(entry, "name")?.Trim();
            result.Add(new HubModule(id, string.IsNullOrEmpty(name) ? id.ToUpperInvariant() : name[..Math.Min(40, name.Length)], port, url, external, target, start, token));
        }
        return result;
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Nur http und https: die Adresse landet als src eines Frames in der Seite
    private static string? HttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.ToString() : null;

    // "token": Text ohne Leerraum, sichtbare ASCII-Zeichen (es geht in einen HTTP-Kopf), in vernünftiger Länge. Sonst null.
    // Der Wert wird nie ausgegeben: keine Meldung enthält ihn.
    public static string? TokenOf(JsonElement entry)
    {
        var token = Text(entry, "token")?.Trim();
        return token is { Length: > 0 and <= 512 } && token.All(c => c is >= '!' and <= '~') ? token : null;
    }

    // Ein kaputtes start macht nur den Start ungültig, nicht den Eintrag. tokenEnv ist Pflicht: ohne den Namen gäbe es
    // keinen Weg, das Token an den Dienst zu geben.
    private static HubStart? StartOf(JsonElement entry)
    {
        if (!entry.TryGetProperty("start", out var start) || start.ValueKind != JsonValueKind.Object) return null;
        var command = Text(start, "command")?.Trim();
        var tokenEnv = Text(start, "tokenEnv")?.Trim();
        if (string.IsNullOrEmpty(command) || tokenEnv == null || !EnvName(tokenEnv)) return null;

        var args = new List<string>();
        if (start.TryGetProperty("args", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array) return null;
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return null;
                args.Add(item.GetString()!);
            }
        }

        var env = new Dictionary<string, string>();
        if (start.TryGetProperty("env", out var vars))
        {
            if (vars.ValueKind != JsonValueKind.Object) return null;
            foreach (var variable in vars.EnumerateObject())
            {
                if (variable.Value.ValueKind != JsonValueKind.String || !EnvName(variable.Name)) return null;
                env[variable.Name] = variable.Value.GetString()!;
            }
        }
        return new HubStart(command, args, env, tokenEnv);
    }

    private static bool EnvName(string? name) => name != null && EnvNamePattern.IsMatch(name);
}
