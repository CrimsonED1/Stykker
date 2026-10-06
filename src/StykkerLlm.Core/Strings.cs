using System.Globalization;

namespace StykkerLlm.Core;

// Alle Texte der Oberfläche (Englisch) an einer Stelle. Zahlen immer mit der invarianten Kultur.
public static partial class Strings
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string N0(double v) => v.ToString("N0", Inv);
    public static string N1(double v) => v.ToString("N1", Inv);

    // ── Fenster, Kopf, Tray ──
    public const string AppName = "StykkerLLM";
    public const string TitleIdle = "StykkerLLM";
    public const string WordStykker = "STYKKER", WordSlm = "LLM";
    public const string OverlayTitle = "StykkerLLM";
    public const string TrayShow = "Show window", TrayOverlay = "Overlay", TrayTheme = "Theme ▸", TrayExit = "Exit";
    public const string TrayDonate = "Donate ♥";
    // Tray-Symbol des Servers (ohne Fenster erreichbar)
    public const string ServerName = "StykkerLLM-Server";
    public const string TrayServerWeb = "Open web interface", TrayServerStop = "Shut down server";
    public static string TrayServerTip(string url) => $"{ServerName} · {url}";
    public const string DonateUrl = "https://www.paypal.me/crimsoned";
    public static string Uptime(TimeSpan up) => $"{DateTime.Now:HH:mm:ss}  ·  up {(int)up.TotalHours:00}:{up.Minutes:00}:{up.Seconds:00}";
    public const string TitleNoServer = "StykkerLLM: no server", TitleLoading = "StykkerLLM: loading …";
    public static string TitleActive(double tps, int busy, int slots) => $"StykkerLLM: {tps.ToString("0", Inv)} t/s · {busy}/{slots}";
    public static string TitleVramFree(double gb) => $" · VRAM {N1(gb)} GB free";
    public const string TitleSettingsBroken = " · settings.json unreadable (defaults)";

    // ── Erkennung ──
    public const string NoServersTitle = "No server detected";
    public const string NoServersHint = "Start a llama-server (llama.cpp) and it will show up here within a few seconds.";
    public const string Sleeping = "sleeping";
    public const string DisplayOnly = "display only";
    public static string SaveBlockText(SaveBlock b) => b switch
    {
        SaveBlock.CommandLineUnreadable => "command line not readable",
        SaveBlock.RouterMode => "router mode: unknown, display only",
        SaveBlock.HuggingFace => "Hugging Face model: unknown, display only",
        SaveBlock.Manual => "added manually",
        SaveBlock.OtherBackend => "read-only backend",
        _ => "",
    };

    // ── Serverkarte ──
    public const string Loading = "loading model …", RunningLoading = "running / loading …", Offline = "offline";
    public const string StartingShort = "starting …";
    public const string TokensPerSec = "tokens/s", Clients = "Clients", None = "none", Stop = "Stop";
    public static string Stats(double peak, double avg, long generated, int busy, int slots) =>
        $"Peak {N1(peak)}     Avg active {N1(avg)}     Generated {N0(generated)}     Slots {busy}/{slots}";
    public const string ChipGpuVram = "GPU VRAM", ChipGpuShared = "GPU shared", ChipRam = "RAM", ChipCommit = "Commit",
        ChipCpu = "CPU", ChipModelFile = "Model file", ChipQueue = "queue", ChipSharedRam = "in RAM (slow)";
    public const string ColSlot = "Slot", ColStatus = "Status", ColGenerated = "Generated", ColContext = "Context";
    public const string ColBackend = "Backend", ColUrl = "URL", ColModelFile = "Model file", ColTps = "tokens/s";
    public const string ColName = "Name", ColServer = "Server", ColReqPrompt = "Prompt", ColReqGeneration = "Generation", ColReqTokens = "Tokens", ColReqTime = "Took";
    public const string SlotIdle = "idle", SlotReading = "reading prompt", SlotWriting = "generating";
    public const string RowPrompt = "Prompt", RowReply = "Reply";
    public static string TokensRead(int n) => $"{N0(n)} tokens read";
    public const string NoMaxTokens = "no max_tokens limit", LimitAlmostReached = "limit almost reached";
    public static string ContextTight(long free, long rest) => $"Context tight: free {N0(free)} < remaining {N0(rest)}";
    public const string ContextTotal = "Context total", ReadingPromptBand = "Reading prompt";

    // Kontext fast voll (CtxPressure.NearlyFull = 90 %) – derselbe Text in Fenster, Web und TUI
    public const string CtxAlmostFull = "context almost full";
    public static string Pct100(double f) => $"{(int)Math.Round(f * 100)} %";
    public static string CtxAlmostFullOf(int used, int max) => $"{CtxAlmostFull}: {N0(used)} / {N0(max)}";

    // Spekulatives Decoding: Quote der angenommenen Entwürfe seit dem Start des Servers
    public const string ChipDraft = "draft";
    public static string DraftRate(int accepted, int drafted) => $"{N0(accepted)} / {N0(drafted)} · {Pct100(drafted > 0 ? (double)accepted / drafted : 0)}";
    public static string DraftMean(double meanAccepted) => $"mean len {N1(meanAccepted)}";
    public const string DraftHint = "Speculative decoding: accepted drafts / drafted tokens since the server started";

    // Automatischer Neustart nach einem Absturz
    public const string FieldRestartOnCrash = "Restart after a crash";
    public const string FieldMaxRestarts = "Restarts before giving up";
    public const string RestartHint = "Only for profiles you started yourself. The monitor stops after the limit and says so.";
    public static string RestartDone(string name, int attempt) => $"{name} crashed - restarting (attempt {attempt})";
    public static string RestartFailed(string name) => $"{name} crashed and could not be started again";
    public static string RestartGaveUp(string name, int max) => $"{name} crashed too often - no further restart (limit {max})";

    // Leerlauf-Entladen
    public const string FieldIdleUnload = "Unload after (idle minutes, 0 = off)";
    public const string IdleUnloadHint = "Stops the server (llama.cpp) or unloads the model (Ollama, LM Studio) after this many minutes without a request.";
    public const string IdleUnloadOff = "Idle unload is off for this profile.";
    public static string IdleUnloadSet(int min) => $"Unloads after {min} idle minutes.";
    public static string IdleUnloadDone(string name) => $"{name} was idle - unloaded as the profile asks";
    public static string IdleUnloadQueued(string name, int min) => $"{name} will be unloaded after {min} idle minutes";

    // ── GPU und System ──
    public const string Gpu = "GPU", NvmlMissing = "NVML not available";
    public static string VramLow(double freeGb) => $"Low VRAM: only {N1(freeGb)} GB free";
    public const string GpuLoad = "Load", GpuVram = "VRAM", GpuPower = "Power", GpuTemp = "Temperature",
        GpuMemController = "Memory controller", GpuGfxClock = "Graphics clock", GpuMemClock = "Memory clock",
        GpuThrottling = "Throttling", GpuClocksLimited = "Clocks limited", GpuClocksFree = "Clocks free", VramFree = "VRAM free";
    public const string GpuConsumers = "VRAM per process (all programs)";
    public const string GpuConsumersNone = "No GPU process data yet.";
    public static string GpuConsumersSum(double processesGb, double usedGb, int count) =>
        $"Top {count}: {N1(processesGb)} GB  ·  GPU used: {N1(usedGb)} GB";
    public const string GpuDetails = "GPU details", GpuDetailsButton = "Details …";
    // Gestapelte Speicherbalken (VRAM bzw. RAM je Prozess) mit Tooltips
    public const string MemOther = "other", MemFree = "free";
    public const string RamConsumers = "RAM per process (all programs)";
    public static string MemSegmentTip(string name, double gb, double totalGb) =>
        $"{name}: {N1(gb)} GB ({(totalGb > 0 ? gb / totalGb * 100 : 0):0} %)";
    public static string MemMore(int n) => $"+{n} more";
    // Aufgeklappter Rest der Liste wieder zuklappen (Klick auf „+n more“)
    public const string MemShowLess = "show less";

    // Eval (Model tests): Bereiche in allen Oberflächen (docs/ui.md, Bereich 9)
    public const string EvalTitle = "Model tests", EvalRuns = "Runs", EvalResults = "Results", EvalCatalog = "Catalog", EvalModels = "Models";
    public const string EvalStartQueue = "▶ Start queue", EvalStopQueue = "■ Stop", EvalClearFinished = "Clear finished", EvalAddToQueue = "+ Add to queue";
    public const string EvalAllowCode = "run model-written Python code (coding tasks)", EvalFindModels = "Find models", EvalTry = "▶ Try on model",
        EvalSendAll = "Send suite to all models", EvalNewTest = "+ New test", EvalNewSuite = "+ Suite", EvalRepeat = "Repeat";
    public const string EvalNoJobs = "Nothing queued yet. Pick models and suites and press “Add to queue”.";
    public const string EvalNoSuites = "No test suites found. The built-in suites should be in the data folder (eval folder).";
    public const string EvalNoModels = "No models yet. “Find models” reads your saved profiles and searches the model folders.";
    public const string EvalAllSuites = "all suites (mean)", EvalPickCell = "Click a cell to see what the model answered.";
    public const string MenuEval = "Model tests …";

    // ☰-Panel des Hauptfensters
    public const string MenuAddServer = "Add server …", MenuFreeVram = "Free VRAM …", MenuSimulate = "Simulator …", MenuExit = "Exit";
    public const string MenuRecordAll = "Record all", MenuStopRecording = "Stop recording", MenuRecordings = "Recordings …";
    public const string MenuProxy = "Stykker-Proxy", MenuProxySettings = "Proxy settings …";
    public const string MenuBenchmark = "Benchmark …", MenuBenchResults = "Benchmark results …", MenuGpuDetails = "GPU details …";
    public const string MenuOverlay = "Mini overlay", MenuTheme = "Theme", MenuSettings = "Settings …";
    public const string MenuAbout = "About …", MenuDonate = "Donate ♥", MenuProject = "Project page";
    public const string GroupServer = "SERVER", GroupRecord = "RECORD", GroupTools = "TOOLS";
    public const string GroupView = "VIEW", GroupTheme = "THEME", GroupHelp = "HELP";
    public const string GpuDetailsHint = "What uses the GPU right now: every program (games, browsers, LLMs), not only monitored servers. Refreshes every second.";
    public const string GpuDetailsNoData = "No GPU process data available (NVML/PDH not present).";
    public const string ColProcess = "Process", ColGpuUtil = "GPU", ColVramUsed = "VRAM";
    public const string SystemCard = "System", NotAvailable = "not available", CpuLoad = "CPU load", Ram = "RAM", Commit = "Commit", Memory = "Memory";
    public static string CommitLow(double usedGb, double limitGb) =>
        $"Memory reserve low: {N1(usedGb)} of {N1(limitGb)} GB committed – programs may crash. Tip: start servers with a smaller prompt cache (--cache-ram) or enlarge the page file.";
    public static string LogicalCores(int n) => $"{n} logical cores";

    // ── Letzte Anfragen ──
    public const string RecentRequests = "Recent requests", NoneYet = "none yet", FromLog = "from log";
    public const string StatusDone = "done", StatusTruncated = "truncated", StatusCancelled = "cancelled";

    // ── Overlay ──
    public const string OverlayNoServer = "no server online", OverlayGpuMissing = "GPU: NVML not available", OverlayGpuLoad = "GPU load";
    public static string OverlayIdle(int slots) => $"idle 0/{slots}";
    public static string OverlayReading(int busy, int slots) => $"reading {busy}/{slots}";
    public static string OverlayWriting(int busy, int slots) => $"writing {busy}/{slots}";

    // ── Dialoge ──
    public const string StopTitle = "Stop server";
    public static string StopConfirm(string name, string proc, int pid, string url, string clients) =>
        $"Stop server \"{name}\"?\n\nProcess {proc} (PID {pid}), {url}\nRunning requests will be aborted.{clients}";
    public static string StopClients(string list) => $"\n\nConnected: {list}";
    public const string StopNoProcess = "No process found for this server.";
    public const string StopChanged = "The process has changed, nothing was stopped.";
    public const string StopManual = "This server was added manually and is not a local process. It cannot be stopped from here.";
    public static string StopFailed(string msg) => $"Stopping failed:\n{msg}";
}
