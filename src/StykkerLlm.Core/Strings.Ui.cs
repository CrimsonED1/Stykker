namespace StykkerLlm.Core;

// Texte für Saved/Running/History, Bearbeiten-Fenster und Start (AP4)
public static partial class Strings
{
    public const string Ok = "OK", Cancel = "Cancel", Close = "Close", Back = "Back";
    public const string Cancelled = "cancelled";

    // Bereiche
    public const string SectionSaved = "SAVED", SectionRunning = "RUNNING", SectionHistory = "History";
    public const string SavedEmpty = "No saved profiles yet. Use Save on a running server or on a history entry.";
    public const string RunningEmpty = "Nothing running";

    // Knöpfe
    public const string BtnStart = "Start", BtnEdit = "Edit", BtnRemove = "Remove", BtnSave = "★ Save", BtnSaved = "Saved",
        BtnDetails = "Details", BtnDismiss = "Dismiss", BtnOpenLog = "Open log", BtnAddServer = "+ Add server",
        BtnStartAgain = "Start again", BtnForget = "Forget", BtnRecord = "● Record", BtnStopRec = "■ Stop rec", BtnBench = "Bench",
        BtnRecordAll = "● Record all", BtnRecordings = "Recordings", BtnBenchmark = "Benchmark", BtnMore = "More";

    public const string RunningTag = "running";
    public static string StartingFor(int seconds) => $"starting … {seconds} s";
    public const string StartingHint = "waiting for the port to open (the model is loading)";
    public static string ExitedWithCode(int? code) => code == null ? "exited" : $"exited with code {code}";
    public const string NoLogLines = "(no log output)";
    public static string SavedSub(string model, string ctx, int? port) =>
        string.Join("  ·  ", new[] { model, ctx.Length > 0 ? "ctx " + ctx : "", port != null ? ":" + port : "" }.Where(s => s.Length > 0));
    public static string SavedStats(string lastRun, string best, string vram) =>
        string.Join("  ·  ", new[] { lastRun, best, vram }.Where(s => s.Length > 0));
    public const string NeverStarted = "never started here";
    public const string AsksForSecret = "asks for the API key / token at start";

    // Verlauf
    public const string ColModel = "Model", ColCtx = "Context", ColLastSeen = "Last seen", ColRuns = "Runs", ColBest = "Best t/s",
        ColAvg = "Avg t/s", ColVram = "Max VRAM", ColSavedFlag = "";
    public const string HistoryEmpty = "Nothing here yet. Every server that runs while the monitor is open shows up here.";

    // Dialoge
    public const string EditProfileTitle = "Edit profile";
    public const string FieldName = "Name", FieldNote = "Note", FieldProgram = "Program", FieldWorkingDir = "Working folder",
        FieldParams = "Parameters (option and value, one per row)", FieldRaw = "Command line (arguments only)", Browse = "Browse …";
    public const string SecretHint = "*** = secret value, it is asked for again at start and never stored.";
    public const string NeedNameProgram = "Name and program are required.";
    public static string RemoveProfileConfirm(string name) => $"Remove the saved profile \"{name}\"?\n\nIts history stays.";
    public static string ForgetConfirm(string name) => $"Forget the history entry \"{name}\"?";
    public const string StartTitle = "Start server";
    public static string StartErrors(string name, string list) => $"\"{name}\" cannot be started:\n\n{list}";
    public static string StartWarnings(string name, string list) => $"Warnings for \"{name}\":\n\n{list}\n\nStart anyway?";
    public static string StartFailed(string msg) => $"The server process could not be started:\n{msg}";
    // Startknopf-Tooltip: ist das Profil startbar? (Vorprüfung im Hintergrund, siehe MainForm.StartTipFor)
    public const string StartTipChecking = "Checking …";
    public const string StartTipReady = "Ready to start";
    public static string StartTipBlocked(string list) => "Not startable:\n" + list;
    public static string StartTipWarn(string list) => "Startable, but:\n" + list;
    public static string SecretPromptTitle(string opt) => $"Value for {opt}";
    public static string SecretPromptText(string opt) => $"Enter the value for {opt}. It is used for this start only and is never stored or shown:";
    public const string AlreadyStarting = "A server for this port is already starting.";
    public const string SaveFailed = "This server cannot be saved: ";
    public const string SaveBlockNoCommand = "This entry has no command line, so it cannot be saved as a profile.";
    public const string AddServerTitle = "Add server by URL";
    public const string RemoveServerTitle = "Remove server";
    public const string FieldUrl = "URL (for example http://192.168.1.5:8080)", FieldLog = "Log file (optional, local path)";
    public const string BadUrl = "Please enter a valid http:// or https:// URL.";
    public const string RemoveManual = "Remove";
    public const string ManualTag = "added manually";
    public const string FieldBackend = "Server type", KindAuto = "Auto-detect";
}

// Details-Fenster (AP5)
public static partial class Strings
{
    public static string DetailsTitle(string name) => $"Details · {name}";
    public const string BtnCopyCommand = "Copy command";
    public const string SecCommand = "Command line (secrets are hidden)", SecParams = "Parameters and environment", SecServer = "Server and model",
        SecMemory = "Memory and CPU", SecTemplate = "Chat template", SecLog = "Log (last 50 lines)";
    public const string ColOption = "Option", ColValue = "Value", ColProperty = "Property", ColTime = "Time", ColTask = "Task",
        ColPromptTok = "Prompt tok", ColPromptTps = "Prompt t/s", ColReplyTok = "Reply tok", ColReplyTps = "Reply t/s", ColSeconds = "Seconds";
    public const string CommandNotReadable = "(command line not readable: the process belongs to another user or runs elevated)";
    // Art der Anfrage (0/1/2 wie im Serverprotokoll); dieselbe Spalte in Fenster, Web und TUI
    public static string TaskName(int task) => task switch { 0 => "completion", 1 => "chat", 2 => "embedding", _ => task.ToString(Inv) };
    public const string NoParameters = "(no parameters)", NoProps = "(no answer from /props yet)", NoTemplate = "(no chat template reported)",
        NoLogFile = "(no log file: the server was started without --log-file)";

