using StykkerLlm.Core;

namespace StykkerLlm.Cli.Tui;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Hex(string h) => new(Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
}

// Die Themes der App als Terminal-Farben (dieselben Werte wie ThemeCatalog im Core). Der Hintergrund bleibt der des Terminals:
// Dark für dunkle Terminals, Titan (dunkle Schrift) für helle. „System“ kann das Terminal nicht verraten – dann Dark.
// Frame ist kräftiger als in der App: auf beliebigem Terminal-Hintergrund müssen Rahmen und Trennlinien sichtbar bleiben.
public sealed record TuiTheme(string Name, Rgb Ink, Rgb Muted, Rgb Accent, Rgb Second, Rgb Good, Rgb Warn, Rgb Bad, Rgb Frame)
{
    public static readonly TuiTheme Dark = new("Dark", Rgb.Hex("E8EEFF"), Rgb.Hex("8292B8"), Rgb.Hex("4FE3FF"), Rgb.Hex("A77BFF"),
        Rgb.Hex("6CFFB0"), Rgb.Hex("FFC857"), Rgb.Hex("FF5C7A"), Rgb.Hex("534C8A"));
    public static readonly TuiTheme Titan = new("Spacepunk Titan", Rgb.Hex("1A1E20"), Rgb.Hex("484F52"), Rgb.Hex("BD5017"), Rgb.Hex("1F6E73"),
        Rgb.Hex("256030"), Rgb.Hex("734F08"), Rgb.Hex("962817"), Rgb.Hex("7E8789"));
    public static readonly TuiTheme[] All = { Dark, Titan };

    // Wie ThemeCatalog.Find: frühere Themen und „System“ werden zu Dark
    public static TuiTheme? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var web = ThemeCatalog.Find(name);
        return All.FirstOrDefault(t => t.Name == web.Name) ?? Dark;
    }
}

// Farben in der Tiefe, die das Terminal kann: TrueColor, 256 Farben, 16 Farben oder keine
public sealed class Paint
{
    public TuiTheme Theme { get; set; }
    public ColorMode Mode { get; }

    public Paint(TuiTheme theme, ColorMode mode) { Theme = theme; Mode = mode; }

    public static ColorMode Detect(ColorMode wanted)
    {
        if (wanted != ColorMode.Auto) return wanted;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))) return ColorMode.None;
        var ct = (Environment.GetEnvironmentVariable("COLORTERM") ?? "").ToLowerInvariant();
        if (ct is "truecolor" or "24bit") return ColorMode.TrueColor;
        if (OperatingSystem.IsWindows()) return ColorMode.TrueColor;   // Windows Terminal und conhost (ab Win10 1703) können 24 Bit
        var term = (Environment.GetEnvironmentVariable("TERM") ?? "").ToLowerInvariant();
        if (term == "dumb") return ColorMode.None;
        return term.Contains("256") ? ColorMode.Ansi256 : ColorMode.Ansi16;
    }

    public string Fg(Rgb c) => Mode switch
    {
        ColorMode.TrueColor => $"\u001b[38;2;{c.R};{c.G};{c.B}m",
        ColorMode.Ansi256 => $"\u001b[38;5;{To256(c)}m",
        ColorMode.Ansi16 => $"\u001b[{To16(c)}m",
        _ => "",
    };

    public string Bg(Rgb c) => Mode switch
    {
        ColorMode.TrueColor => $"\u001b[48;2;{c.R};{c.G};{c.B}m",
        ColorMode.Ansi256 => $"\u001b[48;5;{To256(c)}m",
        ColorMode.Ansi16 => $"\u001b[{To16(c) + 10}m",
        _ => "",
    };

    public string C(Rgb c, string text, bool bold = false) =>
        Mode == ColorMode.None ? text : (bold ? "\u001b[1m" : "") + Fg(c) + text + Ansi.Reset;

    public string Ink(string t, bool bold = false) => C(Theme.Ink, t, bold);
    public string Muted(string t) => C(Theme.Muted, t);
    public string Accent(string t, bool bold = false) => C(Theme.Accent, t, bold);
    public string Second(string t) => C(Theme.Second, t);
    public string Good(string t) => C(Theme.Good, t);
    public string Warn(string t) => C(Theme.Warn, t);
    public string Bad(string t) => C(Theme.Bad, t);
    public string Frame(string t) => C(Theme.Frame, t);
    // Auswahl (Vorschlagsliste): Akzent als Hintergrund, dunkle Schrift
    public string Inverse(string t) => Mode == ColorMode.None ? "\u001b[7m" + t + Ansi.Reset
        : Bg(Theme.Accent) + (Mode == ColorMode.TrueColor ? "\u001b[38;2;8;10;16m" : "\u001b[30m") + t + Ansi.Reset;

    // 6×6×6-Würfel oder Graustufen, je nachdem was näher liegt
    internal static int To256(Rgb c)
    {
        static int Cube(int v) => v < 48 ? 0 : v < 115 ? 1 : (v - 35) / 40;
        int r = Cube(c.R), g = Cube(c.G), b = Cube(c.B);
        int[] lv = { 0, 95, 135, 175, 215, 255 };
        int cubeIdx = 16 + 36 * r + 6 * g + b;
        int dc = Sq(lv[r] - c.R) + Sq(lv[g] - c.G) + Sq(lv[b] - c.B);
        int avg = (c.R + c.G + c.B) / 3;
        int gi = avg > 238 ? 23 : Math.Max(0, (avg - 3) / 10);
        int gv = 8 + 10 * gi;
        int dg = Sq(gv - c.R) + Sq(gv - c.G) + Sq(gv - c.B);
        return dg < dc ? 232 + gi : cubeIdx;
    }

    // die 16 Grundfarben (30–37, hell 90–97)
    internal static int To16(Rgb c)
    {
        (int Code, int R, int G, int B)[] basic =
        {
            (30, 0, 0, 0), (31, 205, 49, 49), (32, 13, 188, 121), (33, 229, 229, 16), (34, 36, 114, 200), (35, 188, 63, 188), (36, 17, 168, 205), (37, 229, 229, 229),
            (90, 102, 102, 102), (91, 241, 76, 76), (92, 35, 209, 139), (93, 245, 245, 67), (94, 59, 142, 234), (95, 214, 112, 214), (96, 41, 184, 219), (97, 255, 255, 255),
        };
        return basic.MinBy(x => Sq(x.R - c.R) + Sq(x.G - c.G) + Sq(x.B - c.B)).Code;
    }

    private static int Sq(int v) => v * v;
}
