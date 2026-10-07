namespace StykkerLlm.Core;

// Texte für die Kopplung mit sechs Ziffern (beide Richtungen) und das Fenster StykkerUI.
// Fenster, TUI und Web zeigen dieselben Sätze.
public static partial class Strings
{
    // ── Kopplung mit sechs Ziffern ──
    public static string CodeValidFor(TimeSpan left) =>
        left.TotalSeconds <= 0 ? "expired, a new code follows" : $"valid for {(int)left.TotalMinutes}:{left.Seconds:00} min, once";
    public const string PairOtherWay = "Or the other way round";
    public const string PairShowCodeHint = "This device shows a code. Type it in on a device that is already signed in: ☰ → Phone access in the window or web page.";
    public const string PairShowCode = "Show a code on this device";
    public const string PairWaiting = "Waiting for approval …";
    public const string PairDenied = "The request was declined.";
    public const string PairExpired = "The code has expired. Ask for a new one.";
    public const string PairTooMany = "Too many open requests. Try again in a few minutes.";
    public const string PairScanToApprove = "Or scan with a phone that is already signed in";
    public const string PairApproveTitle = "Approve a device";
    public const string PairApproveHint = "A new device (phone, tablet, another PC) shows six digits. Enter them here to let it in.";
    public const string PairApprovePlaceholder = "6 digits";
    public const string BtnApprove = "Approve", BtnDeny = "Decline";
    public const string PairApproveUnknown = "No open request with this code. Check the digits on the new device.";
    public static string PairApproved(string name) => $"Approved: {name}";
    public const string PairPending = "Waiting for approval";
    public static string PairPendingLine(string name, string address, string kind) =>
        $"{name}{(address.Length > 0 ? " (" + address + ")" : "")}";
    public const string PairApproveAsViewer = "as viewer";

    // ── Fenster StykkerUI ──
    public const string ShellFailed = "The window could not reach the server";
    public const string ShellNoServer = "StykkerLLM-Server was not found next to StykkerUI.";
    public const string ShellNoKey = "No access key in the data folder – start the server once as this user.";
    public const string ShellDeviceName = "Desktop window";
    // Beim Schließen gefragt: in den Tray legen (das Fenster läuft unsichtbar weiter) oder beenden
    public const string ShellCloseText = "Keep the window in the tray, or quit?";
    public const string ShellCloseTray = "Keep in tray", ShellCloseQuit = "Quit", ShellCloseCancel = "Cancel";
    public const string ShellCloseRemember = "Don't ask again (the tray menu brings the question back)";
    public const string TrayAskOnClose = "Ask when closing";
    public static string ServerPortBusy(int port, string detail) =>
        $"Port {port} is already in use (another StykkerLLM server with a different data folder, or another program). Start with --port <number>. ({detail})";
}
