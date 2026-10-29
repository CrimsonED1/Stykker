using System.Globalization;

namespace StykkerLlm.Core;

// Was der Browser-Tab über den Zustand sagt, auch wenn er im Hintergrund liegt: Titel („▶ 94 t/s · Monitor“), die Stufe für
// den Punkt im Favicon und die Meldung für eine Browser-Benachrichtigung bei einem Absturz. Nur Text und Stufe; ui.js
// zeichnet daraus das Favicon und zeigt die Benachrichtigung (nur wenn der Nutzer sie auf diesem Gerät eingeschaltet hat).
public enum TabLevel { Idle, Busy, Alarm }

// Eine Meldung: Id ist je Ereignis fest (zwei Tabs oder ein Neuladen melden es nicht doppelt), AtMs = Unix-Zeit in ms
public sealed record TabAlert(string Id, string Title, string Body, long AtMs);

public static class TabStatus
{
    public const string Brand = "Stykker LLM";

    public static TabLevel Level(IEnumerable<double> currentTps, ServerLost? lost) =>
        lost != null ? TabLevel.Alarm : currentTps.Any(t => t > 0) ? TabLevel.Busy : TabLevel.Idle;

    // Der Titel: Zustand vorn (im schmalen Tab bleibt nur der Anfang sichtbar), dann die Seite, dann die Marke
    public static string Title(string page, IEnumerable<double> currentTps, ServerLost? lost)
    {
        var tps = currentTps.Where(t => t > 0).Sum();
        var state = lost != null ? "⚠ " + Strings.TabLost(lost.Name)
            : tps > 0 ? "▶ " + tps.ToString(tps >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " t/s"
            : "";
        var parts = new[] { state, page, Brand }.Where(p => p.Length > 0);
        return string.Join(" · ", parts);
    }

    public static string LevelName(TabLevel level) => level switch
    {
        TabLevel.Busy => "busy",
        TabLevel.Alarm => "alarm",
        _ => "idle",
    };

    // Die Meldung zu einem verlorenen Server: Ursache, sonst der Hinweis aus dem Tray
    public static TabAlert? Alert(ServerLost? lost)
    {
        if (lost == null) return null;
        var at = UnixMs(lost.When);
        var body = !string.IsNullOrWhiteSpace(lost.Cause) ? lost.Cause! : Strings.LostBalloon(lost.Name);
        return new TabAlert("lost-" + at.ToString(CultureInfo.InvariantCulture) + "-" + lost.Name, Strings.TabLostTitle(lost.Name), body, at);
    }

    // Absturz oder fertiger Lauf (DoneEvents): gemeldet wird das neuere von beiden – ui.js zeigt jede Id nur einmal
    public static TabAlert? Alert(ServerLost? lost, DoneEvent? done)
    {
        var crash = Alert(lost);
        if (done == null) return crash;
        var at = UnixMs(done.When);
        if (crash != null && crash.AtMs >= at) return crash;
        return new TabAlert(done.Kind + "-" + at.ToString(CultureInfo.InvariantCulture), done.Title, done.Body, at);
    }

    private static long UnixMs(DateTime t) =>
        new DateTimeOffset(t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Local) : t).ToUnixTimeMilliseconds();
}
