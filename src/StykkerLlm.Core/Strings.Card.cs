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
    public static string StateText(string state) => state switch
    {
        "gen" => CardStateGen, "read" => CardStateRead, "load" => CardStateLoad, "off" => CardStateOff, _ => CardStateIdle,
    };
}
