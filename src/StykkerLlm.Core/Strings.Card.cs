namespace StykkerLlm.Core;

// Texte der Serverkarte (docs/plan-ui-redesign.md, U4). Auf der Karte stehen Symbole; diese Wörter sind ihre Tooltips
// und Screenreader-Texte.
public static partial class Strings
{
    public const string CardVram = "GPU VRAM", CardRam = "RAM of the server process", CardCpu = "CPU", CardFile = "Model file";
    public const string CardCtx = "Context per slot", CardSlots = "Busy slots / slots", CardModels = "Loaded models";
    public const string CardQueue = "Waiting requests (queue)", CardPeak = "Peak t/s", CardAvg = "Average t/s while active";
    public const string CardSum = "Tokens generated", CardDraft = "Draft acceptance (speculative decoding)", CardClients = "Clients";
    public const string CardNotReported = "not reported by this backend", CardNone = "none";
    public const string CardLocal = "local", CardLocalTip = "Runs on this machine";
    public static string CardHostTip(string host) => $"Runs on model host {host}";
    public const string CardServed = "Served by the proxy as \"stykker\"", CardServe = "Serve via the proxy as \"stykker\"";
    public const string CardRecord = "Record requests", CardStopRecord = "Stop recording", CardRecording = "Recording requests";
    public const string CardSave = "Save as profile", CardDetails = "Details", CardStop = "Stop", CardOpenHost = "Open on the Hosts page";
    public static string CardPort(int port) => $"Port {port}";
    public static string CardSpill(double gb) => $"{N1(gb)} GB spill into shared RAM – slower than it could be";
    public const string CardStateIdle = "idle", CardStateRead = "reading prompt", CardStateGen = "generating", CardStateLoad = "loading", CardStateOff = "offline";
    public const string CardModelCol = "loaded model", CardOnGpu = "on GPU", CardCtxCol = "ctx", CardUnloads = "unloads";
    public static string CardSlotTip(int id, string state, double tps, long gen, int used, int max) =>
        $"Slot {id}: {state}" + (state == CardStateIdle ? "" : $" · {N1(tps)} t/s · {gen:N0} tokens") + (max > 0 ? $" · ctx {used:N0} / {max:N0}" : "");
    public static string CardSparkAgo(int minutes) => $"−{minutes} min";
    // Hardware-Karte (U5)
    public const string SectionHardware = "Hardware", HwDetails = "details";
    public static string HwFree(double gb) => $"{N1(gb)} free";
    // Profil-Kacheln (U6)
    public const string PfNgl = "GPU layers (-ngl)", PfIdle = "Unload when idle", PfDraft = "Draft model", PfRestart = "Restart after a crash";
    public const string PfDraftOn = "Speculative decoding with a draft model", PfDraftOff = "No draft model";
    public const string PfRestartOn = "Restarts after a crash", PfRestartOff = "No restart after a crash";
    public const string PfOn = "on", PfOff = "off", PfCmdLine = "command line";
    public static string PfForecastTip(double need, double free, bool measured) =>
        $"Needs about {N1(need)} GB VRAM ({(measured ? "measured last run" : "estimated from the model file")}), {N1(free)} GB free now" +
        (need > free ? " – it may not fit, or spill into shared RAM." : ".");
    // Proxy-Panel (U9)
    public const string PxTitle = "Stykker-Proxy", PxOn = "on", PxOff = "off", PxCopy = "Copy address", PxCopied = "Copied";
    public const string PxAnswers = "Answers as \"stykker\"", PxLan = "Reachable in the network", PxLanOn = "LAN", PxLanOff = "this PC only";
    public const string PxSources = "Sources", PxThisPc = "This PC", PxMachines = "Other Stykker machines", PxCloud = "Cloud providers";
    public static string PxModels(int n) => n == 1 ? "1 model" : $"{n} models";
    public const string PxAdd = "Add", PxCancel = "Cancel", PxSetKey = "Set key", PxRemove = "Remove", PxKeyStored = "key stored", PxKeyMissing = "no key";
    public const string PxReachable = "reachable", PxUnreachable = "not reachable", PxNoMachines = "none attached", PxNoCloud = "none added";
    public static string PxModelList(int n) => $"{n} models under /v1/models";
    public const string PxRunning = "running", PxStopped = "stopped";
    // Letzte Anfragen (U7)
    public const string ReqModel = "model", ReqHost = "host", ReqTime = "time", ReqPrompt = "prompt t/s", ReqTokens = "tok";
    public const string ReqTools = "tools", ReqThink = "think", ReqResult = "result", ReqAgo = "ago";
    public const string ReqSpeedScale = "tokens/s colour: slow to fast", ReqClientUnknown = "client unknown";
    public const string ReqNoProxy = "only known for requests through the Stykker-Proxy";
    public static string ReqToolsTip(int n) => n == 1 ? "1 tool call" : $"{n} tool calls";
    public static string ReqThinkTip(int n) => n == 0 ? "no thinking tokens" : $"{n:N0} thinking tokens";
    public static string ReqResultText(string r) => r switch
    {
        "full" => "stopped at the length or context limit", "abort" => "cancelled by the client", "err" => "error answer", _ => "done",
    };
    // History (U8)
    public const string HistFilter = "Filter", HistAll = "All", HistRunning = "running", HistCrashed = "crashed";
    public const string HistToday = "Today", HistYesterday = "Yesterday", HistOlder = "Older";
    public const string HistEmptyTitle = "No runs yet", HistEmptyHint = "Every llama-server you start – here, in a terminal or by a script – appears here with its settings and speed.";
    public const string HistNoMatch = "Nothing matches", HistNoMatchHint = "Clear the search or pick All.";
    public const string HistTpsTip = "Average t/s while generating / peak", HistTokensTip = "Tokens generated in the last run";
    public static string HistRunTip(double avg, double total) => $"Average run {Fmt(avg)} · all runs {Fmt(total)}";
    public static string HistForgotten(string name) => $"Forgot \"{name}\"";
    public const string ToastUndo = "Undo";
    public static string HistEndText(string end, DateTime? at) => end switch
    {
        "live" => "running now",
        "clean" => "stopped by StykkerLLM" + At(at),
        "crashed" => "crashed" + At(at),
        "outside" => "ended outside StykkerLLM (closed or killed)" + At(at),
        "lost" => "ended unexpectedly – cause unknown, see the log" + At(at),
        _ => "end not recorded",
    };
    private static string At(DateTime? t) => t is DateTime d ? $" · {d:g}" : "";
    private static string Fmt(double sec) => sec < 60 ? $"{(int)sec} s" : sec < 3600 ? $"{(int)(sec / 60)} min" : $"{sec / 3600:0.#} h";
    // Befehlsfenster und Rahmen (U10)
    public const string CmdTitle = "Commands", CmdSearch = "Search or run …", CmdPlaceholder = "Page, profile, server or action …", CmdNone = "Nothing found";
    public const string CmdPage = "page", CmdAction = "action", CmdProfile = "profile", CmdServer = "server";
    public const string CmdProxyOn = "Turn the proxy on", CmdProxyOff = "Turn the proxy off";
    public static string CmdTheme(string t) => $"Theme: {t}";
    public static string CmdStart(string name) => $"Start {name}";
    public static string CmdStop(string name) => $"Stop {name}";
    public const string LostLogTail = "last log lines";
    public static string CardSlower(int pct) => $"{pct} % slower than this server's usual average – another program on the GPU, a hotter card or a bigger context?";
    public const string ThemeHint = "System follows the light or dark setting of this device: light → Spacepunk Titan, dark → Dark.";
    public static string HwMemTip(int modules, double totalGb, int channels) =>
        $"{modules} module(s), {N1(totalGb)} GB. Bandwidth is theoretical: speed × 8 bytes × {channels} channel(s) (channels estimated from the modules).";
    // Nach der Endprüfung (Feature-Bericht)
    public static string CardCtxFull(int slots) => slots == 1 ? "ctx full" : $"ctx full ×{slots}";
    public const string PfStartFailed = "start failed", PfCancelStart = "Cancel the start";
    public static string PfStartingFor(int sec) => $"starting … {sec} s";
    public const string PfRestartTurnOn = "Restart after a crash: turn on", PfRestartTurnOff = "Restart after a crash: turn off";
    public const string PxPort = "Port", PxPortTip = "Port of the proxy on this PC (1024–65535). Clients then use the new address.";
    public const string CmdHistory = "history";
    public static string CmdDetails(string name) => $"Details: {name}";
    public static string CmdRecord(string name) => $"Record {name}";
    public static string CmdRecordStop(string name) => $"Stop recording {name}";
    public static string CmdSave(string name) => $"Save {name} as profile";
    public static string CmdStartAgain(string name) => $"Start again: {name}";
    public const string NoticeDismissed = "Notice hidden";
    public const string HostRemoveTitle = "Remove host";
    public static string HostRemoveConfirm(string name) => $"Remove the host \"{name}\"? It has to be paired again with a new code.";
    public static string StateText(string state) => state switch
    {
        "gen" => CardStateGen, "read" => CardStateRead, "load" => CardStateLoad, "off" => CardStateOff, _ => CardStateIdle,
    };
}
