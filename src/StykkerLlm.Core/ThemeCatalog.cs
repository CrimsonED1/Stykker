using System.Globalization;

namespace StykkerLlm.Core;

// Die Themen aller Oberflächen (docs/plan-ui-redesign.md, U1): Dark und Spacepunk Titan, dazu „System“ (hell → Titan,
// dunkel → Dark). Das Web setzt daraus seine CSS-Variablen (ThemeCatalog.Css in den Kopf jeder Seite), die TUI kennt
// dieselben Namen (siehe Tui\Palette). Farben mit Deckkraft stehen als #rrggbbaa.
public sealed record ThemeInfo
{
    public string Name { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Kind { get; init; } = "space";     // Hintergrund und Formen: space (Dark) | titan
    public bool Light { get; init; }                 // helles Thema (color-scheme, Schatten, Leuchten nur in Display-Fenstern)
    public int Radius { get; init; } = 12;           // Ecken der Karten
    public string Bg { get; init; } = "";            // Seitenfarbe
    public string BgTop { get; init; } = "";         // Verlauf oben
    public string BgBottom { get; init; } = "";      // … und unten
    public string Card { get; init; } = "";          // Kartenfläche
    public string CardTop { get; init; } = "";       // Kartenverlauf oben
    public string CardBottom { get; init; } = "";    // … und unten
    public string Card2 { get; init; } = "";         // vertiefte Fläche (Felder, Tabellenköpfe, Display-Rahmen)
    public string Line { get; init; } = "";          // Haarlinien (Tabellen, Rahmen)
    public string Border { get; init; } = "";        // Kartenrand (Titan: die Fuge)
    public string TopLine { get; init; } = "";       // heller Strich an der Oberkante der Karte
    public string Track { get; init; } = "";         // Bahn der Balken
    public string Ink { get; init; } = "";           // Werte, Haupttext
    public string Muted { get; init; } = "";         // Beschriftungen
    public string Accent { get; init; } = "";        // Aktion, Text in Akzentfarbe
    public string Signal { get; init; } = "";        // kräftiger Akzent für Flächen (Titan: Signal-Orange), sonst = Accent
    public string Second { get; init; } = "";
    public string Good { get; init; } = "";          // läuft / fertig
    public string Warn { get; init; } = "";          // lädt / knapp
    public string Bad { get; init; } = "";           // Fehler / Stop
    // Backends und Hosts: je eine Farbe für Streifen, Punkte und Zeichen
    public string Llama { get; init; } = "";
    public string Ollama { get; init; } = "";
    public string LmStudio { get; init; } = "";
    public string Vllm { get; init; } = "";
    public string Host { get; init; } = "";
    // Zustände eines Slots/Servers: wartet, liest Prompt, schreibt, lädt, offline (immer zusammen mit einem Symbol)
    public string StIdle { get; init; } = "";
    public string StRead { get; init; } = "";
    public string StGen { get; init; } = "";
    public string StLoad { get; init; } = "";
    public string StOff { get; init; } = "";
    // Display-Fenster (Titan: dunkle Einsätze für tokens/s und Kurve; Dark: durchsichtig)
    public string Display { get; init; } = "";
    public string DisplayInk { get; init; } = "";
    public string DisplayUnit { get; init; } = "";
    public double Glow { get; init; } = 1;           // Leuchten (0 = keins): Text, Punkte, Balken, Kurve
    public int Sheen { get; init; }                  // Glanz oben auf der Karte (0–255)

    public static ThemeInfo Dark => new()
    {
        Name = "Dark", Slug = "dark", Kind = "space", Radius = 12, Glow = 1.25, Sheen = 20,
        Bg = "#060916", BgTop = "#0e1430", BgBottom = "#060916",
        Card = "#111a33", CardTop = "#96b4ff22", CardBottom = "#0e785a0e", Card2 = "#0e0f26",
        Line = "#24325a", Border = "#4fe3ff40", TopLine = "#ffffff3c", Track = "#ffffff1c",
        Ink = "#e8eeff", Muted = "#8292b8", Accent = "#4fe3ff", Signal = "#4fe3ff", Second = "#a77bff",
        Good = "#6cffb0", Warn = "#ffc857", Bad = "#ff5c7a",
        Llama = "#4fe3ff", Ollama = "#b18cff", LmStudio = "#ffb35c", Vllm = "#ff6fb5", Host = "#9aa8d6",
        StIdle = "#8292b8", StRead = "#ffc857", StGen = "#6cffb0", StLoad = "#7ab8ff", StOff = "#ff5c7a",
        Display = "#00000000", DisplayInk = "#e8eeff", DisplayUnit = "#8292b8",
    };

    // Raumschiff-Innenraum bei Arbeitslicht: mattes Metallgrau, Signal-Orange, gedeckte Lackfarben. Text- und
    // Symbolfarben sind auf AA gegen das Paneel gerechnet (docs/design/proposals.de.md, „Light-Kontraste“).
    public static ThemeInfo Titan => new()
    {
        Name = "Spacepunk Titan", Slug = "titan", Kind = "titan", Light = true, Radius = 2, Glow = 1, Sheen = 0,
        Bg = "#d9dcdb", BgTop = "#d9dcdb", BgBottom = "#d9dcdb",
        Card = "#cbd0cf", CardTop = "#cbd0cf", CardBottom = "#cbd0cf", Card2 = "#bfc5c4",
        Line = "#a9b0b1", Border = "#7e8789", TopLine = "#00000000", Track = "#7e878940",
        Ink = "#1a1e20", Muted = "#484f52", Accent = "#bd5017", Signal = "#e0601f", Second = "#4a5a6a",
        Good = "#256030", Warn = "#734f08", Bad = "#962817",
        Llama = "#c2531a", Ollama = "#1f6e73", LmStudio = "#a67c1e", Vllm = "#9a3b2e", Host = "#4a5a6a",
        StIdle = "#4f5659", StRead = "#225684", StGen = "#256030", StLoad = "#225684", StOff = "#962817",
        Display = "#141819", DisplayInk = "#7cff9e", DisplayUnit = "#ffb347",
    };

    // Die CSS-Variablen dieses Themas (ohne Selektor). Ohne Anführungszeichen, damit Razor nichts escaped.
    public string CssVariables()
    {
        var vars = new List<string>
        {
            "color-scheme:" + (Light ? "light" : "dark"),
            "--bg:" + Bg, "--bg-top:" + BgTop, "--bg-bottom:" + BgBottom,
            "--card:" + Card, "--card-top:" + CardTop, "--card-bottom:" + CardBottom, "--card2:" + Card2,
            "--line:" + Line, "--card-border:" + Border, "--top-line:" + TopLine, "--track:" + Track,
            "--radius:" + Radius.ToString(CultureInfo.InvariantCulture) + "px",
            "--ink:" + Ink, "--muted:" + Muted, "--acc:" + Accent, "--signal:" + Signal, "--second:" + Second,
            "--good:" + Good, "--warn:" + Warn, "--bad:" + Bad,
            "--b-llama:" + Llama, "--b-ollama:" + Ollama, "--b-lms:" + LmStudio, "--b-vllm:" + Vllm, "--b-host:" + Host,
            "--st-idle:" + StIdle, "--st-read:" + StRead, "--st-gen:" + StGen, "--st-load:" + StLoad, "--st-off:" + StOff,
            "--disp:" + Display, "--disp-ink:" + DisplayInk, "--disp-unit:" + DisplayUnit,
            "--bg-rgb:" + Rgb(Bg), "--ink-rgb:" + Rgb(Ink), "--muted-rgb:" + Rgb(Muted),
            "--acc-rgb:" + Rgb(Accent), "--second-rgb:" + Rgb(Second), "--good-rgb:" + Rgb(Good),
            "--warn-rgb:" + Rgb(Warn), "--bad-rgb:" + Rgb(Bad), "--disp-ink-rgb:" + Rgb(DisplayInk),
            "--font:" + UiStack, "--font-num:" + MonoStack,
            "--glow:" + Glow.ToString("0.##", CultureInfo.InvariantCulture),
            "--sheen:" + (Sheen / 255.0).ToString("0.###", CultureInfo.InvariantCulture),
        };
        return string.Join(';', vars);
    }

    private const string UiStack = "Segoe UI Variable Text,Segoe UI,system-ui,sans-serif";
    private const string MonoStack = "Cascadia Mono,Consolas,monospace";

    // "108 184 255" für rgb(var(--acc-rgb)/.12) im Stylesheet. Mit Leerzeichen, nicht mit Kommas: rgb(108,184,255/.12)
    // ist ungültiges CSS, der Browser verwirft dann die ganze Eigenschaft
    public static string Rgb(string hex)
    {
        var (r, g, b) = Parts(hex);
        return string.Create(CultureInfo.InvariantCulture, $"{r} {g} {b}");
    }

    // Zwei Farben mischen, t ist der Anteil der zweiten
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
    public const string SystemName = "System";

    public static readonly ThemeInfo[] All = { ThemeInfo.Dark, ThemeInfo.Titan };

    public static string[] Names => All.Select(t => t.Name).ToArray();

    // Zur Wahl in den Einstellungen: die Themen und „System“
    public static string[] Choices => Names.Append(SystemName).ToArray();

    public static ThemeInfo Default => All[0];

    public static bool IsSystem(string? name) => Norm(name ?? "") == "system";

    // Der gespeicherte Name in seiner gültigen Form. Die früheren Themen (Deep Sea, Cyber Grid, Space Glass, Obsidian,
    // Phosphor, die alten deutschen Namen) und Unbekanntes werden zu Dark.
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return SystemName;
        if (IsSystem(name)) return SystemName;
        return Find(name).Name;
    }

