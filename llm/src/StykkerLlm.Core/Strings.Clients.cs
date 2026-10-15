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

    // Modelltest der Sicherheitsprüfung (Proxy-Dialog, Abschnitt „Auto security classifier“)
    public const string ClassifierTestButton = "Test classifier models", ClassifierTestStop = "Stop",
        ClassifierTestProvider = "Provider to test", ClassifierTestUse = "Use as classifier",
        ClassifierTestRealRequest = "Test request: your last real check – it contains your conversation and is sent to the provider.",
        ClassifierTestExampleRequest = "Test request: a built-in example without your conversation. It is sent to the provider.",
        ClassifierTestNoProvider = "Add a cloud provider with its key first.",
        ClassifierTestNotOffered = "Choose this model under Models first.",
        ClassifierTestNoKey = "No API key is stored for this provider. Nothing was sent.",
        ClassifierTestNoModels = "The provider has not listed any model yet. Try again in a few seconds.",
        ClassifierTestRunning = "A classifier test is already running.",
        ClassifierTestStarted = "Classifier test started.", ClassifierTestStopping = "Stopping the classifier test.",
        ClassifierTestNotRunning = "No classifier test is running.",
        ClassifierTestFailed = "The classifier test could not start.",
        ClassifierFormatOk = "ok", ClassifierFormatNo = "no",
        ClassifierColModel = "Model", ClassifierColFormat = "Format", ClassifierColStatus = "Status",
        ClassifierColFirst = "First byte (s)", ClassifierColTotal = "Total (s)", ClassifierColNote = "Note",
        ClassifierStatusSkipped = "skipped", ClassifierStatusTimeout = "timeout", ClassifierStatusError = "error",
        ClassifierNoteEmpty = "The answer is empty.", ClassifierNoteNotJson = "The answer is not a chat reply in JSON.";

    public static string ClassifierTestProgress(int done, int total) => $"Done {done} of {total} models";
    public static string ClassifierNoteSkipped(string word) => $"Not asked: the name contains '{word}' (not a chat model).";
    public static string ClassifierNoteFormat(string reply) => $"The answer does not start with <block>: {reply}";
    public static string ClassifierNoteTimeout(string seconds) => $"No complete answer within {seconds} seconds.";
    public static string ClassifierNoteRetried(string seconds) => $"Rate limited (HTTP 429): asked once more after {seconds} seconds.";
    public static string ClassifierNoteError(string message) => $"Connection failed: {message}";
}
