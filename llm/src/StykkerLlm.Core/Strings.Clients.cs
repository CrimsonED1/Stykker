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
        ClientAutoSection = "Safety check (Claude Code auto mode)", ClientAutoLabel = "Safety check model", ClientAutoSame = "Same as the default model",
        ClientAutoSet = "Safety check model set",
        ProxyVisionDescribeLabel = "Describe images only (recommended): the vision model says what is on each image, your default model answers",
        ProxyVisionDescribeOn = "Images are described as text for the default model", ProxyVisionDescribeOff = "Requests with images go to the vision model",
        ProxyVisionSection = "Images", ProxyVisionLabel = "Vision model", ProxyVisionSet = "Vision model set",
        ProxyVisionHint = "Requests that contain an image go to this model. Choose one that can read images, for example a cloud model; many local models cannot.",
        ProxyDebugLabel = "Debug: save the full text of every proxy request (logs\\proxy-debug, last 100)",
        ProxyDebugOn = "Proxy debug on: request texts go to logs\\proxy-debug", ProxyDebugOff = "Proxy debug off",
        ClientAutoHint = "In auto mode Claude Code checks each command with a second model before it runs. These checks go to the model chosen here. If your main model has only one slot, choose another running server or a cloud model, otherwise the checks wait or fail.";

    public static string ClientChipTip(string title, bool viaProxy) => $"{title}: {(viaProxy ? ClientViaProxy : ClientDirect)}";
    public static string ClientStartNotInstalled(string title) => $"{title} is not installed on this PC";
    public static string ClientLaunched(string label) => $"{label} started";
    public static string ClientLaunchFailed(string message) => $"Could not start: {message}";
    public static string ClientUnknown(string id) => $"Unknown client '{id}'";
    public static string ClientUnknownVariant(string key) => $"Unknown start option '{key}'";

    // Modelltest der Sicherheitsprüfung (Proxy-Dialog, Abschnitt „Safety check“)
    public const string ClassifierTestButton = "Test safety check models", ClassifierTestStop = "Stop",
        ClassifierTestProvider = "Models to test", ClassifierTestUse = "Use for safety check",
        ClassifierTestRealRequest = "Test request: your last real check – it contains your conversation and is sent to the provider.",
        ClassifierTestExampleRequest = "Test request: a built-in example without your conversation. It is sent to the provider.",
        ClassifierTestNoProvider = "Add a cloud provider with its key first.",
        ClassifierTestLocalRequest = "The test request stays on this PC: it goes straight to the running servers.",
        TestNoLocalModels = "No model is running on this PC. Start a server first.",
        TestSourceLocal = "This PC (running servers)",
        ClassifierTestRetry = "Retry unavailable",
        ClassifierTestNoKey = "No API key is stored for this provider. Nothing was sent.",
        ClassifierTestNoModels = "The provider has not listed any model yet. Try again in a few seconds.",
        ClassifierTestRunning = "A safety check test is already running.",
        ClassifierTestStarted = "Safety check test started.", ClassifierTestStopping = "Stopping the safety check test.",
        ClassifierTestNotRunning = "No safety check test is running.",
        ClassifierTestFailed = "The safety check test could not start.",
        ClassifierFormatOk = "ok", ClassifierFormatNo = "no",
        ClassifierColModel = "Model", ClassifierColFormat = "Format", ClassifierColStatus = "Status",
        ClassifierColFirst = "First byte (s)", ClassifierColTotal = "Total (s)", ClassifierColNote = "Note",
        ClassifierStatusSkipped = "skipped", ClassifierStatusTimeout = "timeout", ClassifierStatusError = "error",
        ClassifierNoteEmpty = "The answer is empty.", ClassifierNoteNotJson = "The answer is not a chat reply in JSON.";

    // Live-Zeile des Modelltests: Phase, Zähler, Sekunden, Restzeit (beide Tests)
    public static string TestPhaseText(TestPhase phase, double waitSec) => phase switch
    {
        TestPhase.Sending => "sending",
        TestPhase.WaitingFirstByte => "waiting for the first byte",
        TestPhase.Receiving => "receiving",
        TestPhase.RateLimitWait => $"waiting {waitSec.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s (rate limit)",
        TestPhase.Pause => "pause",
        _ => "",
    };
    public static string ClassifierTestCounts(int done, int total, int ok, int failed, int skipped) =>
        $"{done} of {total} · ok {ok} · failed {failed} · skipped {skipped}";
    public static string ClassifierTestSeconds(double sec) => sec.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s";
    // Restzeit grob, auf 5 Sekunden gerundet; ohne Schätzung leer
    public static string ClassifierTestLeft(double? sec)
    {
        if (sec is not double s) return "";
        int r = (int)(Math.Round(Math.Max(0, s) / 5) * 5);
        return r < 60 ? $"about {r} s left" : $"about {r / 60} min {r % 60} s left";
    }
    // Bekannte Fehler, kurz (die Originalmeldung steht im Tooltip)
    public const string ClassifierNoteNoImage = "no image input", ClassifierNoteBusy = "provider busy (limit)",
        ClassifierNoteNotForAccount = "not available for this account";
    public static string ClassifierNoteSlow(string seconds) => $"slower than {seconds} s";
    public static string ClassifierNoteSkipped(string word) => $"Not asked: the name contains '{word}' (not a chat model).";
    public const string ClassifierNoteUnavailableLast = "Not asked: the provider answered 404 last time. Tick \"Retry unavailable\" to ask again.";
    public static string ClassifierNoteFormat(string reply) => $"The answer does not start with <block>: {reply}";
    public static string ClassifierNoteTimeout(string seconds) => $"No complete answer within {seconds} seconds.";
    public static string ClassifierNoteRetried(string seconds) => $"Rate limited (HTTP 429): asked once more after {seconds} seconds.";
    public static string ClassifierNoteError(string message) => $"Connection failed: {message}";

    // Modelltest der Bilderkennung (Proxy-Dialog, Abschnitt „Images“): ein eingebautes Bild, Antwort muss Rot und Blau nennen
    public const string VisionTestButton = "Test vision models", VisionTestUse = "Use as vision",
        VisionTestImageNote = "Test request: a built-in 64 × 64 image (left red, right blue) and a question. It is sent to the provider.",
        VisionTestImageNoteLocal = "Test request: a built-in 64 × 64 image (left red, right blue) and a question. It stays on this PC.",
        VisionTestRunning = "A model test is already running.", VisionTestStarted = "Vision test started.",
        VisionTestColTime = "Time (s)", VisionTestColOk = "ok";

    public static string VisionNoteWrong(string reply) => $"The answer does not name both colours (red and blue): {reply}";

    // Tabellen beider Tests: Knöpfe „+ Models“, „Use as auto“ und „Use as vision“, Markierungen und Hinweise
    public const string TestAddModels = "+ Models", TestInList = "✓ in list", TestColActions = "Use",
        TestAddModelsTip = "Offer this model at the provider, as the Models dialog does",
        TestInListTip = "Offered at the provider. Click to take it off the list.",
        TestNotTestedAuto = "not tested for the safety check", TestNotTestedVision = "not tested for images",
        TestFailedAuto = "did not pass the safety check test", TestFailedVision = "did not pass the image test",
        TestMarkAuto = "safety check ✓", TestMarkVision = "vision ✓";
    public static string TestResultsFrom(DateTime time) =>
        "results from " + time.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    public static string ClassifierTestSummary(int ok, int failed, int skipped) => $"ok {ok} · failed {failed} · skipped {skipped}";
}
