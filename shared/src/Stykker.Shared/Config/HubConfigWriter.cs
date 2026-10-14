using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stykker.Pairing;

namespace Stykker.Shared.Config;

// Ein fremder Dienst, den die Zentrale neu einträgt. Token ist das Geräte-Token aus dem Tausch (nie das Geheimnis).
public sealed record ServiceEntry(string Id, string Name, int Port, string Target, string Token);

// Ein fremder Dienst, wie die Liste ihn zeigt: ohne das Token, nur mit der Angabe, ob eines gesetzt ist.
public sealed record ServiceListing(string Id, string Name, int Port, string? Target, bool TokenSet);

// Das Ergebnis eines Schreibvorgangs. Message ist ein Text für die Oberfläche und enthält nie ein Token.
public sealed record WriteResult(bool Ok, string Message);

// Die fremden Dienste in stykker.json ändern: eintragen, die URL (Ziel und Token) ersetzen, entfernen. Die übrigen Einträge
// und Felder bleiben, wie sie sind. Kommentare in der Datei gehen dabei verloren (sie werden neu geschrieben). Eine Datei,
// die kein gültiges JSON ist, wird nicht angefasst. Geschrieben wird atomar: erst in eine Datei daneben, dann an die Stelle
// der alten.
public static class HubConfigWriter
{
    // Die Ids, die die Zentrale selbst oder die Vorgabe belegen
    private static readonly string[] ReservedIds = [HubModules.MonitorId, "sys", "llm"];
    private const int MaxNameLength = 40;
    private static readonly object Gate = new();

    // Prüft einen neuen Dienst, ohne etwas zu schreiben (die Oberfläche fragt, bevor der Tausch läuft).
    public static WriteResult Check(string file, string id, string name, int port, int hubPort)
    {
        lock (Gate)
        {
            if (!TryLoad(file, out var root, out var failure)) return failure!;
            var modules = root["modules"] as JsonArray ?? new JsonArray();
            return CheckNew(modules, id, name, port, hubPort);
        }
    }

    public static WriteResult Add(string file, ServiceEntry entry, int hubPort)
    {
        return Edit(file, modules =>
        {
            var check = CheckNew(modules, entry.Id, entry.Name, entry.Port, hubPort);
            if (!check.Ok) return check;
            modules.Add(new JsonObject
            {
                ["id"] = entry.Id,
                ["name"] = entry.Name.Trim(),
                ["external"] = true,
                ["port"] = entry.Port,
                ["target"] = entry.Target,
                ["token"] = entry.Token,
            });
            return new WriteResult(true, $"Added {entry.Name} on port {entry.Port}.");
        });
    }

    // Ziel und Token eines Dienstes ersetzen; Name, Id und Port bleiben. token null = ohne Token.
    public static WriteResult Replace(string file, string id, string target, string? token)
    {
        return Edit(file, modules =>
        {
            var entry = FindExternal(modules, id);
            if (entry == null) return new WriteResult(false, "No service with this id.");
            entry["target"] = target;
            if (token == null) entry.Remove("token");
            else entry["token"] = token;
            return new WriteResult(true, "The address is replaced.");
        });
    }

    public static WriteResult Remove(string file, string id)
    {
        return Edit(file, modules =>
        {
            var entry = FindExternal(modules, id);
            if (entry == null) return new WriteResult(false, "No service with this id.");
            modules.Remove(entry);
            return new WriteResult(true, "Removed.");
        });
    }

    // Die fremden Dienste mit Port, für die Liste. Ohne Datei oder bei einem Fehler: leer.
    public static IReadOnlyList<ServiceListing> List(string file)
    {
        lock (Gate)
        {
            if (!TryLoad(file, out var root, out _)) return [];
            var result = new List<ServiceListing>();
            if (root["modules"] is not JsonArray modules) return result;
            foreach (var entry in Modules(modules))
            {
                if (!(entry["external"] is JsonValue e && e.TryGetValue(out bool external) && external)) continue;
                if (Port(entry) is not int port) continue;
                var id = Id(entry) ?? "";
                var name = entry["name"] is JsonValue n && n.TryGetValue(out string? nameText) && !string.IsNullOrWhiteSpace(nameText) ? nameText.Trim() : id.ToUpperInvariant();
                var target = entry["target"] is JsonValue t && t.TryGetValue(out string? targetText) ? targetText : null;
                var tokenSet = entry["token"] is JsonValue k && k.TryGetValue(out string? tokenText) && !string.IsNullOrWhiteSpace(tokenText);
                result.Add(new ServiceListing(id, name, port, target, tokenSet));
            }
            return result;
        }
    }

