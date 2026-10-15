namespace StykkerLlm.Core;

// Texte der Proxy-Clients (Claude Desktop, Claude Code): Kopfleiste, Client-Dialog und die Meldungen der Aktionen.
public static partial class Strings
{
    public const string ClientStartSection = "Start", ClientStartDesktop = "Desktop app", ClientStartCliProxy = "CLI via the Stykker-Proxy",
        ClientStartCliDirect = "CLI direct to Anthropic", ClientNotInstalledHint = "not installed",
        ClientViaProxy = "via Stykker-Proxy", ClientDirect = "direct to Anthropic", ClientUseProxy = "Use the Stykker-Proxy",
        ClientDesktopDirectHint = "While the switch is off, the desktop app talks directly to Anthropic. That is not an error.",
        ClientCliHint = "Opens a new console window. The CLI uses the address of the Stykker-Proxy.",
        ClientProxyOff = "Proxy is off – start it first",
        ClientAutoSection = "Auto security classifier", ClientAutoLabel = "Classifier model", ClientAutoSame = "Same as the proxy target",
        ClientAutoSet = "Safety check model set",
        ProxyDebugLabel = "Debug: save the full text of every proxy request (logs\\proxy-debug, last 100)",
        ProxyDebugOn = "Proxy debug on: request texts go to logs\\proxy-debug", ProxyDebugOff = "Proxy debug off",
        ClientAutoHint = "In auto mode Claude Code checks each command with a second model before it runs. These checks go to the model chosen here. If your main model has only one slot, choose another running server or a cloud model, otherwise the checks wait or fail.";

    public static string ClientChipTip(string title, bool viaProxy) => $"{title}: {(viaProxy ? ClientViaProxy : ClientDirect)}";
    public static string ClientStartNotInstalled(string title) => $"{title} is not installed on this PC";
    public static string ClientLaunched(string label) => $"{label} started";
    public static string ClientLaunchFailed(string message) => $"Could not start: {message}";
    public static string ClientUnknown(string id) => $"Unknown client '{id}'";
    public static string ClientUnknownVariant(string key) => $"Unknown start option '{key}'";
}
