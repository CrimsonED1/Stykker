namespace StykkerLlm.Cli;

public enum ColorMode { Auto, TrueColor, Ansi256, Ansi16, None }

// Argumente: erst der Befehl (optional), dann seine Wörter und Optionen in beliebiger Reihenfolge.
//   stykker status --json --data-dir D:\x      stykker stop 8081 --yes      stykker --sim status --watch 2
public sealed class CliArgs
{
    public string Command = "";
    public List<string> Words = new();
    public string? DataDir, Theme;
    public string? Suite, Only, Md;          // eval: Suite-Datei oder -Name, Kategorien (Komma), Markdown-Datei
    public string? Snapshot, Keys;          // interaktive Oberfläche ohne Terminal: Tasten-Skript abspielen, letztes Bild in Datei
    public int SnapWidth = 120, SnapHeight = 36;
    public bool Json, Yes, Wait, Sim, Help, Version, Ascii, NoBrowser, Local;
    public int? WatchSeconds;
    public int IntervalMs = 1000;
    public int Repeat = 1;                  // eval: Wiederholungen mit wechselndem Seed
    public int Port = ServerCommand.DefaultPort;   // Port des Servers
    public ColorMode Color = ColorMode.Auto;

    // Fehlertext statt Ausnahme: null = alles gelesen
    public static (CliArgs Args, string? Error) Parse(IReadOnlyList<string> argv)
    {
        var a = new CliArgs();
        for (int i = 0; i < argv.Count; i++)
        {
            string arg = argv[i];
            string? Next() => i + 1 < argv.Count ? argv[++i] : null;
            switch (arg)
            {
                case "-h": case "--help": case "-?": a.Help = true; break;
                case "--version": a.Version = true; break;
                case "--json": a.Json = true; break;
                case "-y": case "--yes": a.Yes = true; break;
                case "--wait": a.Wait = true; break;
                case "--sim": a.Sim = true; break;
                case "--no-browser": a.NoBrowser = true; break;
                case "--port":
                    if (!int.TryParse(Next(), out a.Port) || a.Port is < 1024 or > 65535) return (a, "--port needs a number from 1024 to 65535");
                    break;
                case "--local": a.Local = true; break;   // nicht den Server fragen, selbst messen
                case "--ascii": a.Ascii = true; break;
                case "--no-color": a.Color = ColorMode.None; break;
                case "--data-dir":
                    a.DataDir = Next();
                    if (string.IsNullOrWhiteSpace(a.DataDir)) return (a, "--data-dir needs a folder");
                    break;
                case "--suite":
                    a.Suite = Next();
                    if (string.IsNullOrWhiteSpace(a.Suite)) return (a, "--suite needs a file or name");
                    break;
                case "--only":
                    a.Only = Next();
                    if (string.IsNullOrWhiteSpace(a.Only)) return (a, "--only needs categories, e.g. coding,tools");
                    break;
                case "--repeat":
                    if (!int.TryParse(Next(), out a.Repeat) || a.Repeat < 1 || a.Repeat > 20) return (a, "--repeat needs a number from 1 to 20");
                    break;
                case "--md":
                    a.Md = Next();
                    if (string.IsNullOrWhiteSpace(a.Md)) return (a, "--md needs a file");
                    break;
                case "--theme":
                    a.Theme = Next();
                    if (string.IsNullOrWhiteSpace(a.Theme)) return (a, "--theme needs a name");
                    break;
                case "--interval":
                    if (!int.TryParse(Next(), out a.IntervalMs) || a.IntervalMs < 250) return (a, "--interval needs milliseconds (>= 250)");
                    break;
                case "--snapshot":
                    a.Snapshot = Next();
                    if (string.IsNullOrWhiteSpace(a.Snapshot)) return (a, "--snapshot needs a file");
                    break;
                case "--keys":
                    a.Keys = Next() ?? "";
                    break;
                case "--size":
                    var sz = (Next() ?? "").Split('x');
                    if (sz.Length != 2 || !int.TryParse(sz[0], out a.SnapWidth) || !int.TryParse(sz[1], out a.SnapHeight) || a.SnapWidth < 40 || a.SnapHeight < 12)
                        return (a, "--size needs WIDTHxHEIGHT, at least 40x12");
                    break;
                case "--watch":
                    // Zahl optional: "--watch" allein = jede Sekunde
                    if (i + 1 < argv.Count && int.TryParse(argv[i + 1], out int w)) { i++; a.WatchSeconds = w; }
                    else a.WatchSeconds = 1;
                    if (a.WatchSeconds < 1) return (a, "--watch needs seconds (>= 1)");
                    break;
                case "--color":
                    var c = Next();
                    a.Color = c switch
                    {
                        "auto" => ColorMode.Auto, "truecolor" or "24bit" => ColorMode.TrueColor, "256" => ColorMode.Ansi256,
                        "16" => ColorMode.Ansi16, "none" or "never" => ColorMode.None, _ => (ColorMode)(-1),
                    };
                    if ((int)a.Color < 0) return (a, "--color needs auto, truecolor, 256, 16 or none");
                    break;
                default:
                    if (arg.StartsWith('-') && arg.Length > 1 && !int.TryParse(arg, out _)) return (a, $"unknown option {arg}");
                    if (a.Command.Length == 0) a.Command = arg.ToLowerInvariant();
                    else a.Words.Add(arg);
                    break;
            }
        }
        return (a, null);
    }
}
