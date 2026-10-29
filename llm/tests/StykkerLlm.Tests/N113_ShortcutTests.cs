using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Tastenkürzel (Shortcuts): die Liste im Fenster „Keyboard shortcuts“ passt zu dem, was ui.js wirklich abfängt
[TestClass]
public class N113_ShortcutTests
{
    private static string UiJs() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "StykkerLlm.Server", "wwwroot", "ui.js"));

    [TestMethod]
    public void Browser_HasNoShellOnlyKeys()
    {
        Assert.IsTrue(Shortcuts.For(shell: true).Count > Shortcuts.For(shell: false).Count);
        Assert.IsFalse(Shortcuts.For(shell: false).Any(s => s.Keys.Contains("Alt")), "Alt+Pfeil gibt es nur im Fenster");
        Assert.IsTrue(Shortcuts.All.All(s => s.Keys.Length > 0 && s.What.Length > 10));
    }

    [TestMethod]
    public void UiJs_HandlesTheListedKeys()
    {
        var js = UiJs();
        StringAssert.Contains(js, "e.key === 'k'", "Ctrl K");
        StringAssert.Contains(js, "e.key === '/'");
        StringAssert.Contains(js, "e.key === '?'");
        StringAssert.Contains(js, "'ArrowLeft'", "Alt ← im Fenster");
        StringAssert.Contains(js, "keys: keys", "das Befehlsfenster öffnet die Liste über stykkerUi.keys");
    }
}
