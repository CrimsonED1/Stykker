using System.Text.RegularExpressions;

namespace StykkerLlm.Tests;

// Jedes Eingabefeld der Weboberfläche hat einen Namen für Screenreader: ein verbundenes <label for>, ein umschließendes
// <label>, aria-label oder title. Ein <label> direkt davor ohne for zählt nicht – das sieht nur aus wie eine Beschriftung.
[TestClass]
public class N111_FormLabelTests
{
    private static string Src(params string[] parts) =>
        Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "StykkerLlm.Server" }.Concat(parts).ToArray());

    private static readonly Regex Field = new(@"<(input|select|textarea)\b(?:=>|[^>])*>", RegexOptions.Compiled);   // => einer Lambda im Attribut beendet das Tag nicht
    private static readonly Regex Id = new(@"\bid=""([^""@]+)""", RegexOptions.Compiled);

    [TestMethod]
    public void EveryField_HasAName()
    {
        var missing = new List<string>();
        foreach (var file in Directory.GetFiles(Src("Components"), "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Field.Matches(text))
            {
                var tag = m.Value;
                if (tag.Contains("type=\"hidden\"")) continue;
                if (tag.Contains("aria-label=") || tag.Contains("aria-labelledby=") || tag.Contains(" title=")) continue;
                if (Id.Match(tag) is { Success: true } id && text.Contains($"for=\"{id.Groups[1].Value}\"", StringComparison.Ordinal)) continue;
                // in einem <label> … </label> eingeschlossen
                int open = text.LastIndexOf("<label", m.Index, StringComparison.Ordinal);
                int close = text.LastIndexOf("</label>", m.Index, StringComparison.Ordinal);
                if (open >= 0 && open > close && !text[open..m.Index].Contains(" for=", StringComparison.Ordinal)) continue;
                int line = text[..m.Index].Count(c => c == '\n') + 1;
                missing.Add($"{Path.GetFileName(file)}:{line} {tag[..Math.Min(80, tag.Length)]}");
            }
        }
        Assert.AreEqual(0, missing.Count, "Feld ohne Namen:\n" + string.Join("\n", missing));
    }

    [TestMethod]
    public void EveryLabelFor_PointsToAnId()
    {
        var broken = new List<string>();
        foreach (var file in Directory.GetFiles(Src("Components"), "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"<label\b[^>]*\bfor=""([^""@]+)"""))
                if (!text.Contains($"id=\"{m.Groups[1].Value}\"", StringComparison.Ordinal)) broken.Add($"{Path.GetFileName(file)}: for=\"{m.Groups[1].Value}\"");
        }
        Assert.AreEqual(0, broken.Count, string.Join("\n", broken));
    }
}
