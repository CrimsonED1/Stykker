namespace StykkerLlm.Cli;

public enum ColorMode { Auto, TrueColor, Ansi256, Ansi16, None }

// stykker [status|web|stop|bugreport <text>|help|version] – keine Optionen. Die Anzeige liest den Server; gesteuert wird
// im Web. Nur im Debug-Build gibt es Entwickler-Schalter (anderer Datenordner/Port, Simulator, Bild ohne Terminal);
// sie landen nie im Release.
public sealed class CliArgs
{
    public string Command = "";
    public List<string> Words = new();
    public string? DataDir;
    public int Port = 17400;
    public bool Sim;
    public string? Snapshot;
    public string Keys = "";
    public int SnapWidth = 120, SnapHeight = 34;

    public AppPathsHolder Paths => new(DataDir);

    public static (CliArgs Args, string? Error) Parse(IReadOnlyList<string> argv)
    {
        var a = new CliArgs();
        for (int i = 0; i < argv.Count; i++)
        {
            var t = argv[i];
            if (!t.StartsWith("--", StringComparison.Ordinal) || t.Length == 2)
            {
                if (a.Command.Length == 0) a.Command = t.ToLowerInvariant();
                else a.Words.Add(t);
                continue;
            }
#if DEBUG
            string? Next() => i + 1 < argv.Count ? argv[++i] : null;
            switch (t)
            {
                case "--data-dir": a.DataDir = Next(); if (string.IsNullOrWhiteSpace(a.DataDir)) return (a, "--data-dir needs a folder"); continue;
                case "--port": if (!int.TryParse(Next(), out a.Port) || a.Port is < 1 or > 65535) return (a, "--port needs a number"); continue;
                case "--sim": a.Sim = true; continue;
                case "--snapshot": a.Snapshot = Next(); if (string.IsNullOrWhiteSpace(a.Snapshot)) return (a, "--snapshot needs a file"); continue;
                case "--keys": a.Keys = Next() ?? ""; continue;
                case "--size":
                    var sz = (Next() ?? "").Split('x');
                    if (sz.Length != 2 || !int.TryParse(sz[0], out a.SnapWidth) || !int.TryParse(sz[1], out a.SnapHeight) || a.SnapWidth < 40 || a.SnapHeight < 12)
                        return (a, "--size needs WIDTHxHEIGHT, at least 40x12");
                    continue;
            }
#endif
            return (a, $"stykker has no options ('{t}'). Everything else is in the web interface: stykker web");
        }
        return (a, null);
    }
}

// Datenordner: der Standard, im Debug-Build auch ein anderer (--data-dir)
public readonly record struct AppPathsHolder(string? DataDir)
{
    public StykkerLlm.Core.AppPaths Get() =>
        DataDir != null ? new StykkerLlm.Core.AppPaths(Path.GetFullPath(DataDir)) : StykkerLlm.Core.AppPaths.Default();
}
