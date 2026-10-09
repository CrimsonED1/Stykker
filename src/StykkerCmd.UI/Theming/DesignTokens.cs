using System.Text.Json;
using Avalonia.Media;

namespace StykkerCmd.UI.Theming;

// Ein Thema mit aufgelösten Farben. Aliase wie "{card-2}" sind bereits durch die echten Werte ersetzt.
public sealed record ThemePalette(string Id, string Name, IReadOnlyDictionary<string, Color> Colors);

// Der Teil von tokens.json, den die Oberfläche braucht.
public sealed record DesignDocument(IReadOnlyList<ThemePalette> Themes, string SansFamilies, string MonoFamilies);

// Liest die eingebettete Kopie von tokens.json (Design-System der Stykker-Familie).
public static class DesignTokens
{
    private const string ResourceName = "StykkerCmd.UI.Themes.tokens.json";

    public static DesignDocument LoadEmbedded()
    {
        using var stream = typeof(DesignTokens).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Eingebettete Ressource fehlt: {ResourceName}");
        return Load(stream);
    }

    public static DesignDocument Load(Stream json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var colorRoot = root.GetProperty("color");
        var themeInfos = colorRoot.GetProperty("themes").EnumerateArray()
            .Select(t => (Id: t.GetProperty("id").GetString()!, Name: t.GetProperty("name").GetString()!))
            .ToList();

        // Rohwerte je Thema: Token-Name -> "#rrggbb", "#rrggbbaa" oder Alias "{name}".
        var raw = themeInfos.ToDictionary(t => t.Id, _ => new Dictionary<string, string>());
        foreach (var token in colorRoot.GetProperty("tokens").EnumerateArray())
        {
            var name = token.GetProperty("name").GetString()!;
            var values = token.GetProperty("value");
            foreach (var (themeId, rawValues) in raw)
                rawValues[name] = values.GetProperty(themeId).GetString()!;
        }

        var themes = themeInfos
            .Select(t => new ThemePalette(t.Id, t.Name, ResolveAll(raw[t.Id])))
            .ToList();

        var families = root.GetProperty("type").GetProperty("families");
        return new DesignDocument(
            themes,
            families.GetProperty("sans").GetString()!,
            families.GetProperty("mono").GetString()!);
    }

    private static IReadOnlyDictionary<string, Color> ResolveAll(Dictionary<string, string> raw)
    {
        var colors = new Dictionary<string, Color>();
        foreach (var name in raw.Keys)
            colors[name] = Resolve(raw, name, depth: 0);
        return colors;
    }

    private static Color Resolve(Dictionary<string, string> raw, string name, int depth)
    {
        if (depth > 8)
            throw new InvalidDataException($"Alias-Zyklus bei '{name}'.");

        var value = raw[name];
        if (value.StartsWith('{') && value.EndsWith('}'))
            return Resolve(raw, value[1..^1], depth + 1);

        return ParseCssHex(value);
    }

    // CSS schreibt #rrggbbaa, Avalonia erwartet #aarrggbb.
    private static Color ParseCssHex(string css)
    {
        var digits = css.TrimStart('#');
        if (digits.Length is not (6 or 8))
            throw new FormatException($"Kein Hex-Farbwert: '{css}'.");

        var alpha = digits.Length == 8 ? digits[6..] : "ff";
        return Color.Parse("#" + alpha + digits[..6]);
    }
}