    // Details eines Verlaufseintrags (nur gespeicherte Werte, der Server läuft nicht unbedingt)
    public static string HistoryDetailsSub(string lastSeen, int runs, string total) =>
        $"last run {lastSeen}  ·  {runs} {(runs == 1 ? "run" : "runs")}  ·  {total} in total";
    public const string SecRuns = "Runs and performance", SecModelFile = "Model file";
    public const string ModelFileMissing = "(file not found on this computer)", NoModelPath = "(no model file in the command line)";
    public const string RunningNow = "now";

    // Die Beschriftungen der Listen in den Verlaufs-Details: Fenster (HistoryDetailsForm) und Web (/history/{key}) sagen dasselbe.
// Die Laufstatistik-Zeilen kommen von Strings.RowFirstSeen/RowRunningTime/RowTpsBest/RowTpsAverage/RowGpuMax (dieselben wie in der TUI).
public const string HistProgram = "Program", HistSecrets = "Secrets",
        HistFile = "File", HistFileSize = "File size", HistFileSizeWhenRan = "File size (when it ran)",
        HistArchitecture = "Architecture", HistQuantization = "Quantization", HistParameters = "Parameters",
        HistLayers = "Layers", HistTrainedContext = "Trained context", HistVramEstimate = "VRAM estimate",
        SecretsNote = "yes – shown as ***, asked for again on start";

    // In den Parameterlisten aller drei Oberflächen: eine Umgebungsvariable
    public static string EnvRow(string name) => "env " + name;

    // Die Aufteilung der VRAM-Schätzung aus Modelldatei und Startparametern (Fenster und Web rechnen dasselbe)
    public static string VramEstimateParts(double total, double model, double kv, double compute) =>
        $"{N1(total)} GB (model {N1(model)} + KV cache {N1(kv)} + buffers {N1(compute)})";
}

// Ollama und LM Studio (AP7)
public static partial class Strings
{
    public const string NotRunning = "not running", RunningNothingLoaded = "running, nothing loaded", NothingLoaded = "No model is loaded right now.";
    public static string ModelsLoaded(int n) => n == 1 ? "1 model loaded" : $"{n} models loaded";
    // Engine (llama-server), die LM Studio selbst gestartet hat: gehört zur Karte, kein Stop, kein Save
    public static string EngineHeader(string model, int port, double tps) => $"Engine (llama.cpp, managed by LM Studio)  ·  {model}  ·  :{port}  ·  {N1(tps)} t/s";
    public const string ReadOnlyBackend = "read-only", BtnUnload = "Unload", StaysLoaded = "stays loaded", Unloading = "unloading …";
    public static string UnloadsInMinutes(int m) => $"unloads in {m} min";
    public static string UnloadsInSeconds(int s) => $"unloads in {s} s";
    public static string SharedMemoryNote(double gb) => $"{N1(gb)} GB in shared memory (RAM, slower)";
    // Ein Text für alle Oberflächen: wann ein Ollama-/LM-Studio-Modell von selbst entladen wird
    public static string Until(DateTime? expires)
    {
        if (expires == null) return "";
        var left = expires.Value - DateTime.Now;
        if (left.TotalDays > 365) return StaysLoaded;
        if (left.TotalSeconds <= 0) return Unloading;
        return left.TotalMinutes < 1 ? UnloadsInSeconds((int)left.TotalSeconds) : UnloadsInMinutes((int)Math.Ceiling(left.TotalMinutes));
    }
    public const string UnloadTitle = "Unload model";
    public static string UnloadConfirm(string model) =>
        $"Unload \"{model}\" from Ollama's memory?\n\nThe model stays installed and is loaded again on the next request. A running request using it is aborted.";
    public static string UnloadFailed(string model) => $"Ollama did not accept the request to unload \"{model}\".";

    // Free VRAM
    public const string BtnFreeVram = "Free VRAM", FreeVramTitle = "Free VRAM";
    public const string FreeVramNothing = "Nothing to free: no local llama.cpp server is running and Ollama has no model loaded.";
    public const string FreeVramOnlyLmStudio = "Only LM Studio has a model loaded. Unload it in LM Studio (the monitor has no way to do that).";
    public const string FreeVramLmStudioNote = "(LM Studio: unload its model in LM Studio itself)";
    public static string FreeVramStopLine(string name, string url, double? vramGb) =>
        $"• stop {name}  ({url}{(vramGb is double v ? $", {N1(v)} GB VRAM" : "")})";
    public static string FreeVramUnloadLine(string model, string server) => $"• unload {model}  ({server})";
    public static string FreeVramConfirm(string list) =>
        $"This frees the graphics card memory:\n\n{list}\n\nRunning requests are aborted. Saved profiles stay and can be started again.";
    public static string FreeVramFailed(int n) => $"{n} action(s) did not succeed. See the cards for details.";

