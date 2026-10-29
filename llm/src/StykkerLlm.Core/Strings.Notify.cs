using System.Globalization;

namespace StykkerLlm.Core;

// Tab-Titel und Browser-Benachrichtigung (TabStatus, DoneEvents, ui.js) – English, wie alle Oberflächentexte
public static partial class Strings
{
    public static string TabLost(string name) => $"{name} stopped";
    public static string TabLostTitle(string name) => $"Stykker LLM: {name} stopped";

    public const string NotifyLabel = "Alerts",
        NotifyTip = "Show a browser notification when a model server stops unexpectedly, the model tests are done or a benchmark is finished, also while this tab is in the background. Applies to this browser only.",
        NotifyUnsupported = "This browser cannot show notifications here. They need this PC (localhost) or an https address.",
        NotifyBlocked = "The browser blocks notifications for this page. Allow them in the site settings, then switch again.",
        NotifyOnToast = "Alerts are on for this browser: crashes, finished model tests and benchmarks.",
        NotifyOffToast = "Alerts are off for this browser.";

    // Fertige Läufe (DoneEvents)
    public const string DoneTestsTitle = "Stykker LLM: model tests finished";
    public static string DoneTestsBody(int done, int failed, double? score) =>
        (done == 1 ? "1 job done" : $"{done} jobs done") + (failed > 0 ? $", {failed} failed" : "")
        + (score is double s ? " · average score " + s.ToString("0", CultureInfo.InvariantCulture) : "");

    public static string DoneBenchTitle(string name) => $"Stykker LLM: benchmark of {name} finished";
    public static string DoneBenchBody(double seconds, double? tps, double? dropPct) =>
        "Took " + (seconds >= 90 ? (seconds / 60).ToString("0", CultureInfo.InvariantCulture) + " min" : seconds.ToString("0", CultureInfo.InvariantCulture) + " s")
        + (tps is double t ? " · " + t.ToString("0.0", CultureInfo.InvariantCulture) + " t/s" : "")
        + (dropPct is double d ? " · " + d.ToString("0", CultureInfo.InvariantCulture) + " % slower than the last run" : "");
}
