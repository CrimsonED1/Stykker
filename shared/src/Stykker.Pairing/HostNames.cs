using System.Net;
using System.Text.Json;

namespace Stykker.Pairing;

// Welche Namen ein Host-Kopf haben darf (DNS-Rebinding). Eine Regel für HUD, SYS, die Weiterleitung der Zentrale und
// StykkerLLM. Dazu kommen Zusatznamen aus stykker.json ("hostNames"), etwa der Name eines PCs im Tailscale-Netz: genau
// dieser Name, ohne Platzhalter. Die Klasse hängt an nichts außer dem Framework, damit LLM sie ohne Stykker.Shared hat.
public static class HostNames
{
    // Das Feld in der obersten Ebene von stykker.json (neben "modules")
    public const string ExtraField = "hostNames";
    public const int MaxLength = 253;

    // %APPDATA%\Stykker\stykker.json: die Datei der Zentrale (unter Linux ~/.config/Stykker/stykker.json)
    public static string DefaultFile() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stykker", "stykker.json");

    // Der Host-Kopf der Anfrage muss der Name sein, unter dem dieser PC erreichbar ist: eine IP-Adresse, localhost, der
    // Rechnername oder der Rechnername mit der DNS-Endung dieses PCs (domainName, leer wenn keine). Dazu ein Zusatzname
    // aus extraNames, genau so geschrieben (ohne Groß-/Kleinschreibung, mit oder ohne Port). Jeder andere Name wird
    // abgewiesen. Der Grund ist DNS-Rebinding: eine fremde Seite lässt ihren Namen auf 127.0.0.1 zeigen, dann schickt der
    // Browser diesen Namen als Host und als Origin, und beides stimmt überein. Ein Name wie <Rechnername>.<fremd> ist darum
    // nicht erlaubt: die Domain dieses Namens kann der Angreifer selbst besitzen.
    public static bool Allowed(string? hostHeader, string machineName, string? domainName, IReadOnlyCollection<string> extraNames)
    {
        var name = NameOf((hostHeader ?? "").Trim());
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IsIpLiteral(name)) return true;
        // Ein Zusatzname gilt nur, wenn er selbst gültig ist: ein Platzhalter wie "*.example" trifft nie
        if (extraNames.Any(extra => IsValidName(extra) && name.Equals(extra, StringComparison.OrdinalIgnoreCase))) return true;
        if (machineName.Length == 0) return false;
        if (name.Equals(machineName, StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrEmpty(domainName) && name.Equals($"{machineName}.{domainName}", StringComparison.OrdinalIgnoreCase);
    }

    // Die Zusatznamen aus der Datei (Feld "hostNames" in der obersten Ebene). Ohne Datei, ohne Feld oder mit einem
    // kaputten JSON: leere Liste.
    public static IReadOnlyList<string> LoadExtra(string? file)
    {
        if (string.IsNullOrEmpty(file)) return [];
        try
        {
            if (!File.Exists(file)) return [];
            using var document = JsonDocument.Parse(File.ReadAllText(file),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return ExtraFrom(document.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    // Dasselbe aus der schon gelesenen Wurzel des JSON. Ungültige Einträge fallen still weg; doppelte auch.
    public static IReadOnlyList<string> ExtraFrom(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(ExtraField, out var list)
            || list.ValueKind != JsonValueKind.Array) return [];

        var names = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var name = item.GetString()?.Trim() ?? "";
            if (IsValidName(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        return names;
    }

    // Ein Name aus Buchstaben, Ziffern, "-" und "." (ohne leere Teile), höchstens 253 Zeichen. Kein IP-Literal, kein localhost:
    // die sind ohnehin erlaubt, und ein Zusatzname soll nur Namen erweitern.
    public static bool IsValidName(string name)
    {
        if (name.Length is 0 or > MaxLength) return false;
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IsIpLiteral(name)) return false;
        if (name[0] == '.' || name[^1] == '.' || name.Contains("..", StringComparison.Ordinal)) return false;
        return name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.');
    }

    // Der Name ohne Port: "pc:8091" wird "pc", "[::1]:8091" wird "::1". null, wenn der Kopf nicht so aufgebaut ist.
    private static string? NameOf(string value)
    {
        if (value.Length == 0) return null;
        if (value[0] == '[')
        {
            int close = value.IndexOf(']');
            if (close < 0 || !PortSuffix(value[(close + 1)..])) return null;
            return value[1..close];
        }
        // Ein IPv6-Name ohne Klammern ist in einem Host-Kopf nicht gültig
        if (value.Count(c => c == ':') > 1) return null;
        int colon = value.IndexOf(':');
        if (colon < 0) return value;
        return PortSuffix(value[colon..]) ? value[..colon] : null;
    }

    // "" oder ":" gefolgt von einer Portnummer 1 bis 65535
    private static bool PortSuffix(string rest)
    {
        if (rest.Length == 0) return true;
        if (rest[0] != ':') return false;
        var digits = rest[1..];
        return digits.Length is >= 1 and <= 5 && digits.All(char.IsAsciiDigit)
            && int.TryParse(digits, out var port) && port is > 0 and <= 65535;
    }

    // Eine IPv4-Adresse (nur Ziffern und Punkte) oder eine IPv6-Adresse (mit Doppelpunkt)
    private static bool IsIpLiteral(string name) =>
        (name.Contains(':') || name.All(c => char.IsAsciiDigit(c) || c == '.')) && IPAddress.TryParse(name, out _);
}