    // Server lost (crash or stopped outside the monitor)
    public const string LostTitle = "Server stopped";
    public static string LostBalloon(string name) => $"{name} stopped (not by the monitor). Click for details.";
    public static string LostHeader(string name, string url, int? pid, DateTime when) =>
        $"{name} ({url}{(pid is int p ? $", PID {p}" : "")}) stopped at {when:HH:mm:ss} – not by the monitor.";
    public const string LostNoCause = "No known cause found in the log. It may have been closed from outside (window closed, task manager) or crashed without a message.";
    public const string LostNoLog = "(no log file – start servers with --log-file, or from a saved profile, to see the reason here)";

    // About / start with Windows / export
    public const string BtnAbout = "About", AboutTitle = "About StykkerLLM";
    public const string StartWithWindows = "Start with Windows (in the tray)";
    public const string BtnCopyPowerShell = "Copy as PowerShell", BtnCopyCmd = "Copy as cmd";
    public const string CopiedScript = "Start script copied to the clipboard";
    public const string HistorySearch = "Search history …";
    public static string HistoryNoneMatch(string q) => $"No history entry matches \"{q}\".";
    public const string DetectingServer = "Checking which server answers at this address …";
    public static string DetectedAs(string kind) => $"Detected: {kind}.";
    public const string NothingAnswered = "No known server (llama.cpp, Ollama, LM Studio) answered at this address. It is added as llama.cpp and shown as offline until it responds.";
}

// Aufnahme und Benchmark (AP8/AP9)
public static partial class Strings
{
    public const string NoSamples = "No data for this metric.", NoRequestsRecorded = "No requests were recorded in this session.";
}

public static partial class Strings
{
    public static string N2(double v) => v.ToString("N2", Inv);
    public static string RecordingTitle(string model, DateTime started) => $"Recording · {(model.Length > 0 ? model : "all servers")} · {started.ToString("yyyy-MM-dd HH:mm", Inv)}";
    public const string RecordingsTitle = "Recordings", CompareTitle = "Compare recordings";
    public const string BtnExportSamples = "Export samples (CSV)", BtnExportRequests = "Export requests (CSV)", BtnOpenFolder = "Open folder",
        BtnView = "View", BtnCompare = "Compare", BtnDelete = "Delete";
    public const string ModeProxy = "with proxy: thinking and answer are told apart", ModeLogOnly = "log only: thinking and answer are shown together as generation";
    public const string ModeProxyShort = "proxy", ModeLogShort = "log";
    public const string TileDuration = "Duration", TileRequests = "Requests", TileBusy = "Model busy", TileTokens = "Tokens", TileCache = "Cache hits",
        TileTps = "Tokens/s", TileVram = "Max VRAM", TileEnergy = "Energy", TileTtft = "First token p50 / p95";
    public const string SecTimeSplit = "Where the time went", SecTimeline = "Requests", SecChart = "Metric";
    public const string PhasePrompt = "Reading prompt", PhaseThinking = "Thinking", PhaseAnswer = "Answer", PhaseGeneration = "Generation", PhaseWaiting = "Waiting for client";
    public const string TimelineLegend = "amber = reading the prompt, purple = thinking, cyan = answer/generation;   ⚑ truncated   ✕ cancelled   ◷ unusually long   ƒ tool call";
    public const string SelectRequestHint = "Click a request to see its details.";
    public const string ColStarted = "Started", ColDuration = "Duration", ColRequests = "Req", ColPeak = "Peak t/s", ColTokensOut = "Tokens out", ColEnergy = "Energy", ColMode = "Mode",
        ColSlots = "slots", ColGenTps = "Gen t/s";
    public const string NoActiveRecording = "No recording is running.", AllServers = "all servers";
    public const string RecordingsEmpty = "Pick two recordings to compare (Recordings → tick two → Compare).", CompareDelta = "Delta";
    public const string BtnUnselectAll = "Unselect";
    public static string ComparePickTwo(int n) => n == 2 ? "Compare the two selected recordings" : $"Tick two recordings to compare (now {n})";
    public static string RecordingNow(string list) => "Recording now: " + list;
    public const string RecordingUnreadable = "This recording could not be read.";
    public static string DeleteRecordingsConfirm(int n) => n == 1 ? "Delete the selected recording? This cannot be undone." : $"Delete the {n} selected recordings? This cannot be undone.";
}

public static partial class Strings
{
    public const string GlobalRecordingRunning = "A recording of all servers is running. Stop it first.";
    public const string NothingToRecord = "There is no server to record yet.";
    public const string ReadOnlyEngine = "Read-only view: another Stykker window uses this data folder, so nothing is changed or recorded here.";
    public static string RecordingSaved(int requests, string duration) => $"Recording saved: {requests} requests, {duration}. See Recordings.";
    public const string RecordingFailed = "The recording could not be saved.";
    public const string NoFreeProxyPort = "No free port was found for the proxy.";
    public static string ProxyStartFailed(string msg) => $"The proxy could not be started:\n{msg}";
    public static string ProxyHint(int port, int requests, int active) =>
        $"Proxy on 127.0.0.1:{port}: point your client here to also see thinking vs answer, tool calls and time to first token.   {requests} requests" + (active > 0 ? $", {active} active" : "");
    public const string BtnProxy = "Proxy";
    public static string BtnProxyOn(int port) => $"Proxy :{port}";
    public static string BtnRecording(int seconds) => $"■ Rec {seconds / 60}:{seconds % 60:00}";
    public const string BtnStopAllRec = "■ Stop recording";

