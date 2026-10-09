using System.Text.RegularExpressions;
using StykkerCmd.Core.Model;

namespace StykkerCmd.Core.Listing;

// Filter pro Panel. Ohne Platzhalter zählt jeder Teiltreffer; mit "*" oder "?" muss das Muster den ganzen Namen treffen.
public static class EntryFilter
{
    public static Func<string, bool> CreateMatcher(string pattern)
    {
        var text = pattern.Trim();
        if (text.Length == 0)
            return _ => true;

        if (text.IndexOf('*') < 0 && text.IndexOf('?') < 0)
            return name => name.Contains(text, StringComparison.OrdinalIgnoreCase);

        // Regex einmal pro Filter bauen, nicht pro Eintrag.
        var regex = new Regex(
            "^" + Regex.Escape(text).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return name => regex.IsMatch(name);
    }

    public static IEnumerable<FileEntry> Apply(IEnumerable<FileEntry> entries, string pattern)
    {
        var matches = CreateMatcher(pattern);
        return entries.Where(e => matches(e.Name));
    }
}