    // Das Thema zu einem Namen. „System“ ist hier Dark: wo nur eines gezeichnet werden kann (Anmeldeseite ohne Skript,
    // Fenster-Dialog, TUI), ist das die Vorgabe; die Weboberfläche wählt selbst nach dem Hell/Dunkel des Systems.
    public static ThemeInfo Find(string? name)
    {
        var n = Norm(name ?? "");
        return All.FirstOrDefault(t => n.Length > 0 && (Norm(t.Name) == n || Norm(t.Slug) == n || (t.Kind == "titan" && n is "titan" or "spacepunk")))
            ?? Default;
    }

    // Wie die Weboberfläche zeichnet: "dark", "titan" oder "system" (dann entscheidet ui.js nach prefers-color-scheme)
    public static string Mode(string? name) => IsSystem(name) || string.IsNullOrWhiteSpace(name) ? "system" : Find(name).Slug;

    // Der Stilblock für den Kopf jeder Seite: Dark als Grundlage, Titan über html[data-theme=titan]
    public static string Css() =>
        ":root{" + ThemeInfo.Dark.CssVariables() + "}html[data-theme=titan]{" + ThemeInfo.Titan.CssVariables() + "}";

    private static string Norm(string s) => new string(s.Where(char.IsLetter).ToArray()).ToLowerInvariant();
}