    // „Stykker-Proxy": ein Proxy, Routing nach Modell
    public const string ProxyNoTarget = "The Stykker-Proxy has no target: no server is running.";
    public const string ProxyTargetLoading = "The target server is still loading its model. Please try again in a moment.";
    public const string ProxyTargetOffline = "The target server is offline.";
    public const string ProxyTargetNone = "none";
    public const string BtnStykkerProxy = "Stykker-Proxy";
    public static string BtnStykkerProxyOn(int port) => $"Stykker-Proxy :{port}";
    public static string ProxyBar(bool on, int port, string target) =>
        on ? $"Stykker-Proxy: on · 127.0.0.1:{port} · target: {target}" : "Stykker-Proxy: off";
    public static string ProxyBar(bool on, int port, string target, bool lan) =>
        on ? $"Stykker-Proxy: on · {(lan ? "LAN" : "127.0.0.1")}:{port} · target: {target}" : "Stykker-Proxy: off";
    public const string ProxyDialogTitle = "Stykker-Proxy";
    // WinForms-Kopfzeile: Schalter (zeigt den Zustand, Klick schaltet um) und Einstellungen
    public static string BtnProxySwitchOn(int port) => $"● Proxy on :{port}";
    public const string BtnProxySwitchOff = "○ Proxy off", BtnProxySettings = "Proxy settings …";
    public const string ProxyTargetLabel = "Target (default for \"stykker\" and unknown models)";
    public const string ProxyTargetAuto = "Auto";
    public const string ProxyPortLabel = "Port";
    public static string ProxyClientUrl(int port) => $"http://127.0.0.1:{port}/v1";
    public static string ProxyAnthropicUrl(int port) => $"http://127.0.0.1:{port}";
    public const string ProxyCopy = "Copy URL";
    public const string ProxyClientHint = "Point OpenAI-compatible clients (Qwen Code, Aider …) to this URL and use model \"stykker\", or any model from /v1/models.";
    public const string ProxyAnthropicHint = "Anthropic-compatible clients (Claude Code …) use the base URL without /v1.";
    // Serviertes Modell (aus den aktiven Modellen) und angehängte Rechner
    public const string ProxyServeLabel = "Model served as \"stykker\" (from active models)";
    public const string ProxySwitchHint = "The choice applies from the next request. A running client session (Qwen Code …) may need a reload, because another backend can use a different chat template.";
    public const string ProxyRemoteLabel = "Other machines with Stykker";
    public const string ProxyRemoteNameLabel = "Name";
    public const string ProxyRemoteUrlLabel = "URL (http://machine:17500)";
    public const string ProxyRemoteHint = "The other machine must offer its Stykker-Proxy on the LAN. Its models then appear here, like one local LLM.";
    public const string ProxyRemoteEmpty = "No other machine attached.";
    public const string ProxyLanLabel = "Offer this proxy on the LAN (no access code)";
    public const string ProxyLanHint = "Warning: with the LAN switch anyone in the network can use this proxy. Otherwise it is reachable only from this PC.";
    public static string ProxyRemoteOffline(string name) => $"\"{name}\" is not reachable.";
    // Befehlszeile der TUI und von "stykker proxy" (im Fenster gibt es die Schalter, im Web die Karte)
    public const string ProxyUsage = "usage: stykker proxy [on|off|lan|serve <server|provider/model|auto>|providers]";
    public const string ProxyNoticeDismiss = "/notice dismiss";
    public const string BtnProxyAddRemote = "Add machine", BtnProxyRemoveRemote = "Remove";
    // Proxy-Chip auf der Server-Karte (Nr. 48) und Hinweis bei zu kleinem Ziel-Kontext (Nr. 47)
    public const string BtnProxyCard = "→ Proxy";
    public const string BtnProxyServed = "→ Proxy: stykker";
    public static string ProxyContextTooSmall(long requestTokens, long ctxTokens) =>
        $"Target context too small: {requestTokens:N0} > {ctxTokens:N0} tokens — start the server with a larger -c.";
    public const string ProxyContextTooSmallGeneric =
        "Target context too small for this request — start the server with a larger -c.";

    // Cloud-Anbieter über den Proxy (Nr. 46). Ohne eingetragenen Anbieter mit Schlüssel geht keine Anfrage ins Netz.
    public const string ProxyProviderLabel = "Cloud providers";
    public const string ProxyProviderHint =
        "Requests to a provider leave this computer. Stykker stores the key protected for your Windows user, never in clear text, and sends it only as \"Authorization: Bearer\" to that provider.";
    public const string ProxyProviderNameLabel = "Name";
    public const string ProxyProviderUrlLabel = "Base URL (https://openrouter.ai/api/v1)";
    public const string ProxyProviderKeyLabel = "API key";
    public const string ProxyProviderEmpty = "No cloud provider added.";
    public static string ProxyProviderModels(int count) => count == 1 ? "1 model" : $"{count} models";
    public const string ProxyProviderModelsNone = "no models yet";
    public const string ProxyProviderKeyStored = "key stored";
    public const string ProxyProviderKeyMissing = "no key stored — this provider is not asked at all";
    // Das Cloud-Modell in der Auswahl: der Anzeigename ist „<Anbieter> · <Modell>"
    public static string ProxyProviderTarget(string display) => $"☁ {display}";
    public const string ProxyProviderNoModels = "The provider returned no models.";
    public static string ProxyProviderModelError(int status) => $"The provider answered the model list with HTTP {status}.";
    public static string ProxyProviderOffline(string msg) => $"The provider is not reachable: {msg}";
    public const string ProxyProviderUrlInvalid = "The base URL must start with http:// or https://.";
    public const string ProxyProviderKeyNeeded = "A provider needs an API key.";
    public const string ProxyProviderKeyMissingShort = "No API key stored for this provider.";
    public const string ProxyProviderUnknown = "This provider is not in the list.";
    public const string ProxyProviderAdded = "Provider added.";
    public const string ProxyProviderRemoved = "Provider removed.";
    public const string ProxyProviderKeySet = "The key was saved.";
    public const string BtnProxyAddProvider = "Add provider", BtnProxyProviderKey = "New key";
    // Antworten des Proxys, wenn ein Cloud-Modell ohne Schlüssel oder ohne Erreichbarkeit angesprochen wird
    public const string ProxyProviderNoKey = "This model needs an API key. Add the key for this provider in the proxy settings.";
    public const string ProxyProviderOfflineProxy = "The provider is not reachable. Check the base URL and the key.";
    public const string ProxyProvidersTitle = "Cloud providers (requests leave this computer)";
    public const string ProxyProvidersUsage = "set as target: stykker proxy serve <provider/model>";

