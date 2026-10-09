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

    // ── Nur noch Dark und Spacepunk Titan (docs/plan-ui-redesign.md); Werte wie im Mockup docs/design/mockup-7.html ──
    [TestMethod]
    public void Themes_AreDarkAndTitan_WithTheMockupColors()
    {
        CollectionAssert.AreEqual(new[] { "Dark", "Spacepunk Titan" }, ThemeCatalog.Names);
        CollectionAssert.AreEqual(new[] { "Dark", "Spacepunk Titan", "System" }, ThemeCatalog.Choices);
        var dark = ThemeCatalog.Find("Dark");
        Assert.IsFalse(dark.Light);
        Assert.AreEqual("#060916", dark.Bg);
        Assert.AreEqual("#4fe3ff", dark.Accent);
        var titan = ThemeCatalog.Find("Spacepunk Titan");
        Assert.IsTrue(titan.Light);
        Assert.AreEqual("titan", titan.Kind);
        Assert.AreEqual("#d9dcdb", titan.Bg);
        Assert.AreEqual("#cbd0cf", titan.Card);
        Assert.AreEqual("#e0601f", titan.Signal);
        Assert.AreEqual("#bd5017", titan.Accent, "Hauptknopf: dunklere Stufe, damit Weiß darauf AA hat");
        Assert.AreEqual("#141819", titan.Display);
        Assert.AreEqual("#7cff9e", titan.DisplayInk);
    }

    // ── Die CSS-Variablen, die MainLayout bei jedem Takt in den Kopf schreibt ──
    [TestMethod]
    public void CssVariables_AreCompleteAndWellFormed()
    {
        string[] colors =
        {
            "--bg", "--bg-top", "--bg-bottom", "--card", "--card-top", "--card-bottom", "--line", "--card-border",
            "--top-line", "--track", "--ink", "--muted", "--acc", "--second", "--good", "--warn", "--bad",
            "--card2", "--signal", "--b-llama", "--b-ollama", "--b-lms", "--b-vllm", "--b-host",
            "--st-idle", "--st-read", "--st-gen", "--st-load", "--st-off", "--disp", "--disp-ink", "--disp-unit",
        };
        string[] triples =
        {
            "--bg-rgb", "--ink-rgb", "--muted-rgb", "--acc-rgb", "--second-rgb", "--good-rgb", "--warn-rgb", "--bad-rgb",
        };

        foreach (var t in ThemeCatalog.All)
        {
            var css = t.CssVariables();
            Assert.IsFalse(css.Contains('"'), $"{t.Name}: Anführungszeichen escaped Razor als &quot;");

            var map = css.Split(';').Select(p => p.Split(':', 2)).ToDictionary(p => p[0], p => p[1]);
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
    public void Find_MapsOldThemesToDark_AndSystemHasItsOwnMode()
    {
        Assert.AreEqual("Spacepunk Titan", ThemeCatalog.Find("titan").Name);
        Assert.AreEqual("Spacepunk Titan", ThemeCatalog.Find("SPACEPUNK-TITAN").Name);
        foreach (var old in new[] { "Deep Sea", "Cyber Grid", "Space Glass", "Obsidian", "Phosphor", "tiefsee", "Weltraumglas", "gibt es nicht" })
        {
            Assert.AreEqual("Dark", ThemeCatalog.Find(old).Name, old);
            Assert.AreEqual("Dark", ThemeCatalog.Normalize(old), old);
        }
        Assert.AreEqual("System", ThemeCatalog.Normalize("system"));
        Assert.AreEqual("System", ThemeCatalog.Normalize(null));
        Assert.AreEqual("Dark", ThemeCatalog.Find("System").Name, "wo nur eines gezeichnet werden kann: Dark");
        Assert.AreEqual("system", ThemeCatalog.Mode("System"));
        Assert.AreEqual("titan", ThemeCatalog.Mode("Spacepunk Titan"));
        Assert.AreEqual("dark", ThemeCatalog.Mode("Obsidian"));
        Assert.AreEqual("Dark", ThemeCatalog.Default.Name);
        Assert.AreEqual("System", new AppSettings().Theme, "neue Installationen folgen dem System");
    }

    [TestMethod]
    public void Css_HasDarkAtTheRoot_AndTitanBehindTheAttribute()
    {
        var css = ThemeCatalog.Css();
        StringAssert.StartsWith(css, ":root{color-scheme:dark");
        StringAssert.Contains(css, "html[data-theme=titan]{color-scheme:light");
        Assert.IsFalse(css.Contains('"'));
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