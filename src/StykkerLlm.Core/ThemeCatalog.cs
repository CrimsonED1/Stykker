using System.Globalization;

namespace StykkerLlm.Core;

// Die fünf Themen des Fensters als Farbwerte für alle Oberflächen: das Web setzt daraus seine CSS-Variablen
// (ThemeInfo.CssVariables in die Kopfzeile jeder Seite), die TUI kennt dieselben Namen (siehe Tui\Palette).
// Das Fenster zeichnet weiter mit seiner eigenen, genaueren Fassung (Theme.cs mit Verläufen, Rändern und
// Hintergründen); Name, Reihenfolge und Farben müssen mit Theme.cs übereinstimmen – WebThemeTests wacht darüber.
// Farben mit Deckkraft stehen als #rrggbbaa (im Fenster ARGB), damit das Web dieselben Flächen und Ränder bekommt.
public sealed record ThemeInfo
{
    public string Name { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Kind { get; init; } = "flat";      // BgKind des Fensters: space | cyber | flat | deep | phosphor
    public int Radius { get; init; } = 12;           // Ecken der Karten wie im Fenster
    public string Bg { get; init; } = "";            // Fensterfarbe
    public string BgTop { get; init; } = "";         // Verlauf oben
    public string BgBottom { get; init; } = "";      // … und unten
    public string Card { get; init; } = "";          // Kartenfläche ohne Verlauf
    public string CardTop { get; init; } = "";       // Kartenverlauf oben
    public string CardBottom { get; init; } = "";    // … und unten
    public string Line { get; init; } = "";          // Haarlinien (Tabellen, Rahmen)
    public string Border { get; init; } = "";        // Kartenrand (Fenster: Border1)
    public string TopLine { get; init; } = "";       // heller Strich an der Oberkante der Karte
    public string Track { get; init; } = "";         // Bahn der Balken
    public string Ink { get; init; } = "";           // Werte, Haupttext
    public string Muted { get; init; } = "";         // Beschriftungen
    public string Accent { get; init; } = "";        // Aktion
    public string Second { get; init; } = "";
    public string Good { get; init; } = "";          // läuft / fertig
    public string Warn { get; init; } = "";          // lädt / knapp
    public string Bad { get; init; } = "";           // Fehler / Stop

    public static ThemeInfo CyberGrid => new()
    {
        Name = "Cyber Grid", Slug = "cyber-grid", Kind = "cyber", Radius = 8,
        Bg = "#030608", BgTop = "#030608", BgBottom = "#04090c",
        Card = "#0a1620", CardTop = "#050a0dd1", CardBottom = "#040809d6",
        Line = "#16303d", Border = "#1fb5d48c", TopLine = "#bff6ff96", Track = "#2bb8d622",
        Ink = "#e8fbff", Muted = "#5f8792", Accent = "#4fe3ff", Second = "#ff5c74",
        Good = "#5bffb0", Warn = "#ffc857", Bad = "#ff5c74",
    };

    public static ThemeInfo SpaceGlass => new()
    {
        Name = "Space Glass", Slug = "space-glass", Kind = "space", Radius = 12,
        Bg = "#060916", BgTop = "#0a1026", BgBottom = "#0e0820",
        Card = "#111a33", CardTop = "#96b4ff22", CardBottom = "#0e785a0e",
        Line = "#24325a", Border = "#4fe3ff78", TopLine = "#ffffff3c", Track = "#ffffff1c",
        Ink = "#e8eeff", Muted = "#8292b8", Accent = "#4fe3ff", Second = "#a77bff",
        Good = "#6cffb0", Warn = "#ffc857", Bad = "#ff5c7a",
    };

    public static ThemeInfo Obsidian => new()
    {
        Name = "Obsidian", Slug = "obsidian", Kind = "flat", Radius = 8,
        Bg = "#0a0c0f", BgTop = "#0a0c0f", BgBottom = "#0a0c0f",
        Card = "#14161b", CardTop = "#12151af5", CardBottom = "#12151af5",
        Line = "#242932", Border = "#22272e", TopLine = "#ffffff0e", Track = "#ffffff14",
        Ink = "#e6eaf0", Muted = "#7c8696", Accent = "#4fd1e8", Second = "#7c8696",
        Good = "#5cdca0", Warn = "#e8b44a", Bad = "#e5596b",
    };

    public static ThemeInfo DeepSea => new()
    {
        Name = "Deep Sea", Slug = "deep-sea", Kind = "deep", Radius = 12,
        Bg = "#05070d", BgTop = "#070b15", BgBottom = "#05070d",
        Card = "#0c1320", CardTop = "#0c1320ee", CardBottom = "#0a0f19ee",
        Line = "#1b2b40", Border = "#223350", TopLine = "#aad2ff1a", Track = "#96beff18",
        Ink = "#dce9fa", Muted = "#6f86a6", Accent = "#6cb8ff", Second = "#7c9cd6",
        Good = "#6ce8c0", Warn = "#ffc16c", Bad = "#ff6f8a",
    };

    public static ThemeInfo Phosphor => new()
    {
        Name = "Phosphor", Slug = "phosphor", Kind = "phosphor", Radius = 3,
        Bg = "#040605", BgTop = "#040605", BgBottom = "#050906",
        Card = "#08100a", CardTop = "#08100af2", CardBottom = "#08100af2",
        Line = "#12301c", Border = "#173020", TopLine = "#00000000", Track = "#5cff9a18",
        Ink = "#b8ffd0", Muted = "#4c8a62", Accent = "#5cff9a", Second = "#2fbf6e",
        Good = "#5cff9a", Warn = "#ffb000", Bad = "#ff5c5c",
    };

    // Phosphor beschriftet klein und schreibt in der Konsolenschrift (Theme.LowerLabels im Fenster)
    public bool LowerLabels => Kind == "phosphor";

    // Die acht Farben des gestapelten Speicherbalkens, ein Abschnitt je Programm (MainForm.MemBar im Fenster)
    public string[] MemPalette() => new[]
    {
        Accent, Second, Good, Warn,
        Mix(Accent, Bad, 0.5), Mix(Second, Good, 0.5), Mix(Warn, Bad, 0.4), Mix(Accent, Ink, 0.4),
    };

    // Die CSS-Variablen der Weboberfläche. Ohne Anführungszeichen, damit Razor den Text nicht als &quot; escaped.
    public string CssVariables()
    {
        var vars = new List<string>
        {
            "--bg:" + Bg, "--bg-top:" + BgTop, "--bg-bottom:" + BgBottom,
            "--card:" + Card, "--card-top:" + CardTop, "--card-bottom:" + CardBottom,
            "--line:" + Line, "--card-border:" + Border, "--top-line:" + TopLine, "--track:" + Track,
            "--radius:" + Radius.ToString(CultureInfo.InvariantCulture) + "px",
            "--ink:" + Ink, "--muted:" + Muted, "--acc:" + Accent, "--second:" + Second,
            "--good:" + Good, "--warn:" + Warn, "--bad:" + Bad,
            "--bg-rgb:" + Rgb(Bg), "--ink-rgb:" + Rgb(Ink), "--muted-rgb:" + Rgb(Muted),
            "--acc-rgb:" + Rgb(Accent), "--second-rgb:" + Rgb(Second), "--good-rgb:" + Rgb(Good),
            "--warn-rgb:" + Rgb(Warn), "--bad-rgb:" + Rgb(Bad),
            "--font:" + (LowerLabels ? MonoStack : UiStack), "--font-num:" + MonoStack,
        };
        return ":root{" + string.Join(';', vars) + "}";
    }

    private const string UiStack = "Segoe UI,system-ui,sans-serif";
    private const string MonoStack = "Cascadia Mono,Consolas,monospace";

    // "108 184 255" für rgb(var(--acc-rgb)/.12) im Stylesheet. Mit Leerzeichen, nicht mit Kommas: rgb(108,184,255/.12)
    // ist ungültiges CSS, der Browser verwirft dann die ganze Eigenschaft (z. B. Hintergründe, Ränder, Streifen)
    public static string Rgb(string hex)
    {
        var (r, g, b) = Parts(hex);
        return string.Create(CultureInfo.InvariantCulture, $"{r} {g} {b}");
    }

    // Theme.Lerp des Fensters: zwei Farben mischen, t ist der Anteil der zweiten
    public static string Mix(string a, string b, double t)
    {
        var (r1, g1, b1) = Parts(a);
        var (r2, g2, b2) = Parts(b);
        return "#" + string.Create(CultureInfo.InvariantCulture, $"{Channel(r1, r2, t):x2}{Channel(g1, g2, t):x2}{Channel(b1, b2, t):x2}");
    }

    private static int Channel(int from, int to, double t) => (int)Math.Round(Math.Clamp(from + (to - from) * t, 0, 255));

    private static (int R, int G, int B) Parts(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length >= 6) h = h[..6];
        var v = Convert.ToInt32(h, 16);
        return ((v >> 16) & 255, (v >> 8) & 255, v & 255);
    }
}

public static class ThemeCatalog
{
    public static readonly ThemeInfo[] All =
    {
        ThemeInfo.CyberGrid, ThemeInfo.SpaceGlass, ThemeInfo.Obsidian, ThemeInfo.DeepSea, ThemeInfo.Phosphor,
    };

    public static string[] Names => All.Select(t => t.Name).ToArray();

    public static ThemeInfo Default => All[3];   // Deep Sea (Vorgabe des Fensters)

    // Name wie im Fenster: ohne Rücksicht auf Groß-/Kleinschreibung, Bindestrich und Leerzeichen
    // ("cyber grid", "CYBER-GRID" …). Die alten deutschen Namen bleiben gültig.
    public static ThemeInfo Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Default;
        var n = Norm(name);
        if (n == "tiefsee") n = "deepsea";
        else if (n == "weltraumglas") n = "spaceglass";
        return All.FirstOrDefault(t => Norm(t.Name) == n || Norm(t.Slug) == n) ?? Default;
    }

    private static string Norm(string s) => new string(s.Where(char.IsLetter).ToArray()).ToLowerInvariant();
}