    // Anhaltende Hinweiszeile (bleibt stehen, bis sie bestätigt wird)
    public const string NoticeHint = "Hint", NoticeAlarm = "Attention";
}

public static partial class Strings
{
    public const string BenchTitle = "Benchmark", BenchResultsTitle = "Benchmark results", BenchTarget = "Server or saved profile to measure";
    public const string BenchHint = "A fixed test series with fixed prompts (prompt cache off, temperature 0, seed 42), so results can be repeated and compared.";
    public const string BenchChat = "Short chat", BenchTool = "Tool call", BenchParallel = "Parallel requests (needs -np > 1)", BenchContexts = "Context sizes (prompt tokens)";
    public const string BenchRepeats = "Repeats", BenchGenTokens = "Tokens per test";
    public const string BenchRun = "Run benchmark …", BenchResults = "Saved results", BenchWillStart = "(not running: will be started)";
    public const string BenchNoTarget = "There is no running llama.cpp server and no saved profile to measure.";
    public const string BenchNothingSelected = "Select at least one test.";
    public static string BenchEstimate(string d) => $"about {d}";
    public static string BenchConfirm(string what, string estimate) =>
        $"Run the benchmark on {what}?\n\nThe server will be busy for {estimate} and other clients will be slowed down. A recording of the run is saved as well.";
    public static string BenchStarting(string name) => $"Starting profile \"{name}\" and waiting for the server …";
    public const string BenchStartFailed = "The profile could not be started.", BenchCancelled = "Cancelled. The finished steps were saved.";
    public static string BenchDone(string d) => $"Done in {d}. The result was saved.";
    public const string BenchRegressionTitle = "Possible regression";
    public static string BenchRegressionAlert(double dropPct, double previous, double current) =>
        $"Generation speed dropped {dropPct.ToString("0.#", Inv)}% compared with the earlier run ({previous.ToString("0.0", Inv)} -> {current.ToString("0.0", Inv)} t/s).";
    // Kurze Form für Listen (Tabelle, Chip)
    public static string BenchRegressionShort(double dropPct) => $"-{dropPct.ToString("0.#", Inv)} %";
    public static string BenchFailed(string m) => $"Benchmark failed: {m}";
    public const string BenchStillRunning = "The benchmark is still running. Cancel it first.";
    public const string ColTest = "Test", ColResult = "Result", ColQuant = "Quant", ColSettings = "Settings", ColChatTps = "Chat t/s", ColBestPrompt = "Best prompt t/s", ColTool = "Tools";

    // „Modell suchen" in der Profilbearbeitung
    public const string ModelPickerTitle = "Find a model";
    public const string ModelPickerFind = "Find model…";
    public const string ModelPickerScan = "Search";
    public const string ModelPickerSearch = "filter by name or path";
    public const string ModelPickerSearching = "Searching the model folders …";
    public const string ModelPickerNone = "No .gguf found in these folders.";
    public static string ModelPickerFound(int n) => $"{n} model{(n == 1 ? "" : "s")} found";
    public const string ModelPickerPartial = "search stopped (folder too large)";
    public const string ColSize = "Size", ColParams = "Parameters", ColFile = "File";
    public const string BtnCopyMarkdown = "Copy as Markdown", BtnSaveMarkdown = "Save Markdown …", BtnSaveCsv = "Save CSV …";
    public const string BenchSeriesGen = "Generation t/s", BenchSeriesPrompt = "Prompt t/s", BenchChartTitle = "Speed by prompt length";
    public static string DeleteBenchConfirm(int n) => n == 1 ? "Delete the selected result?" : $"Delete the {n} selected results?";
}

