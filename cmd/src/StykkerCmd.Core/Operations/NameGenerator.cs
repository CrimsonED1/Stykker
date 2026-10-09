using StykkerCmd.Core.Abstractions;

namespace StykkerCmd.Core.Operations;

// Freie Namen für "Umbenennen" im Konfliktfall: "bericht.txt" wird zu "bericht (2).txt".
public static class NameGenerator
{
    public static string FreeName(IFileSystem fs, string target, Func<string, bool> exists)
    {
        var name = fs.NameOf(target);
        var parent = target[..^(name.Length + 1)]; // target = parent + Trenner + name

        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : string.Empty;

        for (var n = 2; ; n++)
        {
            var candidate = fs.Combine(parent, $"{stem} ({n}){extension}");
            if (!exists(candidate))
                return candidate;
        }
    }
}
