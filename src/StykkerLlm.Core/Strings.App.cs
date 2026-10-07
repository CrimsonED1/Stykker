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

    // ── Anzeige im Terminal (stykker): nur ansehen, gesteuert wird im Web ──
    public const string TuiWebLabel = "Web", TuiNetwork = "network";
    public const string TuiCtrlCAgain = "Press Ctrl+C again to quit";
    public const string TuiWebOpened = "Web interface opened in the browser (signed in)";
    public const string TuiNoGpu = "not available", TuiScanning = "scanning …";
    public const string TuiKeysTitle = "Keys";
    public const string TuiKeyWeb = "open the web interface in the browser (signed in) – everything is controlled there";
    public const string TuiKeyCode = "access code and QR code for the phone";
    public const string TuiKeyRecent = "recent requests";
    public const string TuiKeyMemory = "VRAM and RAM per program";
    public const string TuiKeyDetails = "pick a server and show its details";
    public const string TuiKeyClose = "close the panel (quits when none is open)";
    public const string TuiKeyQuit = "quit (the server ends when no window or web page needs it)";
    public const string TuiWebOnly = "Start, stop, model tests, benchmarks, recordings, nodes and settings are in the web interface.";
    public const string RecentTitle = "Recent requests", TuiMemoryTitle = "Memory per program", TuiDetailsTitle = "Details";
    public const string TuiNoCode = "No access code (simulation).";
    public const string TuiCodeLabel = "Code";
    public const string TuiCodePhone = "Scan the QR code with the phone, or type the six digits on the pairing page.";
    public const string TuiRemoteInWeb = "Switch Home/VPN on in the web interface (☰ → Phone access) to use the phone.";
    public const string TuiModel = "Model", TuiBackend = "Backend", TuiMemory = "Memory", TuiModels = "Loaded";
    public const string TuiNotRunning = "The StykkerLLM server is not running. Start it with: stykker (display), stykker web (browser) or StykkerUI.";
    public const string TuiStopped = "The server is shutting down.";
}