// A-Fix: Sicherheit, Start/Stop, Dateien
public static partial class Strings
{
    public static string LaunchTimedOut(int minutes) => $"Did not start listening within {minutes} min. The process is still running.";
    public const string WorkingFolder = "Working folder", ProgramFolder = "(folder of the program)", Environment = "Environment";
    public const string ConfirmCommandTitle = "Confirm command line";
    public static string ConfirmCommandText(string name, string description) =>
        $"Start \"{name}\" with this command?\n\n{description}\n\nSecret values are hidden. You are asked again only when something in it changes.";
    public const string StopUnverifiedText =
        "Windows does not tell when this process was started, so the monitor cannot check that it is still the server you saw (the process id could have been reused).\n\nStop it anyway?";
    public static string ProxyRetryHint(string message, int seconds) => $"Proxy could not start: {message}. Retrying in {seconds} s.";
    public static string CsvLogOutside(string path) =>
        $"The request log path in settings.json points outside the data folder and is ignored for safety. Using {path} instead.";
    public const string TitleLibraryReadOnly = " · library read-only";
    public static string LibraryReadOnlyText(string path) =>
        $"The library file could not be read (locked by another program or on a drive that is not available):\n{path}\n\nSaved profiles and history are shown read-only for now; changes are not saved.";
    public static string LibraryRecoveredText(string path) => $"The library file was damaged and has been moved aside:\n{path}\n\nA new, empty library is used.";
    public static string RecordingLimitReached(int mb) => $"A recording reached the size limit of {mb} MB and was stopped and saved.";
    public const string BtnStopProcess = "Stop process";
    public const string LaunchStillRunning = "process still running";
    public const string AnotherInstance = "StykkerLLM is already running. The existing window was brought to the front.";
}

public static partial class Strings
{
    public const string LibraryAvailableAgain = "The library file is readable again. Saved profiles and history were reloaded.";

    // ── Stykker-Proxy ──
    public static string ProxyOn(int port) => $"● Proxy on :{port}";
    public static string ProxyOff => "○ Proxy off";
    public const string ProxyFailed = "The proxy could not be started.";
    public const string ProxyLanOn = "The Stykker-Proxy now answers in the network.";
    public const string ProxyLanOff = "The Stykker-Proxy now answers only on this PC.";

    // ── Heimnetz, Geräte, Server (Bereich 11 in docs/ui.md) ──
    public const string RemoteTitle = "Phone access";
    public const string MenuWebUi = "Web interface of the server";
    public const string BtnWebUi = "🌐 Web";
    // Der Server misst nur lesend, weil ein anderes Programm den Datenordner hält (S4: der Server ist die Engine)
    public const string ServerReadOnlyHint =
        "The Stykker server is measuring read-only: another program holds this data folder. Actions (starting, stopping, the proxy) are refused. "
        + "Close the other Stykker program and start the window again.";
    public const string RemoteOn = "Home/VPN on: reachable in the network";
    public const string RemoteOff = "Home/VPN off: only this PC can open the web interface";
    public const string RemoteThisPcOnly = "Only this PC";
    public const string RemoteHint = "Switch on Home/VPN to reach the web interface from your phone or another computer in the network. Windows asks once for the firewall.";
    public const string RemoteCodeLabel = "Access code";
    public const string RemoteScan = "Scan this with the phone camera to sign in";
    public const string RemoteThisBrowser = "Continue in this browser", RemoteEnterCode = "Enter the six digits shown on the PC (window ☰ → Phone access, or “stykker qr”).";
    public const string RemoteCodeWrong = "That code does not fit or has expired. Check the window or “stykker qr”.";
    public const string RemotePaired = "Signed in. This device appears in the list below.";
    public const string RemoteNewCode = "New code";
    public const string RemoteOnState = "Home/VPN on";
    public const string RemoteOffState = "Home/VPN off";
    public const string RemoteDevices = "Devices";
    public const string RemoteDevice = "Device";
    public static string RemoteDeviceCount(int n) => n == 1 ? "1 device" : $"{n} devices";
    public const string RemoteOpenPage = "Open the phone page";
    public const string RemoteFrom = "From";
    public const string CopyLink = "Copy link";
    public const string RemoteNoDevices = "No device has signed in yet.";
    public const string RemoteRemoveDevice = "Remove";
    public const string RemoteLastSeen = "last seen";

    // Rollen der angemeldeten Geräte: Admin darf alles wie das Fenster, Viewer sieht nur zu
    public const string RemoteRole = "Role";
    public const string RoleAdmin = "Admin";
    public const string RoleViewer = "Viewer";
    public const string RemoteMakeViewer = "Make viewer";
    public const string RemoteMakeAdmin = "Make admin";
    public const string RemoteRoleHint = "A viewer sees all values, but cannot start, stop, test or change anything.";
    public const string ViewerOnly = "This device may only look, not operate. An admin can change its role on the phone page.";
    public const string RemoteCheckRole = "Checking the role of this device …";
    public static string RoleUnknown(string role) => $"Unknown role '{role}'. Use \"{AccessRole.Admin}\" or \"{AccessRole.Viewer}\".";
    public const string ServerStopping = "The server is shutting down …";
    public const string ServerNotRunning = "The server is not running. Start it with “stykker web”.";
    public const string ServerStopConfirm =
        "Really shut down the server? The window starts it again on the next tick, and the phone page stops answering meanwhile.";
    public const string ServerReadOnly = "read-only";
    public const string ServerStopHint = "Stops the server (running tests are cancelled)";
    public const string On = "on";
    public const string Off = "off";
    public const string EvalPickSuite = "Pick at least one suite.";
    public const string RecordingDeleted = "The recording was deleted.";
    public const string ServerMissing = "StykkerLLM-Server.exe was not found next to StykkerUI.exe.";

    // Karte im Fenster, wenn der Server nicht antwortet (S4: der Server ist die Quelle, das Fenster misst nicht selbst)
    public const string ServerMissingTitle = "StykkerLLM-Server";
    public const string ServerMissingHint = "The window shows the data of its server. It is not answering right now.";
    public const string ServerStarting = "Starting the server …";
    public const string ServerStartFailed = "The server did not start:";
    public const string BtnStartServer = "Start server";
    public const string TitleServerMissing = " · no server";
    public const string ServerGone = "The server stopped answering.";
    public const string ActionFailed = "The server could not do that";
    public const string ServerStarted = "The server is running now: this window and the phone see the same data.";
    public const string BenchCancelling = "Cancelling the benchmark …";
    public const string BenchStepsDone = "steps done";
    public const string BenchNoResults = "No benchmark has been run yet.";