    // Das Token eines Dienstes, damit ein Ersetzen das Geräte-Token wiederverwenden kann. Nur für den Tausch, nie angezeigt.
    public static string? StoredToken(string file, string id)
    {
        lock (Gate)
        {
            if (!TryLoad(file, out var root, out _)) return null;
            var entry = root["modules"] is JsonArray modules ? FindExternal(modules, id) : null;
            return entry?["token"] is JsonValue value && value.TryGetValue(out string? token) ? token : null;
        }
    }

    // Die Prüfung eines neuen Dienstes: Id frei und gültig, Name da, Port frei und keiner der festen Ports
    private static WriteResult CheckNew(JsonArray modules, string id, string name, int port, int hubPort)
    {
        if (!IsValidId(id)) return new WriteResult(false, "The id is 1 to 40 letters, digits or '-', and not monitor, sys or llm.");
        if (ReservedIds.Contains(id) || Modules(modules).Any(m => Id(m) == id)) return new WriteResult(false, "This id is taken.");
        if (string.IsNullOrWhiteSpace(name)) return new WriteResult(false, "Give the service a name.");
        if (port is < 1 or > 65535) return new WriteResult(false, "The port must be between 1 and 65535.");
        var used = Modules(modules).Select(Port).Where(p => p != null).Select(p => p!.Value).ToHashSet();
        if (port == hubPort || port == StykkerPorts.Llm || port == StykkerPorts.Sys || used.Contains(port))
            return new WriteResult(false, $"Port {port} is already in use in the hub.");
        return new WriteResult(true, "");
    }

    // Eine Id: Buchstaben (klein), Ziffern und "-", 1 bis 40 Zeichen. Die Zentrale macht daraus Kleinbuchstaben.
    private static bool IsValidId(string id) =>
        id.Length is >= 1 and <= 40 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && id == id.ToLowerInvariant();

    private static IEnumerable<JsonObject> Modules(JsonArray modules) => modules.OfType<JsonObject>();

    private static string? Id(JsonObject entry) => entry["id"] is JsonValue v && v.TryGetValue(out string? id) ? id.Trim().ToLowerInvariant() : null;

    private static int? Port(JsonObject entry) => entry["port"] is JsonValue v && v.TryGetValue(out int port) ? port : null;

    private static JsonObject? FindExternal(JsonArray modules, string id)
    {
        var key = id.Trim().ToLowerInvariant();
        return Modules(modules).FirstOrDefault(m => Id(m) == key && m["external"] is JsonValue e && e.TryGetValue(out bool external) && external);
    }

    // Liest die Datei, ändert die Liste der Module und schreibt sie zurück. Nur wenn die Änderung gelingt.
    private static WriteResult Edit(string file, Func<JsonArray, WriteResult> change)
    {
        lock (Gate)
        {
            if (!TryLoad(file, out var root, out var failure)) return failure!;
            if (root["modules"] == null) root["modules"] = new JsonArray();
            if (root["modules"] is not JsonArray modules)
                return new WriteResult(false, "stykker.json: \"modules\" is not a list. Fix it by hand; the hub does not overwrite it.");

            var result = change(modules);
            if (!result.Ok) return result;
            Write(file, root);
            return result;
        }
    }

    // Die Datei als Objekt. Fehlt sie, ein leeres Objekt. Ein Fehler, wenn sie nicht lesbar ist: dann wird nichts geschrieben.
    private static bool TryLoad(string file, out JsonObject root, out WriteResult? failure)
    {
        root = new JsonObject();
        failure = null;
        if (!File.Exists(file)) return true;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(file),
                new JsonNodeOptions(),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (node is not JsonObject obj) throw new JsonException("not an object");
            root = obj;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            failure = new WriteResult(false, "stykker.json cannot be read as JSON. Fix it by hand; the hub does not overwrite it.");
            return false;
        }
    }

    // Erst in eine Datei daneben, dann an die Stelle der alten: ein Absturz mitten im Schreiben lässt die alte Datei stehen.
    private static void Write(string file, JsonObject root)
    {
        var full = Path.GetFullPath(file);
        var folder = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $"{Path.GetFileName(full)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllText(temp, json + "\n", new UTF8Encoding(false));
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
