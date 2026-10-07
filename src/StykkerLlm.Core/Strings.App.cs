namespace StykkerLlm.Core;

// Texte zu Lebensdauer des Servers, Startbild des Fensters und Fehlerbericht. Web und TUI zeigen dieselben Sätze.
public static partial class Strings
{
    // ── Server an Fenster/TUI gebunden ──
    public const string KeepServerLabel = "Keep the server running when no window, terminal or web page is open";
    public const string KeepServerHint = "Off (default): the server ends about 15 seconds after the last StykkerUI window, stykker terminal " +
        "or web page closes – unless a model test or benchmark is still running. Turn it on for a PC that other PCs use as a node.";
    public static string ServerAutoStop(string why) => $"server: nobody needs it any more ({why}), shutting down";
    public static string ServerHolders(string who) => $"in use by: {who}";

    // ── Startbild StykkerUI ──
    public const string ShellStarting = "Starting the server …";
    public const string ShellConnecting = "Connecting …";
    public const string ShellSigningIn = "Signing in …";
    public const string ShellRetry = "Try again";
    public const string ShellSeeServerLog = "details in StykkerLLM-Server.log";

    // ── Fehlerbericht ──
    public const string BugReportTitle = "Report a bug";
    public const string BugReportHint = "Describe what happened and what you expected. The report collects the logs of all StykkerLLM programs, " +
        "your settings and the current state into a zip file. Keys, the access code, devices and provider keys are never included; " +
        "API keys, tokens, your Windows user name and the PC name are blacked out.";
    public const string BugReportPlaceholder = "What happened? What did you expect? Steps to reproduce …";
    public const string BtnBugReportCreate = "Create report", BtnBugReportDownload = "Download zip", BtnBugReportIssue = "Open a GitHub issue";
    public const string BugReportIssueHint = "The GitHub page opens with your description and the environment filled in. Attach the zip there yourself – nothing is uploaded automatically.";
    public const string BugReportEmpty = "Please describe the problem first.";
    public static string BugReportSummary(string zip, int files) => $"Report saved: {zip} ({files} files)";
    public const string BugReportFiles = "Contents";
    public const string TuiBugReport = "report a bug: zip with logs, settings and state (no secrets) + a GitHub issue link";
}
