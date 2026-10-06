using System.Text.RegularExpressions;
using StykkerLlm.Cli.Tui;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Die Themen des Fensters in den Farben, die Web und TUI bekommen (ThemeCatalog). Der erste Test trägt die Werte aus
// das frühere Fenster (Theme.cs) nach: wer dort eine Farbe ändert, muss sie hier auch ändern – sonst sieht das Web
// etwas anderes aus als das Fenster.
[TestClass]
public class WebThemeTests
{
    private static readonly Regex HexColor = new("^#[0-9a-f]{6}([0-9a-f]{2})?$");

    // ── Farben wie im Fenster (Theme.cs: MakeSpaceGlass, MakeCyberGrid, …) ──
    [TestMethod]
    public void Themes_MatchTheWindowPalette()
    {
        var expected = new (string Name, string Kind, int Radius, string Bg, string BgTop, string BgBottom, string Ink,
            string Muted, string Accent, string Second, string Good, string Warn, string Bad)[]
        {
            ("Space Glass", "space", 12, "#060916", "#0a1026", "#0e0820", "#e8eeff", "#8292b8", "#4fe3ff", "#a77bff", "#6cffb0", "#ffc857", "#ff5c7a"),
            ("Cyber Grid", "cyber", 8, "#030608", "#030608", "#04090c", "#e8fbff", "#5f8792", "#4fe3ff", "#ff5c74", "#5bffb0", "#ffc857", "#ff5c74"),
            ("Obsidian", "flat", 8, "#0a0c0f", "#0a0c0f", "#0a0c0f", "#e6eaf0", "#7c8696", "#4fd1e8", "#7c8696", "#5cdca0", "#e8b44a", "#e5596b"),
            ("Deep Sea", "deep", 12, "#05070d", "#070b15", "#05070d", "#dce9fa", "#6f86a6", "#6cb8ff", "#7c9cd6", "#6ce8c0", "#ffc16c", "#ff6f8a"),
            ("Phosphor", "phosphor", 3, "#040605", "#040605", "#050906", "#b8ffd0", "#4c8a62", "#5cff9a", "#2fbf6e", "#5cff9a", "#ffb000", "#ff5c5c"),
        };

        Assert.AreEqual(expected.Length, ThemeCatalog.All.Length);
        foreach (var e in expected)
        {
            var t = ThemeCatalog.Find(e.Name);
            Assert.AreEqual(e.Name, t.Name);
            Assert.AreEqual(e.Kind, t.Kind, $"{e.Name}: Art des Hintergrunds");
            Assert.AreEqual(e.Radius, t.Radius, $"{e.Name}: Ecken der Karten");
            foreach (var (label, got, want) in new[]
            {
                ("Bg", t.Bg, e.Bg), ("BgTop", t.BgTop, e.BgTop), ("BgBottom", t.BgBottom, e.BgBottom),
                ("Ink", t.Ink, e.Ink), ("Muted", t.Muted, e.Muted), ("Accent", t.Accent, e.Accent),
                ("Second", t.Second, e.Second), ("Good", t.Good, e.Good), ("Warn", t.Warn, e.Warn), ("Bad", t.Bad, e.Bad),
            })
                Assert.AreEqual(want, got, $"{e.Name}: {label}");
        }
    }

    // ── Die CSS-Variablen, die MainLayout bei jedem Takt in den Kopf schreibt ──
    [TestMethod]
    public void CssVariables_AreCompleteAndWellFormed()
    {
        string[] colors =
        {
            "--bg", "--bg-top", "--bg-bottom", "--card", "--card-top", "--card-bottom", "--line", "--card-border",
            "--top-line", "--track", "--ink", "--muted", "--acc", "--second", "--good", "--warn", "--bad",
        };
        string[] triples =
        {
            "--bg-rgb", "--ink-rgb", "--muted-rgb", "--acc-rgb", "--second-rgb", "--good-rgb", "--warn-rgb", "--bad-rgb",
        };

        foreach (var t in ThemeCatalog.All)
        {
            var css = t.CssVariables();
            Assert.IsTrue(css.StartsWith(":root{") && css.EndsWith("}"), $"{t.Name}: kein :root-Block");
            Assert.IsFalse(css.Contains('"'), $"{t.Name}: Anführungszeichen escaped Razor als &quot;");

            var map = css[6..^1].Split(';').Select(p => p.Split(':', 2)).ToDictionary(p => p[0], p => p[1]);
            foreach (var name in colors)
            {
                Assert.IsTrue(map.ContainsKey(name), $"{t.Name}: {name} fehlt");
                Assert.IsTrue(HexColor.IsMatch(map[name]), $"{t.Name}: {name} = {map[name]} ist keine Farbe");
            }
            foreach (var name in triples)
            {
                Assert.IsTrue(map.ContainsKey(name), $"{t.Name}: {name} fehlt");
                var parts = map[name].Split(' ');
                Assert.AreEqual(3, parts.Length, $"{t.Name}: {name} = {map[name]} ist kein Farbtrio");
                foreach (var p in parts)
                    Assert.IsTrue(int.Parse(p) is >= 0 and <= 255, $"{t.Name}: {name} = {map[name]}");
            }
            Assert.AreEqual(t.Radius + "px", map["--radius"]);
            Assert.AreEqual(ThemeInfo.Rgb(t.Accent), map["--acc-rgb"], $"{t.Name}: Akzent-Trio passt nicht zur Farbe");
        }
    }