    // Einstellungen und About (auch im Web: Bereich 7 und 8)
    public const string SetLimitsTitle = "Limits and storage";
    public const string SetThemeTitle = "Theme", SetDataFolderTitle = "Data folder", SetManualServersTitle = "Servers added by URL";
    public const string SettingsTitle = "Settings";
    public const string SetZeroIsDefault = "(0 = default; older logs and recordings are removed first)";
    public const string SetStartTimeout = "Start timeout: minutes", SetInterval = "Measuring interval: ms (restart)";
    public const string SetGpuTop = "GPU: VRAM processes shown";
    public const string SetBenchRegression = "Benchmark: warn from % slower";
    public const string SetBenchRegressionHint = "A benchmark run slower than the earlier run of the same setup is marked (0 = never).";
    public const string SetOpenData = "Open data folder", SetOpenLogs = "Open logs";
    public const string SetPathData = "Data folder:", SetPathLogs = "Logs:", SetPathRecordings = "Recordings:", SetPathRequestLog = "Request log:";
    public const string SetBroken = "settings.json is unreadable, nothing was saved.";
    public const string SetServerHint = "The window shows the data of this server and sends all actions to it. The web interface, the phone and stykker act on the same data.";
    public const string SetMaxRecordings = "Recordings kept";
    public const string SetMaxRecordingsMb = "Total size of recordings (MB)";
    public const string SetMaxRecordingMb = "Size of one recording (MB)";
    public const string SetMaxLogs = "Log files kept";
    public const string SetMaxLogsMb = "Total size of logs (MB)";
    public const string SetMaxCsvMb = "Size of the request log (MB)";
    public const string AboutVersion = "Version";
    public const string AboutDataDir = "Data folder";
    public const string AboutServer = "Web interface";
    public const string AboutDriver = "Driver";
    public const string AboutOs = "System";
    public const string AboutLicense = "License: Business Source License 1.1 (free for private and non-commercial use)";
    public const string AboutStopServer = "Shut down the server";
    public const string AboutStopReally = "Really shut down";
    public const string AboutStopCancel = "Cancel";
    public const string SetSaved = "Saved.";
    public const string RecHintText = "Record what this server does (tokens/s, context, VRAM). Stops automatically at the limit.";
    public const string BackToMonitor = "Back to the monitor";
    public const string SlotsTitle = "Slots";
    public const string SlotNoSlots = "No slots (the server is not answering /slots).";
    public const string SecretsHidden = "This server has secrets (API key or token): they are shown as *** and never sent to a client.";
    public const string ColWorkingDir = "Working folder";
    public const string ColLogFile = "Log file";
    public const string LogOutsideFolder = "This log is not in the data folder, so the server does not pass it on. Open the file on this PC.";
    public const string BtnRefresh = "Refresh";
    public const string ServerTitle = "server";
    public static string RemoteDeviceLine(string name, string address, string when) =>
        string.IsNullOrEmpty(address) ? $"{name} · {when}" : $"{name} · {address} · {when}";
}

// R1: Simulator
public static partial class Strings
{
    public const string BtnSimulate = "Simulate…", TrayShowSimulator = "Simulate…", TitleSimulated = "SIMULATED · ", SimulatedBadge = "SIMULATED";
    public const string SimBanner = "SIMULATION: every server, number and file shown here is simulated. Nothing real is started, stopped or saved. Stop the simulation in the Simulator window to go back.";
    public const string SimulatorTitle = "Simulator", SimSectionServers = "SIMULATED SERVERS", SimSectionAdd = "ADD A SERVER", SimSectionOptions = "OPTIONS";
    public const string SimIntro = "Try the monitor without a real server. The simulation has its own temporary data folder: nothing lands in your library, history, recordings or request log, and Stop, Start and Proxy only act on the simulated servers. Everything the simulation recorded is discarded when you stop it.";
    public const string SimStateStopped = "stopped", SimStateRunning = "running", SimStatePaused = "paused (no new requests)";
    public const string SimBtnStart = "Start simulation", SimBtnPause = "Pause", SimBtnResume = "Resume", SimBtnStop = "Stop simulation";
    public const string SimBtnAdd = "Add server", SimBtnRemove = "Remove", SimBtnExamples = "Reset to example servers";
    public const string SimFieldType = "Type", SimFieldName = "Name", SimFieldModel = "Model", SimFieldContext = "Context (tokens, total)", SimFieldSlots = "Slots",
        SimFieldTpsMin = "t/s min", SimFieldTpsMax = "t/s max", SimFieldRpm = "Requests per minute", SimFieldThink = "Thinking share (%)",
        SimFieldTools = "Tool calls", SimFieldModelGb = "Model size (GB)", SimFieldDraft = "Draft model (tokens, 0 = off)";
    public const string SimOptGpu = "Simulated GPU values", SimOptSystem = "Simulated system values";
    public const string SimNoServers = "No simulated servers yet. Add one below or use the example servers.";
    public static string SimServerLine(SimServerSpec s) => s.Kind switch
    {
        BackendKind.Ollama => $"Ollama  ·  {s.Model}  ·  {s.ModelGb.ToString("0.#", Inv)} GB",
        BackendKind.LmStudio => $"LM Studio  ·  {s.Model}  ·  ctx {s.Context}  ·  {s.Slots} slots  ·  {s.TpsMin.ToString("0.#", Inv)}-{s.TpsMax.ToString("0.#", Inv)} t/s  ·  {s.RequestsPerMinute.ToString("0.#", Inv)}/min",
        _ => $"{(s.Strata ? "Strata" : "llama.cpp")}  ·  {(s.Name.Length > 0 ? s.Name : s.Model)}  ·  ctx {s.Context}  ·  {s.Slots} slots  ·  {s.TpsMin.ToString("0.#", Inv)}-{s.TpsMax.ToString("0.#", Inv)} t/s  ·  {s.RequestsPerMinute.ToString("0.#", Inv)}/min" +
             (s.ThinkPercent > 0 ? $"  ·  think {s.ThinkPercent} %" : "") + (s.ToolCalls ? "  ·  tools" : ""),
    };
    public const string SimNumbersInvalid = "Please enter numbers in the numeric fields.";
    public const string SimRealSkipNote = "The real monitor keeps measuring in the background while the simulation runs.";
}

