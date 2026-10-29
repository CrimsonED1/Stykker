namespace StykkerLlm.Core;

// Die Tastenkürzel der Weboberfläche an einem Ort (Fenster „Keyboard shortcuts“ auf ?, Befehlsfenster). Gilt Shell = true,
// gibt es das Kürzel nur im Fenster StykkerUI (dort fehlen die Knöpfe des Browsers).
public sealed record Shortcut(string Keys, string What, bool Shell = false);

public static class Shortcuts
{
    public static IReadOnlyList<Shortcut> All { get; } = new[]
    {
        new Shortcut("Ctrl K", Strings.KeyCommands),
        new Shortcut("/", Strings.KeySearch),
        new Shortcut("?", Strings.KeyHelp),
        new Shortcut("Esc", Strings.KeyClose),
        new Shortcut("↑ ↓ Enter", Strings.KeyPick),
        new Shortcut("Enter", Strings.KeyRow),
        new Shortcut("Ctrl Enter", Strings.KeySend),
        new Shortcut("Alt ← / Alt →", Strings.KeyBackForward, Shell: true),
    };

    // Was in dieser Umgebung gilt: im Browser ohne die Kürzel des Fensters
    public static IReadOnlyList<Shortcut> For(bool shell) => All.Where(s => shell || !s.Shell).ToList();
}
