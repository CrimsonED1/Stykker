using System.Runtime.Versioning;

namespace StykkerCmd.Platform.Linux;

// Liest /proc/self/mountinfo, um den Datenträger eines Pfads zu bestimmen: der längste Einhängepunkt gewinnt.
[SupportedOSPlatform("linux")]
internal static class MountTable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);
    private static IReadOnlyList<string> _mountPoints = [];
    private static DateTime _loadedAt = DateTime.MinValue;
    private static readonly object Gate = new();

    public static string VolumeOf(string canonicalPath)
    {
        string best = "/";
        foreach (var mountPoint in MountPoints())
        {
            if (mountPoint.Length > best.Length && IsUnder(canonicalPath, mountPoint))
                best = mountPoint;
        }
        return best;
    }

    private static IReadOnlyList<string> MountPoints()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _loadedAt > CacheLifetime)
            {
                _mountPoints = Load();
                _loadedAt = DateTime.UtcNow;
            }
            return _mountPoints;
        }
    }

    private static IReadOnlyList<string> Load()
    {
        var points = new List<string>();
        try
        {
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                // Feld 5 (0-basiert: 4) ist der Einhängepunkt; Leerzeichen sind als \040 kodiert.
                var fields = line.Split(' ');
                if (fields.Length > 4)
                    points.Add(Unescape(fields[4]));
            }
        }
        catch (IOException)
        {
            points.Add("/");
        }
        return points;
    }

    private static bool IsUnder(string path, string mountPoint)
    {
        if (mountPoint == "/")
            return true;
        return path == mountPoint || path.StartsWith(mountPoint + "/", StringComparison.Ordinal);
    }

    private static string Unescape(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 3 < text.Length
                && IsOctal(text[i + 1]) && IsOctal(text[i + 2]) && IsOctal(text[i + 3]))
            {
                builder.Append((char)Convert.ToInt32(text.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                builder.Append(text[i]);
            }
        }
        return builder.ToString();
    }

    private static bool IsOctal(char c) => c >= '0' && c <= '7';
}