// TUI: die Texte der /-Befehle und ihrer Menüs (Bereich 3, 5, 8 und 10 aus docs/ui.md)
public static partial class Strings
{
    public const string TuiSaved = "saved profiles (menu below)";
    public static string TuiSavedMenu => "menu: /saved <name> rename <new name> | note <text> | args <tokens> | bench | remove";
    public const string TuiSavedColumn = "START";
    public const string TuiHistory = "servers that ran before (search, save, forget)";
    public static string TuiHistoryMenu => "menu: /history <text> finds · /history save [name] <id> remembers · /history forget <id> removes";
    public const string TuiAdd = "add a server by URL (like “Add server” in the window)";
    public const string TuiAddUsage = "usage: /add <url> [name] [logfile]";
    public const string TuiRemove = "remove a server that was added by URL";
    public static string TuiRemoveUsage => "usage: /remove <server>";
    public const string TuiRecord = "start or stop a recording (all servers by default)";
    public const string TuiRecordUsage = "usage: /record [all|<server>]";
    public const string TuiRecordings = "saved recordings: list, details, compare, delete";
    public static string TuiRecordingsMenu => "menu: /recordings <id> shows · /recordings compare <a> <b> · /recordings delete <id>";
    public const string TuiRecordingsUsage = "usage: /recordings [<id>|compare <a> <b>|delete <id>]";
    public const string TuiGpu = "GPU details: clocks, throttling, VRAM and RAM per process";
    public static string TuiFreeVram = "free VRAM: unload models, stop local servers (asks first)";
    public static string TuiFreeVramConfirm => FreeVramConfirm("• the loaded Ollama / LM Studio models\n• the local llama.cpp servers the monitor started");
    public static string TuiRemoveServerConfirm(string name) => $"Remove the server \"{name}\"? It was added by URL; its history stays.";
    public const string TuiBench = "benchmark results; run one with /bench run <server>";
    public static string TuiBenchRunUsage => "usage: /bench run <server> [chat] [tool] [parallel] [ctx=4096,8192] [gen=256] [slots=4] [repeats=1] [seed=42]";
    public const string TuiNoRecording = "Nothing to stop: no recording is running.";
    public const string TuiServerOnly = "Only the Stykker server does this – start it with “stykker web”.";
    public static string TuiSavedRemoved(string name) => $"removed profile {name}";
    public static string TuiHistorySaved(string name) => $"saved as profile {name}";
    public static string TuiHistoryForgotten(string name) => $"forgotten: {name}";
    public static string TuiRenamed(string from, string to) => $"renamed {from} to {to}";
    public const string TuiArgsSet = "command line updated";
    public const string TuiAdded = "added: ";
    public const string TuiRemoved = "removed: ";
    public const string TuiGpuNone = "no GPU (NVML not available here)";
    public const string TuiGpuWaiting = "still measuring – the first tick is still running …";
    public static string TuiMemSum(int shown, double gb) => $"{shown} shown · {N1(gb)} GB";

    // TUI: die Hilfe der Befehle, die keine eigene Menüzeile haben
    public const string TuiStatus = "measure now and print the full status table";
    public const string TuiShow = "details: command line, parameters, runs";
    public const string TuiStart = "start a saved profile or a history entry";
    public const string TuiStop = "stop a running server (asks first)";
    public const string TuiUnload = "Ollama / LM Studio: unload a model";
    public const string TuiProxy = "Stykker-Proxy: state, on/off, network, served model";
    public const string TuiNotice = "the standing hint; dismiss clears it";
    public const string TuiRecent = "the last requests: server, task, tokens, time, status";
    public const string TuiEval = "model tests: queue, ranking, test models, own suites";
    public const string TuiServer = "the measuring server: show, start, stop";
    public const string TuiWeb = "start the web interface (the server does the measuring)";
    public const string TuiRemote = "reach the web interface from the network";
    public const string TuiQr = "QR code and code for phone access";
    public const string TuiDevices = "signed-in devices; with an id: remove it";
    public const string TuiApprove = "let in a device that shows six digits (phone, tablet, a hub)";
    public const string TuiNodes = "other PCs paired with this one: list, search, pair, remove, run an action there";
    public const string TuiRole = "role of a signed-in device: viewer only looks, admin may operate";
    public const string TuiAbout = "version, data folder, license";
    public const string TuiClear = "clear the output";
    public const string TuiHelp = "commands and keys";
    public const string TuiQuit = "leave (or Ctrl+C twice)";
}
