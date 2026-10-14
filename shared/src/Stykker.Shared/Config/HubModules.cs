using System.Text.Json;
using System.Text.RegularExpressions;
using Stykker.Pairing;

namespace Stykker.Shared.Config;

// Ein "start" aus stykker.json: das Programm, seine Argumente, die Umgebung und der Name der Variable. Die Zentrale
// startet keine Dienste mehr; das Feld wird nur gelesen, damit sie das Ignorieren einmal ins Log schreiben kann.
public sealed record HubStart(string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env, string TokenEnv);

// Woher ein fremder Dienst sein Token nimmt: die Datei (Pfad mit Umgebungsvariablen aufgelöst) und ihr Schutz
// ("none" oder "dpapi-user:<Zusatzwert>"; Stykker.Pairing, ServiceTokenFile).
public sealed record HubTokenFile(string Path, string Protection);

// Ein Eintrag der Zentrale: ein eigenes Modul (über seinen Port, unter dem Rechnernamen der Zentrale) oder ein
// fremder Web-Dienst (über seine volle Adresse). Ein fremder Dienst mit Target und Port leitet die Zentrale auf diesem
// Port weiter; das Target ist fest (nur 127.0.0.1 oder localhost). TokenFile ist optional: ohne sie wird ohne Token
// weitergeleitet. Start wird nicht mehr ausgeführt: die Zentrale startet keinen Dienst. Das Feld wird nur gelesen, damit
// sie einmal ins Log schreiben kann, dass es ignoriert wird.
public sealed record HubModule(string Id, string Name, int? Port, string? Url, bool External, string? Target = null, HubStart? Start = null, HubTokenFile? TokenFile = null)
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
//   { "modules": [ { "id": "llm", "name": "LLM", "port": 17401 },
//                  { "id": "qwen", "name": "Qwen Code", "url": "http://pc:17450/", "external": true },
//                  { "id": "qwen", "name": "Qwen Code", "external": true, "port": 17450, "target": "http://127.0.0.1:16000",
//                    "tokenFile": { "path": "%APPDATA%\\com.alibaba.qwen-code\\daemon-token.bin",
//                                   "protection": "dpapi-user:qwen-desktop.daemon-token.v1" } } ] }
//
// Ein "start" in der Datei wird nicht mehr ausgeführt: die Zentrale startet keinen Dienst.
public static class HubModules
{
    public const string FileName = "stykker.json";
    public const string MonitorId = "monitor";

    // Nur diese Form: das Token geht nur an den eigenen Rechner.
    private static readonly Regex LocalTargetPattern = new(@"^http://(127\.0\.0\.1|localhost):(\d{1,5})/?$", RegexOptions.CultureInvariant);
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

            // Weiterleitung nur für einen fremden Dienst mit eigenem Port. Ein anderes Ziel verwirft nur die Weiterleitung.
            var target = external && port != null ? LocalTarget(Text(entry, "target")) : null;
            var start = target != null ? StartOf(entry) : null;
            // Das Token kommt aus einer Datei, nur bei einer Weiterleitung; ein unbrauchbarer Eintrag fällt nur weg
            var tokenFile = target != null ? TokenFileOf(entry) : null;

            var name = Text(entry, "name")?.Trim();
            result.Add(new HubModule(id, string.IsNullOrEmpty(name) ? id.ToUpperInvariant() : name[..Math.Min(40, name.Length)], port, url, external, target, start, tokenFile));
        }
        return result;
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Nur http und https: die Adresse landet als src eines Frames in der Seite
    private static string? HttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.ToString() : null;

    // "http://127.0.0.1:17451" oder "http://localhost:17451" mit Port; alles andere (auch ein fremder Rechner) ist null.
    // Das Ergebnis hat keinen Schrägstrich am Ende.
    public static string? LocalTarget(string? target)
    {
        if (target == null) return null;
        var match = LocalTargetPattern.Match(target.Trim());
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var port) || port is < 1 or > 65535) return null;
        return $"http://{match.Groups[1].Value}:{port}";
    }

    // "tokenFile": { "path": "...", "protection": "none" | "dpapi-user:<Zusatzwert>" }. Fehlt ein Teil oder ist er
    // ungültig, gibt es keine Token-Datei (der Eintrag bleibt, die Weiterleitung läuft ohne Token).
    private static HubTokenFile? TokenFileOf(JsonElement entry)
    {
        if (!entry.TryGetProperty("tokenFile", out var tokenFile) || tokenFile.ValueKind != JsonValueKind.Object) return null;
        var path = Text(tokenFile, "path")?.Trim();
        var protection = Text(tokenFile, "protection")?.Trim();
        if (string.IsNullOrEmpty(path) || !ServiceTokenFile.ProtectionKnown(protection)) return null;
        return new HubTokenFile(Environment.ExpandEnvironmentVariables(path), protection!);
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
