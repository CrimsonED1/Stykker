namespace StykkerLlm.Core;

// Tab-Titel und Browser-Benachrichtigung bei einem Absturz (TabStatus, ui.js) – English, wie alle Oberflächentexte
public static partial class Strings
{
    public static string TabLost(string name) => $"{name} stopped";
    public static string TabLostTitle(string name) => $"Stykker LLM: {name} stopped";

    public const string NotifyLabel = "Crash alerts",
        NotifyTip = "Show a browser notification when a model server stops unexpectedly, also while this tab is in the background. Applies to this browser only.",
        NotifyUnsupported = "This browser cannot show notifications here. They need this PC (localhost) or an https address.",
        NotifyBlocked = "The browser blocks notifications for this page. Allow them in the site settings, then switch again.",
        NotifyOnToast = "Crash alerts are on for this browser.",
        NotifyOffToast = "Crash alerts are off for this browser.";
}
