namespace StykkerLlm.Core;

// Texte für Saved/Running/History, Bearbeiten-Fenster und Start (AP4)
public static partial class Strings
{
    // Statusleiste im Kopf (U3)
    public const string HdrProxy = "Proxy", HdrProxyOff = "off";
    public static string HdrServers(int n, int busy) => busy > 0 ? $"{n} · {busy} busy" : n.ToString(Inv);
    public const string HdrServersTip = "Model servers running · busy right now", HdrProxyTip = "Stykker-Proxy: one address for all models";
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
    public const string NoLogLines = "(no log output)";

    // Verlauf
    public const string ColModel = "Model", ColCtx = "Context", ColLastSeen = "Last seen", ColRuns = "Runs", ColBest = "Best t/s",
        ColAvg = "Avg t/s", ColVram = "Max VRAM", ColSavedFlag = "";

    public const string FieldName = "Name", FieldNote = "Note",
        FieldParams = "Parameters (option and value, one per row)", FieldRaw = "Command line (arguments only)", Browse = "Browse …";
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
    public const string SaveBlockNoCommand = "This entry has no command line, so it cannot be saved as a profile.";
    public const string RemoveManual = "Remove";
    public const string KindAuto = "Auto-detect";
}

// Details-Fenster (AP5)
public static partial class Strings
{
    public const string SecCommand = "Command line (secrets are hidden)",
        SecMemory = "Memory and CPU", SecTemplate = "Chat template", SecLog = "Log (last 50 lines)";
    public const string ColOption = "Option", ColValue = "Value", ColProperty = "Property", ColTime = "Time", ColTask = "Task",
        ColPromptTok = "Prompt tok", ColPromptTps = "Prompt t/s", ColReplyTok = "Reply tok", ColReplyTps = "Reply t/s", ColSeconds = "Seconds";
    // Art der Anfrage (0/1/2 wie im Serverprotokoll); dieselbe Spalte in Fenster, Web und TUI
    public static string TaskName(int task) => task switch { 0 => "completion", 1 => "chat", 2 => "embedding", _ => task.ToString(Inv) };
    public const string NoParameters = "(no parameters)",
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
    public const string NotRunning = "not running", RunningNothingLoaded = "running, nothing loaded";
    // Engine (llama-server), die LM Studio selbst gestartet hat: gehört zur Karte, kein Stop, kein Save
    public const string StaysLoaded = "stays loaded", Unloading = "unloading …";
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

    // About / start with Windows / export
    public const string AboutTitle = "About StykkerLLM";
    public const string HistorySearch = "Search history …";
}

// Aufnahme und Benchmark (AP8/AP9)
public static partial class Strings
{
    public const string NoSamples = "No data for this metric.";
}

public static partial class Strings
{
    public static string N2(double v) => v.ToString("N2", Inv);
    public const string RecordingsTitle = "Recordings", CompareTitle = "Compare recordings";
    public const string BtnView = "View", BtnCompare = "Compare", BtnDelete = "Delete";
    public const string ModeProxy = "with proxy: thinking and answer are told apart", ModeLogOnly = "log only: thinking and answer are shown together as generation";
    public const string ModeProxyShort = "proxy", ModeLogShort = "log";
    public const string TileRequests = "Requests", TileBusy = "Model busy", TileTokens = "Tokens", TileCache = "Cache hits",
        TileTps = "Tokens/s", TileVram = "Max VRAM", TileEnergy = "Energy", TileTtft = "First token p50 / p95";
    public const string ColStarted = "Started",
        ColSlots = "slots", ColGenTps = "Gen t/s";
    public const string NoActiveRecording = "No recording is running.", AllServers = "all servers";
    public const string RecordingsEmpty = "Pick two recordings to compare (Recordings → tick two → Compare).", CompareDelta = "Delta";
    public const string BtnUnselectAll = "Unselect";
    public static string ComparePickTwo(int n) => n == 2 ? "Compare the two selected recordings" : $"Tick two recordings to compare (now {n})";
}

public static partial class Strings
{
    public const string GlobalRecordingRunning = "A recording of all servers is running. Stop it first.";
    public const string NothingToRecord = "There is no server to record yet.";
    public const string ReadOnlyEngine = "Read-only view: another Stykker window uses this data folder, so nothing is changed or recorded here.";
    public static string RecordingSaved(int requests, string duration) => $"Recording saved: {requests} requests, {duration}. See Recordings.";
    public const string RecordingFailed = "The recording could not be saved.";
    public static string ProxyStartFailed(string msg) => $"The proxy could not be started:\n{msg}";

    // „Stykker-Proxy": ein Proxy, Routing nach Modell
    public const string ProxyNoTarget = "The Stykker-Proxy has no target: no server is running.";
    public const string ProxyTargetLoading = "The target server is still loading its model. Please try again in a moment.";
    public const string ProxyTargetOffline = "The target server is offline.";
    public const string ProxyTargetNone = "none";
    public static string ProxyBar(bool on, int port, string target) =>
        on ? $"Stykker-Proxy: on · 127.0.0.1:{port} · target: {target}" : "Stykker-Proxy: off";
    public static string ProxyBar(bool on, int port, string target, bool lan) =>
        on ? $"Stykker-Proxy: on · {(lan ? "LAN" : "127.0.0.1")}:{port} · target: {target}" : "Stykker-Proxy: off";
    // WinForms-Kopfzeile: Schalter (zeigt den Zustand, Klick schaltet um) und Einstellungen
    public static string BtnProxySwitchOn(int port) => $"● Proxy on :{port}";
    public const string BtnProxySwitchOff = "○ Proxy off", BtnProxySettings = "Proxy settings …";
    public const string ProxyTargetAuto = "Auto";
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
    // Befehlszeile der TUI und von "stykker proxy" (im Fenster gibt es die Schalter, im Web die Karte)
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
    public const string ProxyProviderKeyMissingShort = "No API key stored for this provider.";
    public const string ProxyProviderUnknown = "This provider is not in the list.";
    public const string ProxyProviderAdded = "Provider added.";
    public const string ProxyProviderRemoved = "Provider removed.";
    public const string ProxyProviderKeySet = "The key was saved.";
    public const string BtnProxyAddProvider = "Add provider", BtnProxyProviderKey = "New key";
    // Antworten des Proxys, wenn ein Cloud-Modell ohne Schlüssel oder ohne Erreichbarkeit angesprochen wird
    public const string ProxyProviderNoKey = "This model needs an API key. Add the key for this provider in the proxy settings.";
    public const string ProxyProviderOfflineProxy = "The provider is not reachable. Check the base URL and the key.";

