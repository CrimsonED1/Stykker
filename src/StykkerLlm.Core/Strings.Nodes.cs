namespace StykkerLlm.Core;

// Texte für die Kopplung mit sechs Ziffern (beide Richtungen) und für die Nodes (docs/nodes.md).
// Fenster, TUI und Web zeigen dieselben Sätze.
public static partial class Strings
{
    // ── Kopplung mit sechs Ziffern ──
    public const string RoleHub = "Hub";
    public static string CodeValidFor(TimeSpan left) =>
        left.TotalSeconds <= 0 ? "expired, a new code follows" : $"valid for {(int)left.TotalMinutes}:{left.Seconds:00} min, once";
    public const string PairOtherWay = "Or the other way round";
    public const string PairShowCodeHint = "This device shows a code. Type it in on a device that is already signed in: window ☰ → Phone access, “stykker approve”, or the web page Phone access.";
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
        $"{name}{(address.Length > 0 ? " (" + address + ")" : "")}{(kind == PairKinds.Hub ? " · wants to use this PC as a node" : "")}";
    public const string PairApproveAsViewer = "as viewer";

    // ── Fenster StykkerUI ──
    public const string ShellFailed = "The window could not reach the server";
    public const string ShellNoServer = "StykkerLLM-Server was not found next to StykkerUI.";
    public const string ShellNoKey = "No access key in the data folder – start the server once as this user.";
    public const string ShellDeviceName = "Desktop window";
    // Beim Schließen gefragt: in den Tray legen (das Fenster läuft unsichtbar weiter) oder beenden
    public const string ShellCloseText = "Keep the window in the tray, or quit?";
    public const string ShellCloseTray = "Keep in tray", ShellCloseQuit = "Quit";

    // ── Nodes ──
    public const string NavNodes = "Nodes";
    public const string NodesTitle = "Nodes";
    public const string NodesHint = "Other PCs with Stykker, paired with this one. This PC is the hub: it shows and controls them, spreads tests and offers their models through its proxy.";
    public const string NodesEmpty = "No node paired yet. Search the network or enter an address.";
    public static string NodesSummary(int paired, int online) => $"{paired} paired, {online} online";
    public const string BtnNodesSearch = "Search network", BtnNodePair = "Pair node", BtnNodeRemove = "Unpair", BtnNodeOpen = "Open its page";
    public const string NodeAddressLabel = "Address (http://pc:8078)";
    public const string NodeCodeLabel = "Its access code (optional)";
    public const string NodeSearching = "Searching the network …";
    public const string NodeSearchNone = "No Stykker found. On the other PC switch on Home/VPN (Windows asks once for the firewall).";
    public static string NodeFound(int n) => n == 1 ? "1 Stykker found" : $"{n} Stykker found";
    public const string NodePaired = "paired";
    public const string NodeOnline = "online", NodeOffline = "offline";
    public static string NodeOfflineSince(TimeSpan t) =>
        t.TotalMinutes < 1 ? "offline" : t.TotalHours < 1 ? $"offline {(int)t.TotalMinutes} min" : t.TotalDays < 1 ? $"offline {(int)t.TotalHours} h" : $"offline {(int)t.TotalDays} d";
    public static string NodePairShowCode(string code) => $"Enter {code} on the other PC";
    public const string NodePairWhere = "There: window ☰ → Phone access, “stykker approve <code>”, or its web page Phone access.";
    public const string NodePairDone = "Node paired.";
    public const string NodePairFailed = "Pairing failed";
    public const string NodeNotReachable = "Not reachable. Is the server running there and Home/VPN switched on?";
    public const string NodeHubNotAllowed = "A hub may not change access (code, devices, Home/VPN) on this PC.";
    public const string NodeUnknown = "Unknown node.";
    public const string NodeThisPc = "this PC";
    public const string NodeNothingRunning = "nothing running";
    public const string NodeProfilePick = "Saved profile …";
    public const string NodeColStatus = "Status", NodeColRunning = "Running";
    public const string NodeAll = "All";
    public const string NodeTarget = "Run on";
    public const string NodeTargetAuto = "automatic (where the model is)";
    public const string NodeTargetHere = "this PC";
    public static string NodeQueued(string node) => $"queued on {node}";
    public const string NodeModelsTitle = "Models per node";
    public const string NodeModelsHint = "Which GGUF lies where, and the best measured speed per PC.";
    public const string NodeProxyHint = "Models of online nodes appear in this proxy as “node/model” when the node offers its proxy on the LAN.";
    public static string NodeProxyOff(string node) => $"{node}: proxy not on the LAN, its models are not offered here";
}