    // ── Web und TUI zeigen dieselben Themen in derselben Reihenfolge ──
    [TestMethod]
    public void Themes_HaveTheSameNamesAndOrderAsTheTerminal()
    {
        CollectionAssert.AreEqual(TuiTheme.All.Select(t => t.Name).ToArray(), ThemeCatalog.Names);
        foreach (var (tui, web) in TuiTheme.All.Zip(ThemeCatalog.All))
        {
            Assert.AreEqual(tui.Name, web.Name);
            foreach (var (label, rgb, got) in new[] { ("Ink", tui.Ink, web.Ink), ("Accent", tui.Accent, web.Accent), ("Good", tui.Good, web.Good) })
                Assert.AreEqual($"#{rgb.R:x2}{rgb.G:x2}{rgb.B:x2}", got, $"{web.Name}: {label} weicht von der TUI ab");
        }
    }

    [TestMethod]
    public void Find_AcceptsSlugSpacingCaseAndTheOldGermanNames()
    {
        Assert.AreEqual("Phosphor", ThemeCatalog.Find("phosphor").Name);
        Assert.AreEqual("Cyber Grid", ThemeCatalog.Find("cyber-grid").Name);
        Assert.AreEqual("Cyber Grid", ThemeCatalog.Find("CYBER GRID").Name);
        Assert.AreEqual("Deep Sea", ThemeCatalog.Find("tiefsee").Name);          // alte deutsche Namen
        Assert.AreEqual("Space Glass", ThemeCatalog.Find("Weltraumglas").Name);
        Assert.AreEqual("Deep Sea", ThemeCatalog.Find("gibt es nicht").Name);    // unbekannt heißt Vorgabe, nicht "nichts"
        Assert.AreEqual("Deep Sea", ThemeCatalog.Find(null).Name);
        Assert.AreEqual("Deep Sea", ThemeCatalog.Default.Name);
    }

    // ── Speicherbalken: die Farben des Fensters (MainForm.MemBar) ──
    [TestMethod]
    public void MemPalette_BeginsWithTheFourThemeColors()
    {
        foreach (var t in ThemeCatalog.All)
        {
            var pal = t.MemPalette();
            Assert.AreEqual(8, pal.Length, t.Name);
            Assert.AreEqual(t.Accent, pal[0]);
            Assert.AreEqual(t.Second, pal[1]);
            Assert.AreEqual(t.Good, pal[2]);
            Assert.AreEqual(t.Warn, pal[3]);
            foreach (var c in pal)
                Assert.IsTrue(HexColor.IsMatch(c), $"{t.Name}: Balkenfarbe {c} (dort ohne Deckkraft)");
        }
        var deep = ThemeCatalog.Default;
        Assert.AreEqual(ThemeInfo.Mix(deep.Accent, deep.Bad, 0.5), deep.MemPalette()[4]);
    }

    [TestMethod]
    public void Mix_And_Rgb_FollowTheWindowRules()
    {
        Assert.AreEqual("#808080", ThemeInfo.Mix("#000000", "#ffffff", 0.5));
        Assert.AreEqual("#000000", ThemeInfo.Mix("#000000", "#ffffff", 0));
        Assert.AreEqual("#ffffff", ThemeInfo.Mix("#000000", "#ffffff", 1));
        // Leerzeichen statt Kommas: rgb(var(--acc-rgb)/.12) ist nur so gültiges CSS (sonst verwirft der Browser die Eigenschaft)
        Assert.AreEqual("108 184 255", ThemeInfo.Rgb("#6cb8ff"));
        Assert.AreEqual("108 184 255", ThemeInfo.Rgb("#6cb8ffcc"));   // die Deckkraft zählt nicht mit
        var css = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "StykkerLlm.Server", "wwwroot", "app.css"));
        Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(css, @"--[a-z]+-rgb:\d+,"), "Rückfall-Werte in app.css mit Kommas");
    }
}