using Avalonia.Media;

namespace StykkerCmd.UI.Theming;

// Wählt aus einer CSS-Liste wie "\"Segoe UI\", system-ui, sans-serif" die erste installierte Schrift.
// Ohne Treffer gilt die Standardschrift von Avalonia. Schriften werden nicht mitgeliefert (PLAN.md, Abschnitt 4).
public static class FontStack
{
    private static readonly HashSet<string> GenericFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "system-ui", "sans-serif", "monospace", "ui-monospace",
    };

    public static FontFamily Pick(string cssFamilies, IEnumerable<FontFamily> installed)
    {
        var installedNames = installed.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in cssFamilies.Split(','))
        {
            var name = candidate.Trim().Trim('"', '\'');
            if (name.Length > 0 && !GenericFamilies.Contains(name) && installedNames.Contains(name))
                return new FontFamily(name);
        }

        return FontFamily.Default;
    }
}
