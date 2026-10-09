using System.Text.RegularExpressions;

namespace StykkerLlm.Tests;

// Symbole der Weboberfläche (docs/plan-ui-redesign.md, U2): ein Sprite, jedes verwendete Symbol muss darin stehen
[TestClass]
public class N61_IconTests
{
    private static string Src(params string[] parts) =>
        Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "StykkerLlm.Server" }.Concat(parts).ToArray());

    private static HashSet<string> SpriteIds()
    {
        var text = File.ReadAllText(Src("Components", "IconSprite.razor"));
        var ids = Regex.Matches(text, "<symbol id=\"([^\"]+)\" viewBox=\"0 0 16 16\">").Select(m => m.Groups[1].Value).ToList();
        Assert.AreEqual(ids.Count, ids.Distinct().Count(), "doppelte Symbol-Ids");
        Assert.AreEqual(Regex.Matches(text, "<symbol ").Count, ids.Count, "jedes Symbol im 16-px-Raster");
        return ids.ToHashSet();
    }

    [TestMethod]
    public void Sprite_HasTheMockupSet()
    {
        var ids = SpriteIds();
        foreach (var need in new[] { "i-vram", "i-ram", "i-cpu", "i-ctx", "i-slots", "i-port", "i-host", "i-draft", "i-restart", "i-timer",
                     "s-idle", "s-read", "s-gen", "s-load", "s-off", "r-ok", "r-abort", "r-err", "r-full",
                     "c-cli", "c-editor", "c-web", "c-agent", "x-tool", "x-think", "b-llama", "b-ollama", "b-lms", "b-vllm", "e-crash", "e-kill" })
            Assert.IsTrue(ids.Contains(need), need);
    }

    [TestMethod]
    public void EveryIconUsedInThePages_IsInTheSprite()
    {
        var ids = SpriteIds();
        var used = Directory.GetFiles(Src("Components"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "<Icon Name=\"([a-z0-9-]+)\"").Select(m => (File: Path.GetFileName(f), Name: m.Groups[1].Value)))
            .ToList();
        foreach (var (file, name) in used)
        {
            var id = name.Length > 2 && name[1] == '-' ? name : "i-" + name;
            Assert.IsTrue(ids.Contains(id), $"{file}: Symbol {name} fehlt im Sprite");
        }
    }
}