    // Anhaltende Hinweiszeile (bleibt stehen, bis sie bestätigt wird)
    public const string NoticeHint = "Hint", NoticeAlarm = "Attention";
}

public static partial class Strings
{
    public const string BenchTitle = "Benchmark", BenchTarget = "Server or saved profile to measure";
    public const string BenchHint = "A fixed test series with fixed prompts (prompt cache off, temperature 0, seed 42), so results can be repeated and compared.";
    public const string BenchChat = "Short chat", BenchTool = "Tool call", BenchParallel = "Parallel requests (needs -np > 1)", BenchContexts = "Context sizes (prompt tokens)";
    public const string BenchRepeats = "Repeats", BenchGenTokens = "Tokens per test";
    public const string BenchRun = "Run benchmark …", BenchResults = "Saved results", BenchWillStart = "(not running: will be started)";
    public const string BenchNoTarget = "There is no running llama.cpp server and no saved profile to measure.";
    public const string BenchNothingSelected = "Select at least one test.";
    public static string BenchStarting(string name) => $"Starting profile \"{name}\" and waiting for the server …";
    public const string BenchStartFailed = "The profile could not be started.", BenchCancelled = "Cancelled. The finished steps were saved.";
    public static string BenchDone(string d) => $"Done in {d}. The result was saved.";
    public const string BenchRegressionTitle = "Possible regression";
    public static string BenchRegressionAlert(double dropPct, double previous, double current) =>
        $"Generation speed dropped {dropPct.ToString("0.#", Inv)}% compared with the earlier run ({previous.ToString("0.0", Inv)} -> {current.ToString("0.0", Inv)} t/s).";
    public static string BenchFailed(string m) => $"Benchmark failed: {m}";
    public const string ColTest = "Test", ColResult = "Result", ColChatTps = "Chat t/s", ColBestPrompt = "Best prompt t/s";

    public const string BtnCopyMarkdown = "Copy as Markdown";
    public const string BenchSeriesGen = "Generation t/s";
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
    public static string CsvLogOutside(string path) =>
        $"The request log path in settings.json points outside the data folder and is ignored for safety. Using {path} instead.";
    public static string RecordingLimitReached(int mb) => $"A recording reached the size limit of {mb} MB and was stopped and saved.";
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
    public const string RemoteThisBrowser = "Continue in this browser", RemoteEnterCode = "Enter the six digits shown on the PC (☰ → Phone access, or the key c in “stykker”).";
    public const string RemoteCodeWrong = "That code does not fit or has expired. Check ☰ → Phone access, or the key c in “stykker”.";
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
    public const string ServerReadOnly = "read-only";
    public const string ServerStopHint = "Stops the server (running tests are cancelled)";
    public const string On = "on";
    public const string Off = "off";
    public const string EvalPickSuite = "Pick at least one suite.";
    public const string RecordingDeleted = "The recording was deleted.";

    public const string ServerGone = "The server stopped answering.";
    public const string ActionFailed = "The server could not do that";
    public const string BenchCancelling = "Cancelling the benchmark …";
    public const string BenchStepsDone = "steps done";
    public const string BenchNoResults = "No benchmark has been run yet.";

    // Einstellungen und About (auch im Web: Bereich 7 und 8)
    public const string SetLimitsTitle = "Limits and storage";
    public const string SettingsTitle = "Settings";
    public const string SetBenchRegression = "Benchmark: warn from % slower";
    public const string SetBenchRegressionHint = "A benchmark run slower than the earlier run of the same setup is marked (0 = never).";
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
    public const string BackToMonitor = "Back to the monitor";
    public const string SlotsTitle = "Slots";
    public const string SlotNoSlots = "No slots (the server is not answering /slots).";
    public const string SecretsHidden = "This server has secrets (API key or token): they are shown as *** and never sent to a client.";
    public const string ColWorkingDir = "Working folder";
    public const string ColLogFile = "Log file";
    public const string LogOutsideFolder = "This log is not in the data folder, so the server does not pass it on. Open the file on this PC.";
    public const string BtnRefresh = "Refresh";
}

// R1: Simulator
public static partial class Strings
{
    public const string SimFieldTpsMin = "t/s min", SimFieldTpsMax = "t/s max", SimFieldRpm = "Requests per minute", SimFieldThink = "Thinking share (%)",
        SimFieldTools = "Tool calls", SimFieldModelGb = "Model size (GB)", SimFieldDraft = "Draft model (tokens, 0 = off)";
}

// TUI: die Texte der /-Befehle und ihrer Menüs (Bereich 3, 5, 8 und 10 aus docs/ui.md)
public static partial class Strings
{
    public static string TuiFreeVram = "free VRAM: unload models, stop local servers (asks first)";

}
