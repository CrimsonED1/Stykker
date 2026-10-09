using System.Text.Json;
using System.Text.Json.Serialization;
using StykkerLlm.Core.Eval;

namespace StykkerLlm.Core;

// ── Der Zustand des Servers als JSON (Schema 1) ──
// Der Server schreibt ihn für Fenster, TUI und Skripte; die Clients lesen ihn in StateSnapshot. Ohne Reflection
// (Utf8JsonWriter und JsonDocument), später NativeAOT-tauglich. Felder nur hinzufügen, nie umbenennen.
public static class StateJson
{
    public const int Schema = 1;
    public const string KeyHeader = "X-Stykker-Key";      // interner Schlüssel für Fenster und TUI auf diesem Rechner
    public const string CookieName = "stykker_device";       // Gerätecookie für Browser (Fenster und Web)
    public const string DeviceHeader = "X-Stykker-Device";  // dasselbe Gerätetoken als Kopf (StykkerUI prüft so sein gespeichertes Gerät) ab

    // Zwei bis drei hundert Zeilen pro Server, einmal je Takt: deshalb Historie und Rohdaten (/props) nur, wenn gebraucht
    private static double R2(double v) => Math.Round(v, 2);
    private static double? R2(double? v) => v is double d && double.IsFinite(d) ? Math.Round(d, 2) : null;

    public static string WriteText(MonitorEngine e, AccessControl? access, EvalQueue? queue, int serverPort, DateTimeOffset now, bool withHistory = true, bool withCode = true) =>
        Write(e, access, queue, serverPort, now, withHistory, indented: false, withCode);

    // withCode = false für ein Gerät mit der Rolle Viewer: der Zugangscode würde ihm erlauben, sich als Admin
    // anzumelden. Die Geräteliste bleibt stehen (nur ohne Rolle ändern kann sie ohnehin nichts).
    public static string Write(MonitorEngine e, AccessControl? access, EvalQueue? queue, int serverPort, DateTimeOffset now, bool withHistory, bool indented, bool withCode = true)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = indented, SkipValidation = false }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteString("time", now.ToString("yyyy-MM-ddTHH:mm:sszzz"));
            w.WriteNumber("ticks", e.Ticks);
            w.WriteNumber("intervalMs", e.Settings.IntervalMs);
            w.WriteNumber("serverPort", serverPort);
            w.WriteBoolean("readOnly", e.ReadOnly);
            w.WriteBoolean("simulated", e.IsSimulated);
            Str(w, "csvNote", e.CsvLogNote);

            if (e.Gpu is { } g)
            {
                w.WriteStartObject("gpu");
                w.WriteString("name", g.Name);
                Num(w, "util", R2(g.Util));
                Num(w, "vramUsedGb", R2(g.MemUsedGb));
                Num(w, "vramTotalGb", R2(g.MemTotalGb));
                Num(w, "vramFreeGb", R2(g.MemFreeGb));
                Num(w, "memUtil", R2(g.MemUtil));
                Num(w, "powerW", R2(g.PowerW));
                Num(w, "powerLimitW", R2(g.PowerLimitW));
                Num(w, "tempC", R2(g.TempC));
                Num(w, "gfxMhz", R2(g.GfxMhz));
                Num(w, "gfxMaxMhz", R2(g.GfxMaxMhz));
                Num(w, "memMhz", R2(g.MemMhz));
                Num(w, "memMaxMhz", R2(g.MemMaxMhz));
                w.WriteBoolean("throttled", g.Throttled);
                w.WriteString("throttle", g.ThrottleText());
                w.WriteEndObject();
            }
            else w.WriteNull("gpu");

            if (e.Sys is { } s)
            {
                w.WriteStartObject("system");
                NumN(w, "cpu", s.CpuPercent);
                w.WriteNumber("cores", s.Cores);
                Num(w, "ramUsedGb", R2(s.RamUsedGb));
                Num(w, "ramTotalGb", R2(s.RamTotalGb));
                Num(w, "commitUsedGb", R2(s.CommitUsedGb));
                Num(w, "commitLimitGb", R2(s.CommitLimitGb));
                w.WriteEndObject();
            }
            else w.WriteNull("system");

            WriteTop(w, "vramTop", e.VramTop);
            WriteTop(w, "ramTop", e.RamTop);
            WriteUtilTop(w, e.GpuUtilTop);

            w.WriteStartArray("servers");
            foreach (var sv in e.Servers) WriteServer(w, e, sv, withHistory);
            w.WriteEndArray();

            // Vom Monitor gestartete Server, die noch nicht lauschen ("starting …") oder fehlgeschlagen sind
            w.WriteStartArray("launches");
            foreach (var l in e.Registry.Launches) WriteLaunch(w, l);
            w.WriteEndArray();

            w.WriteStartObject("proxy");
            w.WriteBoolean("enabled", e.Settings.ProxyEnabled);
            w.WriteBoolean("running", e.Proxies.AnyRunning);
            w.WriteNumber("port", e.Proxies.Port);
            w.WriteBoolean("bindLan", e.Proxies.BindLan);
            w.WriteString("targetKey", e.Settings.ProxyTarget);
            w.WriteString("targetValue", e.Proxies.ChoiceValue());   // der Wert, den die Auswahl zeigen muss (leer = Auto)
            w.WriteString("targetName", e.Proxies.TargetName());
            w.WriteString("servedKey", e.Proxies.ServedKey() ?? "");   // welcher Server gerade als „stykker" serviert wird (Chip auf der Karte)
            w.WriteNumber("secondsToRetry", e.Proxies.FailureFor("", out var retry) != null ? retry : 0);
            // Cloud-Anbieter (Nr. 46): Name, Basis-URL, ob ein Schlüssel hinterlegt ist, die Modelle und der letzte Fehler.
            // Der Schlüssel selbst steht nicht im Zustand.
            w.WriteStartArray("providers");
            foreach (var p in e.Proxies.ProviderStates())
            {
                w.WriteStartObject();
                w.WriteString("name", p.Name);
                w.WriteString("url", p.Url);
                w.WriteBoolean("hasKey", p.HasKey);
                w.WriteBoolean("ready", p.Ready);
                w.WriteStartArray("models");
                foreach (var m in p.Models) w.WriteStringValue(m);
                w.WriteEndArray();
                w.WriteString("error", p.Error);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            // Die Auswahl „serviertes Modell": lokale Modelle und Cloud-Modelle, jeder mit dem Wert der Auswahl
            w.WriteStartArray("choices");
            foreach (var c in e.Proxies.Choices())
            {
                w.WriteStartObject();
                w.WriteString("value", c.Value);
                w.WriteString("display", c.Display);
                w.WriteBoolean("cloud", c.Cloud);
                // Bei einem Cloud-Modell der Name, unter dem der Client es anfragt: „<Anbieter>/<Modell>"
                w.WriteString("publicModel", c.Cloud ? c.Model : "");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            // Die angehängten Stykker-Rechner (S4: das Fenster zeigt sie aus dem Zustand, nicht aus seiner Datei)
            w.WriteStartArray("remotes");
            foreach (var r in e.Proxies.Remotes)
            {
                w.WriteStartObject();
                w.WriteString("name", r.Name);
                w.WriteString("url", r.Url);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            // Claude Desktop auf den Stykker-Proxy umgeschaltet (Schalter im Proxy-Panel)
            w.WriteStartObject("claude");
            w.WriteBoolean("supported", e.Claude.Supported);
            w.WriteBoolean("enabled", e.Claude.Enabled);
            w.WriteBoolean("running", e.Claude.Running);
            w.WriteNumber("runningCount", e.Claude.RunningCount);
            w.WriteString("url", e.Claude.BaseUrl);
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartObject("recording");
            w.WriteBoolean("global", e.Recorder.IsRecordingGlobal);
            w.WriteStartArray("sessions");
            foreach (var sess in e.Recorder.Sessions)
            {
                w.WriteStartObject();
                w.WriteString("key", sess.Target);
                w.WriteString("id", sess.Id);
                w.WriteString("started", sess.Started.ToString("o"));
                Num(w, "elapsedSec", R2(sess.ElapsedSec));
                Num(w, "lastTps", R2(sess.LastTps));
                w.WriteNumber("bytes", sess.Bytes);
                w.WriteBoolean("global", sess.IsGlobal);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartArray("recordings");
            foreach (var rec in RecordingStore.List(e.Paths.RecordingsDir))
                WriteRecording(w, rec);
            w.WriteEndArray();

            w.WriteStartArray("profiles");
            foreach (var p in e.Library.Profiles)
            {
                w.WriteStartObject();
                w.WriteString("id", p.Id.ToString("N"));
                w.WriteString("key", p.Key);
                w.WriteString("name", p.Name);
                Str(w, "program", p.Program);
                w.WriteStartArray("args");
                foreach (var a in p.Args) w.WriteStringValue(a);
                w.WriteEndArray();
                w.WriteStartObject("env");
                foreach (var (k, v) in p.Env) w.WriteString(k, v);
                w.WriteEndObject();
                Str(w, "workingDir", p.WorkingDir);
                w.WriteNumber("port", p.Port ?? 0);
                Str(w, "modelPath", p.ModelPath);
                Str(w, "note", p.Note);
                w.WriteBoolean("hasSecrets", p.HasSecrets);
                w.WriteString("created", p.Created.ToString("o"));
                Str(w, "lastStarted", p.LastStarted?.ToString("o"));
                w.WriteBoolean("restartOnCrash", p.RestartOnCrash);   // see docs/ui.md
                w.WriteNumber("maxRestarts", p.MaxRestarts);
                w.WriteNumber("unloadAfterIdleMin", p.UnloadAfterIdleMin);   // see docs/ui.md
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("history");
            foreach (var h in e.Library.History)
            {
                w.WriteStartObject();
                w.WriteString("key", h.Key);
                w.WriteString("name", h.Name);
                Str(w, "program", h.Program);
                w.WriteStartArray("args");
                foreach (var a in h.Args) w.WriteStringValue(a);
                w.WriteEndArray();
                w.WriteStartObject("env");
                foreach (var (k, v) in h.Env) w.WriteString(k, v);
                w.WriteEndObject();
                Str(w, "workingDir", h.WorkingDir);
                w.WriteString("modelPath", h.ModelPath);
                w.WriteNumber("port", h.Port ?? 0);
                w.WriteNumber("ctx", h.Ctx ?? 0);
                w.WriteBoolean("hasSecrets", h.HasSecrets);
                w.WriteString("firstSeen", h.FirstSeen.ToString("o"));
                w.WriteString("lastSeen", h.LastSeen.ToString("o"));
                w.WriteNumber("runs", h.Runs);
                Num(w, "totalSeconds", R2(h.TotalSeconds));
                Num(w, "bestTps", R2(h.BestTps));
                Num(w, "meanTps", R2(h.MeanTps));
                NumN(w, "maxVramGb", h.MaxVramGb);
                NumN(w, "modelSizeGb", h.ModelSizeGb);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("benchmarks");
            foreach (var b in e.Library.Benchmarks)
            {
                w.WriteStartObject();
                w.WriteString("id", b.Id.ToString("N"));
                w.WriteString("started", b.Started.ToString("o"));
                Num(w, "durationSec", R2(b.DurationSec));
                w.WriteString("title", b.Title);
                w.WriteString("server", b.Server);
                w.WriteString("model", b.Model);
                w.WriteString("quant", b.Quant);
                w.WriteString("gpu", b.Gpu);
                w.WriteString("build", b.Build);
                w.WriteNumber("contextPerSlot", b.ContextPerSlot);
                w.WriteNumber("slots", b.Slots);
                w.WriteBoolean("cancelled", b.Cancelled);
                Str(w, "recordingFile", b.RecordingFile);
                if (b.Regression is { } reg)
                {
                    w.WriteStartObject("regression");
                    w.WriteString("title", Strings.BenchRegressionTitle);
                    Num(w, "dropPct", R2(reg.DropPct));
                    Num(w, "previous", R2(reg.Previous));
                    Num(w, "current", R2(reg.Current));
                    w.WriteEndObject();
                }
                w.WriteStartArray("steps");
                foreach (var st in b.Steps)
                {
                    w.WriteStartObject();
                    w.WriteString("kind", st.Kind);
                    w.WriteString("name", st.Name);
                    w.WriteNumber("targetPromptTokens", st.TargetPromptTokens);
                    Num(w, "promptTps", R2(st.PromptTps));
                    Num(w, "genTps", R2(st.GenTps));
                    Num(w, "totalSec", R2(st.TotalSec));
                    w.WriteBoolean("ok", st.Ok);
                    Str(w, "note", st.Note);
                    Num(w, "aggregateTps", R2(st.AggregateTps));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            if (queue != null)
            {
                w.WriteStartObject("eval");
                w.WriteBoolean("running", queue.Running);
                w.WriteBoolean("allowCode", queue.AllowCode);
                Str(w, "python", queue.Python?.Executable);
                w.WriteString("llamaServer", queue.Settings.LlamaServer);
                w.WriteStartArray("modelRoots");
                foreach (var r in queue.Settings.ModelRoots) w.WriteStringValue(r);
                w.WriteEndArray();
                w.WriteStartArray("models");
                foreach (var m in queue.Models)
                {
                    w.WriteStartObject();
                    w.WriteString("id", m.Id);
                    w.WriteString("name", m.Name);
                    w.WriteString("exe", m.Exe);
                    w.WriteString("args", m.Args);
                    w.WriteNumber("port", m.Port);
                    w.WriteBoolean("enabled", m.Enabled);
                    w.WriteString("source", m.Source);
                    w.WriteString("modelFile", m.ModelFile);
                    Num(w, "sizeGb", R2(m.SizeGb));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("jobs");
                foreach (var job in queue.Jobs)
                {
                    w.WriteStartObject();
                    w.WriteString("id", job.Id.ToString("N"));
                    w.WriteString("modelId", job.Model.Id);
                    w.WriteString("model", job.Model.Name);
                    w.WriteString("suite", job.Suite);
                    w.WriteNumber("repeat", job.Repeat);
                    w.WriteString("state", job.State.ToString().ToLowerInvariant());
                    w.WriteString("note", job.Note);
                    w.WriteNumber("done", job.Done);
                    w.WriteNumber("total", job.Total);
                    w.WriteString("current", job.Current);
                    Num(w, "score", R2(job.Score));
                    Str(w, "started", job.Started?.ToString("o"));
                    Str(w, "finished", job.Finished?.ToString("o"));
                    w.WriteStartArray("live");
                    foreach (var t in job.Live.TakeLast(30))
                    {
                        w.WriteStartObject();
                        w.WriteString("id", t.Id);
                        w.WriteString("title", t.Title);
                        w.WriteString("category", t.Category);
                        Num(w, "score", R2(t.Score));
                        w.WriteBoolean("passed", t.Passed);
                        w.WriteBoolean("skipped", t.Skipped);
                        w.WriteBoolean("error", t.Error);
                        Str(w, "note", t.Note);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteStartArray("runs");
                    foreach (var r in job.Runs)
                    {
                        w.WriteStartObject();
                        w.WriteString("id", r.Id.ToString("N"));
                        w.WriteString("started", r.Started.ToString("o"));
                        Num(w, "durationSec", R2(r.DurationSec));
                        Num(w, "score", R2(r.Total));
                        w.WriteNumber("repeat", r.Repeat);
                        w.WriteBoolean("cancelled", r.Cancelled);
                        Num(w, "genTps", R2(r.Tasks.Count > 0 ? r.Tasks.Average(t => t.GenTps) : 0));
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            else w.WriteNull("eval");

            w.WriteStartObject("settings");
            w.WriteString("theme", e.Settings.Theme);
            w.WriteNumber("intervalMs", e.Settings.IntervalMs);
            w.WriteString("dataDir", e.Paths.Root);
            w.WriteNumber("maxRecordings", e.Settings.MaxRecordings);
            w.WriteNumber("maxRecordingsMb", e.Settings.MaxRecordingsMb);
            w.WriteNumber("maxRecordingMb", e.Settings.MaxRecordingMb);
            w.WriteNumber("maxLogFiles", e.Settings.MaxLogFiles);
            w.WriteNumber("maxLogsMb", e.Settings.MaxLogsMb);
            w.WriteNumber("maxCsvMb", e.Settings.MaxCsvMb);
            w.WriteNumber("startTimeoutMin", e.Settings.StartTimeoutMin);
            w.WriteNumber("gpuTopCount", e.Settings.GpuTopCount);
            w.WriteNumber("benchRegressionPct", e.Settings.BenchRegressionPct);
            w.WriteNumber("serverPort", e.Settings.ServerPort);
            Str(w, "csvLog", e.Settings.CsvLog);
            w.WriteStartArray("manualServers");
            foreach (var m in e.Settings.ManualServers)
            {
                w.WriteStartObject();
                w.WriteString("name", m.Name);
                w.WriteString("url", m.Url);
                Str(w, "log", m.Log);
                Str(w, "kind", m.Kind);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartObject("access");
            w.WriteBoolean("remote", access?.RemoteEnabled ?? false);
            var code = withCode ? access?.Code ?? "" : "";
            w.WriteString("code", code);
            w.WriteNumber("port", serverPort);
            Str(w, "lanAddress", NetInfo.LanAddress());
            Str(w, "lanAddressAll", string.Join(",", NetInfo.Ipv4Addresses()));
            w.WriteString("pairUrl", code.Length == 0 ? "" : NetInfo.PairUrl(serverPort, code));
            w.WriteString("url", NetAddr.Url("127.0.0.1", serverPort));
            // Ablauf des Codes (sechs Ziffern, zehn Minuten) – nur wer den Code sieht, braucht die Zeit
            if (withCode && access != null) w.WriteString("codeExpires", access.CodeExpires.ToString("o"));
            // Offene Anfragen von Geräten, die selbst einen Code zeigen. Der Code steht hier absichtlich nicht:
            // wer freigibt, muss ihn am neuen Gerät ablesen (sonst gäbe jeder Admin blind frei).
            w.WriteStartArray("requests");
            if (withCode && access != null)
                foreach (var r in access.PendingRequests)
                {
                    w.WriteStartObject();
                    w.WriteString("id", r.Id);
                    w.WriteString("name", r.Name);
                    w.WriteString("kind", r.Kind);
                    Str(w, "address", r.Address);
                    w.WriteString("expires", r.Expires.ToString("o"));
                    w.WriteEndObject();
                }
            w.WriteEndArray();
            w.WriteStartArray("devices");
            if (access != null)
                foreach (var d in access.Devices)
                {
                    w.WriteStartObject();
                    w.WriteString("id", d.Id);
                    w.WriteString("name", d.Name);
                    w.WriteString("role", d.Role);
                    w.WriteString("created", d.Created.ToString("o"));
                    Str(w, "lastSeen", d.LastSeen?.ToString("o"));
                    Str(w, "address", d.Address);
                    w.WriteEndObject();
                }
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartArray("notices");
            foreach (var n in Notices.TakeLast(8)) w.WriteStringValue(n);
            w.WriteEndArray();

            // Der anhaltende Hinweis (Bereich 2 in docs/ui.md): Text, Warnung und die Logdatei zum Nachschlagen
            if (e.CurrentNotice is { } notice)
            {
                w.WriteStartObject("notice");
                w.WriteString("text", notice.Text);
                w.WriteBoolean("alarm", notice.Alarm);
                w.WriteString("logFile", notice.LogFile ?? "");
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    // Kurze Meldungen (Aufnahme gesichert, Skript beendet …); der Server hält die letzten für die Oberflächen fest
    public static List<string> Notices { get; } = new();

    public static void Notice(string text)
    {
        lock (Notices) Notices.Add(text);
    }

    private static void WriteTop(Utf8JsonWriter w, string name, IReadOnlyList<(string Name, double Gb)> top)
    {
        w.WriteStartArray(name);
        foreach (var (n, gb) in top)
        {
            w.WriteStartObject();
            w.WriteString("name", n);
            Num(w, "gb", R2(gb));
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    // GPU-Auslastung je Prozess (für die Liste in den GPU-Details; ohne Wert 0)
    private static void WriteUtilTop(Utf8JsonWriter w, IReadOnlyList<(string Name, double Percent)> top)
    {
        w.WriteStartArray("gpuUtilTop");
        foreach (var (n, pct) in top)
        {
            w.WriteStartObject();
            w.WriteString("name", n);
            Num(w, "pct", R2(pct));
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteServer(Utf8JsonWriter w, MonitorEngine e, ServerWatcher s, bool withHistory)
    {
        var i = s.Info;
        w.WriteStartObject();
        w.WriteString("key", s.Key);
        w.WriteString("name", s.Name);
        w.WriteString("model", s.Model);
        w.WriteString("url", s.Url);
        w.WriteString("host", i.Host);
        w.WriteNumber("port", i.Port);
        w.WriteString("backend", i.Backend switch
        {
            BackendKind.Ollama => "ollama",
            BackendKind.LmStudio => "lmstudio",
            BackendKind.Vllm => "vllm",
            _ => "llama.cpp",
        });
        Str(w, "version", s.BackendVersion);
        w.WriteString("state", ServerStateNames.State(s));
        w.WriteBoolean("online", s.Online);
        w.WriteBoolean("loading", s.Loading);
        w.WriteBoolean("sleeping", s.Sleeping);
        w.WriteBoolean("manual", i.Manual);
        if (i.Pid is int pid) w.WriteNumber("pid", pid); else w.WriteNull("pid");
        w.WriteNumber("startTicks", i.StartTicks);
        Str(w, "program", i.Program);
        Str(w, "workingDir", i.WorkingDir);
        w.WriteStartArray("args");
        foreach (var a in i.Args) w.WriteStringValue(a);
        w.WriteEndArray();
        Str(w, "commandLine", i.CommandLine);
        w.WriteBoolean("commandLineReadable", i.CommandLineReadable);
        w.WriteBoolean("hasSecrets", i.HasSecrets);
        w.WriteStartObject("env");
        foreach (var (k, v) in i.Env) w.WriteString(k, v);
        w.WriteEndObject();
        Str(w, "logFile", i.LogFile);
        Str(w, "modelPath", i.ModelPath);
        w.WriteBoolean("nameIsGeneric", i.NameIsGeneric);
        w.WriteBoolean("canSave", i.CanSave);
        w.WriteString("saveBlock", i.SaveBlock.ToString());
        w.WriteString("mode", i.Mode.ToString().ToLowerInvariant());
        w.WriteBoolean("strata", s.IsStrata == true);
        Num(w, "tps", R2(s.Current));
        Num(w, "peakTps", R2(s.Peak));
        Num(w, "avgActiveTps", R2(s.AverageActive()));
        w.WriteNumber("generated", s.GeneratedTotal);
        NumN(w, "vramGb", s.VramGb);
        NumN(w, "sharedGb", s.SharedGb);
        NumN(w, "ramGb", s.RamGb);
        NumN(w, "commitGb", s.CommitGb);
        NumN(w, "cpu", s.CpuPercent);
        NumN(w, "modelFileGb", s.ModelFileGb);
        NumN(w, "spillGb", ServerWatcher.SpillGb(s.Models, s.SharedGb));
        w.WriteNumber("queue", s.QueueCount ?? 0);
        // Spekulatives Decoding: nur wenn der Server überhaupt Entwürfe zählt (sonst 0/0, und die Oberflächen zeigen nichts)
        NumN(w, "specDrafted", s.SpecActive ? s.Spec.Drafted : null);
        NumN(w, "specAccepted", s.SpecActive ? s.Spec.Accepted : null);
        w.WriteBoolean("unified", s.Unified);
        w.WriteString("external", s.External.ToString().ToLowerInvariant());
        w.WriteNumber("children", s.Children.Count);
        w.WriteBoolean("recording", e.Recorder.IsRecording(s.Key));
        w.WriteStartArray("clients");
        foreach (var c in s.Clients) w.WriteStringValue(c);
        w.WriteEndArray();
        w.WriteStartArray("slots");
        foreach (var sl in s.Slots)
        {
            w.WriteStartObject();
            w.WriteNumber("id", sl.Id);
            w.WriteBoolean("busy", sl.Busy);
            w.WriteBoolean("prompt", sl.ReadingPrompt);
            Num(w, "tps", R2(sl.Tps));
            w.WriteNumber("generated", sl.Generated);
            w.WriteNumber("ctxUsed", sl.CtxUsed);
            w.WriteNumber("ctxMax", sl.CtxMax);
            w.WriteNumber("maxTokens", sl.MaxTokens);
            Num(w, "promptProgress", R2(sl.PromptProgress));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("models");
        foreach (var m in s.Models)
        {
            w.WriteStartObject();
            w.WriteString("name", m.Name);
            Num(w, "sizeGb", R2(m.SizeBytes / 1073741824.0));
            Num(w, "vramGb", R2(m.VramBytes / 1073741824.0));
            Str(w, "expiresAt", m.ExpiresAt?.ToString("o"));
            w.WriteNumber("contextLength", m.ContextLength ?? 0);
            w.WriteString("detail", m.Detail);
            w.WriteString("state", m.State);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (s.Props is { } p)
        {
            w.WriteStartObject("props");
            Str(w, "buildInfo", p.BuildInfo);
            Str(w, "modelPath", p.ModelPath);
            Str(w, "modelAlias", p.ModelAlias);
            w.WriteNumber("totalSlots", p.TotalSlots);
            w.WriteNumber("nCtx", p.NCtx);
            w.WriteBoolean("isSleeping", p.IsSleeping);
            Str(w, "role", p.Role);
            Str(w, "modelFtype", p.ModelFtype);
            w.WriteEndObject();
        }
        else w.WriteNull("props");
        Str(w, "propsJson", s.PropsJson);
        w.WriteStartArray("recent");
        foreach (var f in s.Finished.TakeLast(20))
        {
            w.WriteStartObject();
            w.WriteNumber("seq", f.Seq);
            w.WriteString("server", f.Server);
            w.WriteString("serverKey", f.ServerKey);
            w.WriteString("model", f.Model);
            w.WriteNumber("task", f.Task);
            w.WriteNumber("slot", f.Slot);
            w.WriteNumber("promptTokens", f.PromptTokens);
            Num(w, "promptTps", R2(f.PromptTps));
            Num(w, "genTps", R2(f.GenTps));
            w.WriteNumber("genTokens", f.GenTokens);
            Num(w, "seconds", R2(f.Seconds));
            Str(w, "seen", f.Seen?.ToString("o"));
            w.WriteString("status", f.Status.ToString().ToLowerInvariant());
            w.WriteString("client", f.Client);
            w.WriteNumber("promptTotal", f.PromptTotal);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (withHistory)
        {
            w.WriteStartArray("history");
            for (int k = 0; k < s.HistoryCount; k++) w.WriteNumberValue(Math.Round(s.HistoryAt(k), 2));
            w.WriteEndArray();
            w.WriteStartArray("promptFlags");
            for (int k = 0; k < s.HistoryCount; k++) w.WriteBooleanValue(s.PromptAt(k));
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    // Ein gestarteter Server, der noch nicht lauscht oder fehlgeschlagen ist (für die Karte "starting …" der Oberflächen)
    private static void WriteLaunch(Utf8JsonWriter w, LaunchedServer l)
    {
        w.WriteStartObject();
        w.WriteString("id", l.Id.ToString("N"));
        w.WriteString("name", l.Name);
        w.WriteString("url", l.Url);
        Str(w, "program", l.Program);
        w.WriteNumber("port", l.Port);
        Str(w, "logFile", l.LogFile);
        w.WriteString("state", l.State == LaunchState.Failed ? "failed" : "starting");
        Str(w, "profileId", l.ProfileId?.ToString("N"));   // damit die gemerkte Karte "starting …" weiß, welches Profil läuft
        w.WriteString("started", l.Started.ToString("o"));
        w.WriteNumber("pid", l.Pid);
        Str(w, "failureReason", l.FailureReason);
        if (l.ExitCode is int code) w.WriteNumber("exitCode", code); else w.WriteNull("exitCode");
        w.WriteBoolean("stillRunning", l.StillRunning);
        w.WriteStartArray("logTail");
        foreach (var line in l.LogTail.TakeLast(5)) w.WriteStringValue(line);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteRecording(Utf8JsonWriter w, RecordingSummary r)
    {
        w.WriteStartObject();
        w.WriteString("id", r.Id);
        w.WriteString("file", r.File);
        w.WriteString("started", r.Started.ToString("o"));
        Num(w, "durationSec", R2(r.DurationSec));
        w.WriteString("target", r.Target);
        w.WriteString("model", r.Model);
        w.WriteString("gpu", r.Gpu);
        w.WriteBoolean("proxy", r.Proxy);
        w.WriteNumber("requests", r.Requests);
        Num(w, "busyPct", R2(r.BusyPct));
        w.WriteNumber("tokensIn", r.TokensIn);
        w.WriteNumber("tokensOut", r.TokensOut);
        Num(w, "cachePct", R2(r.CachePct));
        Num(w, "avgTps", R2(r.AvgTps));
        Num(w, "peakTps", R2(r.PeakTps));
        Num(w, "ttftP50", R2(r.TtftP50));
        Num(w, "ttftP95", R2(r.TtftP95));
        Num(w, "maxVramGb", R2(r.MaxVramGb));
        Num(w, "energyWh", R2(r.EnergyWh));
        w.WriteNumber("toolCalls", r.ToolCalls);
        w.WriteNumber("truncated", r.Truncated);
        w.WriteNumber("cancelled", r.Cancelled);
        w.WriteEndObject();
    }

    private static void Num(Utf8JsonWriter w, string name, double v) => w.WriteNumber(name, Math.Round(v, 2));
    private static void NumN(Utf8JsonWriter w, string name, double? v) { if (v is double d && double.IsFinite(d)) w.WriteNumber(name, Math.Round(d, 2)); else w.WriteNull(name); }
    private static void Str(Utf8JsonWriter w, string name, string? v) { if (v == null) w.WriteNull(name); else w.WriteString(name, v); }
}

// Die Zustandswörter der Serverkarte (offline, loading, sleeping, idle, busy) – auch für die Clients aus dem Server
public static class ServerStateNames
{
    public static string State(ServerWatcher s) =>
        !s.Online ? (s.Loading ? "loading" : "offline")
        : s.Sleeping ? "sleeping"
        : s.Slots.Any(x => x.Busy) || s.Current > 0.05 ? "busy" : "idle";

    public static string State(bool online, bool loading, bool sleeping, bool busy) =>
        !online ? (loading ? "loading" : "offline")
        : sleeping ? "sleeping"
        : busy ? "busy" : "idle";
}

// ── Der Zustand auf der Client-Seite ──

public sealed class StateSnapshot
{
    public int Schema { get; init; }
    public DateTime Time { get; init; }
    public int Ticks { get; init; }
    public int IntervalMs { get; init; }
    public int ServerPort { get; init; }
    public bool ReadOnly { get; init; }
    public bool Simulated { get; init; }
    public string? CsvNote { get; init; }
    public GpuSample? Gpu { get; init; }
    public SystemSample? Sys { get; init; }
    public List<(string Name, double Gb)> VramTop { get; init; } = new();
    public List<(string Name, double Gb)> RamTop { get; init; } = new();
    public List<(string Name, double Percent)> GpuUtilTop { get; init; } = new();
    public List<RemoteServer> Servers { get; init; } = new();
    public List<RemoteLaunch> Launches { get; init; } = new();
    public ProxyState Proxy { get; init; } = new();
    public RecordingState Recording { get; init; } = new();
    public List<RemoteRecording> Recordings { get; init; } = new();
    public List<RemoteProfile> Profiles { get; init; } = new();
    public List<RemoteHistory> History { get; init; } = new();
    public List<RemoteBenchmark> Benchmarks { get; init; } = new();
    public EvalState? Eval { get; set; }
    public SettingsState Settings { get; init; } = new();
    public AccessStateView Access { get; init; } = new();
    public List<string> Notices { get; init; } = new();

    // Der anhaltende Hinweis (Bereich 2): bleibt stehen, bis ihn eine Oberfläche wegklickt
    public NoticeState? Notice { get; set; }

    public static StateSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Read(doc.RootElement);
    }

    public static StateSnapshot Read(JsonElement r)
    {
        var snap = new StateSnapshot
        {
            Schema = J.Int(r, "schema"),
            Time = J.Time(r, "time"),
            Ticks = J.Int(r, "ticks"),
            IntervalMs = J.Int(r, "intervalMs", 1000),
            ServerPort = J.Int(r, "serverPort"),
            ReadOnly = J.Bool(r, "readOnly"),
            Simulated = J.Bool(r, "simulated"),
            CsvNote = J.Str(r, "csvNote"),
            Gpu = J.Obj(r, "gpu") is { } g ? new GpuSample(
                J.Str(g, "name") ?? "", J.Dbl(g, "util"), J.Dbl(g, "vramUsedGb"), J.Dbl(g, "vramTotalGb"), J.Dbl(g, "powerW"),
                J.Dbl(g, "powerLimitW"), J.Dbl(g, "tempC"), J.Dbl(g, "memUtil"), J.Dbl(g, "gfxMhz"), J.Dbl(g, "gfxMaxMhz"),
                J.Dbl(g, "memMhz"), J.Dbl(g, "memMaxMhz"), (ulong)J.Dbl(g, "throttleBits")) : null,
            Sys = J.Obj(r, "system") is { } s ? new SystemSample(
                J.Dbl(s, "cpu"), J.Int(s, "cores"), J.Dbl(s, "ramUsedGb"), J.Dbl(s, "ramTotalGb"),
                J.Dbl(s, "commitUsedGb"), J.Dbl(s, "commitLimitGb")) : null,
            VramTop = Top(r, "vramTop"),
            RamTop = Top(r, "ramTop"),
            GpuUtilTop = UtilTop(r, "gpuUtilTop"),
            Proxy = ReadProxy(J.Obj(r, "proxy")),
            Recording = ReadRecording(J.Obj(r, "recording")),
            Access = ReadAccess(J.Obj(r, "access")),
            Settings = ReadSettings(J.Obj(r, "settings")),
        };
        var servers = new List<RemoteServer>();
        if (r.TryGetProperty("servers", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray()) servers.Add(ReadServer(e));
        snap.Servers.AddRange(servers);
        if (r.TryGetProperty("launches", out var starts) && starts.ValueKind == JsonValueKind.Array)
            foreach (var e in starts.EnumerateArray()) snap.Launches.Add(ReadLaunch(e));
        if (r.TryGetProperty("recordings", out var recs) && recs.ValueKind == JsonValueKind.Array)
            foreach (var e in recs.EnumerateArray()) snap.Recordings.Add(ReadRecording2(e));
        if (r.TryGetProperty("profiles", out var profs) && profs.ValueKind == JsonValueKind.Array)
            foreach (var e in profs.EnumerateArray()) snap.Profiles.Add(ReadProfile(e));
        if (r.TryGetProperty("history", out var hist) && hist.ValueKind == JsonValueKind.Array)
            foreach (var e in hist.EnumerateArray()) snap.History.Add(ReadHistory(e));
        if (r.TryGetProperty("benchmarks", out var bench) && bench.ValueKind == JsonValueKind.Array)
            foreach (var e in bench.EnumerateArray()) snap.Benchmarks.Add(ReadBenchmark(e));
        if (J.Obj(r, "eval") is { } ev) snap.Eval = ReadEval(ev);
        if (r.TryGetProperty("notices", out var notes) && notes.ValueKind == JsonValueKind.Array)
            foreach (var e in notes.EnumerateArray()) if (e.ValueKind == JsonValueKind.String) snap.Notices.Add(e.GetString()!);
        if (J.Obj(r, "notice") is { } note) snap.Notice = ReadNotice(note);
        return snap;
    }

    private static NoticeState? ReadNotice(JsonElement e)
    {
        var text = J.Str(e, "text") ?? "";
        return text.Length == 0 ? null : new NoticeState(text, J.Bool(e, "alarm"), J.Str(e, "logFile") ?? "");
    }

    private static IEnumerable<JsonElement> arr(JsonElement e, string name) => J.Arr(e, name);

    private static List<(string, double)> Top(JsonElement r, string name)
    {
        var list = new List<(string, double)>();
        if (r.TryGetProperty(name, out var arr0) && arr0.ValueKind == JsonValueKind.Array)
            foreach (var e in arr0.EnumerateArray()) list.Add((J.Str(e, "name") ?? "", J.Dbl(e, "gb")));
        return list;
    }

    private static List<(string, double)> UtilTop(JsonElement r, string name)
    {
        var list = new List<(string, double)>();
        if (r.TryGetProperty(name, out var arr0) && arr0.ValueKind == JsonValueKind.Array)
            foreach (var e in arr0.EnumerateArray()) list.Add((J.Str(e, "name") ?? "", J.Dbl(e, "pct")));
        return list;
    }

    private static ProxyState ReadProxy(JsonElement? e) => e is { } p ? new ProxyState
    {
        Enabled = J.Bool(p, "enabled"),
        Running = J.Bool(p, "running"),
        BindLan = J.Bool(p, "bindLan"),
        Port = J.Int(p, "port", 8079),
        TargetKey = J.Str(p, "targetKey") ?? "",
        TargetValue = J.Str(p, "targetValue") ?? "",
        TargetName = J.Str(p, "targetName") ?? "",
        ServedKey = J.Str(p, "servedKey") ?? "",
        SecondsToRetry = J.Int(p, "secondsToRetry"),
        Choices = ReadChoices(p),
        Providers = ReadProviders(p),
        Remotes = ReadRemotes(p),
        Claude = ReadClaude(p),
    } : new();

    // Zustand des Schalters „Claude Desktop auf den Stykker-Proxy"
    private static ClaudeState ReadClaude(JsonElement p) => J.Obj(p, "claude") is { } c ? new ClaudeState
    {
        Supported = J.Bool(c, "supported"),
        Enabled = J.Bool(c, "enabled"),
        Running = J.Bool(c, "running"),
        RunningCount = J.Int(c, "runningCount"),
        Url = J.Str(c, "url") ?? "",
    } : new();

    // Die angehängten Stykker-Rechner
    private static List<ProxyRemoteState> ReadRemotes(JsonElement p)
    {
        var list = new List<ProxyRemoteState>();
        if (p.TryGetProperty("remotes", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var c in arr.EnumerateArray())
                list.Add(new ProxyRemoteState { Name = J.Str(c, "name") ?? "", Url = J.Str(c, "url") ?? "" });
        return list;
    }

    // Die Auswahl „serviertes Modell" (lokal und Cloud) – der Wert steht vollständig in der Auswahl, die Oberfläche
    // muss die Kennung nicht selbst zusammenbauen.
    private static List<ProxyChoiceState> ReadChoices(JsonElement p)
    {
        var list = new List<ProxyChoiceState>();
        if (p.TryGetProperty("choices", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var c in arr.EnumerateArray())
                list.Add(new ProxyChoiceState
                {
                    Value = J.Str(c, "value") ?? "",
                    Display = J.Str(c, "display") ?? "",
                    Cloud = J.Bool(c, "cloud"),
                    PublicModel = J.Str(c, "publicModel") ?? "",
                });
        return list;
    }

    // Die Cloud-Anbieter. Der Schlüssel wird nie geschickt, nur hasKey.
    private static List<ProxyProviderState> ReadProviders(JsonElement p)
    {
        var list = new List<ProxyProviderState>();
        if (p.TryGetProperty("providers", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var c in arr.EnumerateArray())
                list.Add(new ProxyProviderState
                {
                    Name = J.Str(c, "name") ?? "",
                    Url = J.Str(c, "url") ?? "",
                    HasKey = J.Bool(c, "hasKey"),
                    Ready = J.Bool(c, "ready"),
                    Models = J.StrList(c, "models"),
                    Error = J.Str(c, "error") ?? "",
                });
        return list;
    }

    private static RecordingState ReadRecording(JsonElement? e)
    {
        var st = new RecordingState();
        if (e is not { } r) return st;
        st.Global = J.Bool(r, "global");
        foreach (var s in arr(r, "sessions"))
            st.Sessions.Add(new RemoteRecordingSession
            {
                Key = J.Str(s, "key") ?? "",
                Id = J.Str(s, "id") ?? "",
                Started = J.Time(s, "started"),
                ElapsedSec = J.Dbl(s, "elapsedSec"),
                LastTps = J.Dbl(s, "lastTps"),
                Bytes = J.Int(s, "bytes"),
                Global = J.Bool(s, "global"),
            });
        return st;
    }

    private static RemoteLaunch ReadLaunch(JsonElement e) => new()
    {
        Id = J.Str(e, "id") ?? "",
        Name = J.Str(e, "name") ?? "",
        Url = J.Str(e, "url") ?? "",
        Program = J.Str(e, "program") ?? "",
        Port = J.Int(e, "port"),
        LogFile = J.Str(e, "logFile") ?? "",
        State = J.Str(e, "state") ?? "starting",
        ProfileId = J.Str(e, "profileId") ?? "",
        Started = J.Time(e, "started"),
        Pid = J.Int(e, "pid"),
        FailureReason = J.Str(e, "failureReason") ?? "",
        ExitCode = J.IntOrNull(e, "exitCode"),
        StillRunning = J.Bool(e, "stillRunning"),
        LogTail = J.StrList(e, "logTail"),
    };

    private static RemoteRecording ReadRecording2(JsonElement e) => new()
    {
        Id = J.Str(e, "id") ?? "",
        File = J.Str(e, "file") ?? "",
        Started = J.Time(e, "started"),
        DurationSec = J.Dbl(e, "durationSec"),
        Target = J.Str(e, "target") ?? "",
        Model = J.Str(e, "model") ?? "",
        Gpu = J.Str(e, "gpu") ?? "",
        Proxy = J.Bool(e, "proxy"),
        Requests = J.Int(e, "requests"),
        BusyPct = J.Dbl(e, "busyPct"),
        TokensIn = J.Int(e, "tokensIn"),
        TokensOut = J.Int(e, "tokensOut"),
        CachePct = J.Dbl(e, "cachePct"),
        AvgTps = J.Dbl(e, "avgTps"),
        PeakTps = J.Dbl(e, "peakTps"),
        TtftP50 = J.Dbl(e, "ttftP50"),
        TtftP95 = J.Dbl(e, "ttftP95"),
        MaxVramGb = J.Dbl(e, "maxVramGb"),
        EnergyWh = J.Dbl(e, "energyWh"),
        ToolCalls = J.Int(e, "toolCalls"),
        Truncated = J.Int(e, "truncated"),
        Cancelled = J.Int(e, "cancelled"),
    };

    private static RemoteProfile ReadProfile(JsonElement e) => new()
    {
        Id = J.Str(e, "id") ?? "",
        Key = J.Str(e, "key") ?? "",
        Name = J.Str(e, "name") ?? "",
        Program = J.Str(e, "program") ?? "",
        Args = J.StrList(e, "args"),
        Env = J.Map(e, "env"),
        WorkingDir = J.Str(e, "workingDir") ?? "",
        Port = J.Int(e, "port"),
        ModelPath = J.Str(e, "modelPath") ?? "",
        Note = J.Str(e, "note") ?? "",
        HasSecrets = J.Bool(e, "hasSecrets"),
        Created = J.Time(e, "created"),
        LastStarted = J.TimeOrNull(e, "lastStarted"),
        RestartOnCrash = J.Bool(e, "restartOnCrash"),
        MaxRestarts = J.Int(e, "maxRestarts"),
        UnloadAfterIdleMin = J.Int(e, "unloadAfterIdleMin"),
    };

    private static RemoteHistory ReadHistory(JsonElement e) => new()
    {
        Key = J.Str(e, "key") ?? "",
        Name = J.Str(e, "name") ?? "",
        Program = J.Str(e, "program") ?? "",
        Args = J.StrList(e, "args"),
        Env = J.Map(e, "env"),
        WorkingDir = J.Str(e, "workingDir") ?? "",
        ModelPath = J.Str(e, "modelPath") ?? "",
        Port = J.Int(e, "port"),
        Ctx = J.Int(e, "ctx"),
        HasSecrets = J.Bool(e, "hasSecrets"),
        FirstSeen = J.Time(e, "firstSeen"),
        LastSeen = J.Time(e, "lastSeen"),
        Runs = J.Int(e, "runs"),
        TotalSeconds = J.Dbl(e, "totalSeconds"),
        BestTps = J.Dbl(e, "bestTps"),
        MeanTps = J.Dbl(e, "meanTps"),
        MaxVramGb = J.Dbl(e, "maxVramGb"),
        ModelSizeGb = J.Dbl(e, "modelSizeGb"),
    };

    private static RemoteBenchmark ReadBenchmark(JsonElement e)
    {
        var b = new RemoteBenchmark
        {
            Id = J.Str(e, "id") ?? "",
            Started = J.Time(e, "started"),
            DurationSec = J.Dbl(e, "durationSec"),
            Title = J.Str(e, "title") ?? "",
            Server = J.Str(e, "server") ?? "",
            Model = J.Str(e, "model") ?? "",
            Quant = J.Str(e, "quant") ?? "",
            Gpu = J.Str(e, "gpu") ?? "",
            Build = J.Str(e, "build") ?? "",
            ContextPerSlot = J.Int(e, "contextPerSlot"),
            Slots = J.Int(e, "slots"),
            Cancelled = J.Bool(e, "cancelled"),
            RecordingFile = J.Str(e, "recordingFile") ?? "",
            Regression = J.Obj(e, "regression") is { } rg ? new RemoteBenchRegression
            {
                Title = J.Str(rg, "title") ?? "",
                DropPct = J.Dbl(rg, "dropPct"),
                Previous = J.Dbl(rg, "previous"),
                Current = J.Dbl(rg, "current"),
            } : null,
        };
        foreach (var s in arr(e, "steps"))
            b.Steps.Add(new RemoteBenchStep
            {
                Kind = J.Str(s, "kind") ?? "",
                Name = J.Str(s, "name") ?? "",
                TargetPromptTokens = J.Int(s, "targetPromptTokens"),
                PromptTps = J.Dbl(s, "promptTps"),
                GenTps = J.Dbl(s, "genTps"),
                TotalSec = J.Dbl(s, "totalSec"),
                Ok = J.Bool(s, "ok"),
                Note = J.Str(s, "note") ?? "",
                AggregateTps = J.Dbl(s, "aggregateTps"),
            });
        return b;
    }

    private static EvalState ReadEval(JsonElement e)
    {
        var st = new EvalState
        {
            Running = J.Bool(e, "running"),
            AllowCode = J.Bool(e, "allowCode"),
            Python = J.Str(e, "python") ?? "",
            LlamaServer = J.Str(e, "llamaServer") ?? "",
            ModelRoots = J.StrList(e, "modelRoots"),
        };
        foreach (var m in arr(e, "models"))
            st.Models.Add(new RemoteEvalModel
            {
                Id = J.Str(m, "id") ?? "",
                Name = J.Str(m, "name") ?? "",
                Exe = J.Str(m, "exe") ?? "",
                Args = J.Str(m, "args") ?? "",
                Port = J.Int(m, "port"),
                Enabled = J.Bool(m, "enabled"),
                Source = J.Str(m, "source") ?? "",
                ModelFile = J.Str(m, "modelFile") ?? "",
                SizeGb = J.Dbl(m, "sizeGb"),
            });
        foreach (var j in arr(e, "jobs"))
        {
            var job = new RemoteEvalJob
            {
                Id = J.Str(j, "id") ?? "",
                ModelId = J.Str(j, "modelId") ?? "",
                Model = J.Str(j, "model") ?? "",
                Suite = J.Str(j, "suite") ?? "",
                Repeat = J.Int(j, "repeat", 1),
                State = J.Str(j, "state") ?? "queued",
                Note = J.Str(j, "note") ?? "",
                Done = J.Int(j, "done"),
                Total = J.Int(j, "total"),
                Current = J.Str(j, "current") ?? "",
                Score = J.Dbl(j, "score"),
                Started = J.TimeOrNull(j, "started"),
                Finished = J.TimeOrNull(j, "finished"),
            };
            foreach (var t in arr(j, "live"))
                job.Live.Add(new RemoteEvalTask
                {
                    Id = J.Str(t, "id") ?? "",
                    Title = J.Str(t, "title") ?? "",
                    Category = J.Str(t, "category") ?? "",
                    Score = J.Dbl(t, "score"),
                    Passed = J.Bool(t, "passed"),
                    Skipped = J.Bool(t, "skipped"),
                    Error = J.Str(t, "error") ?? "",
                    Note = J.Str(t, "note") ?? "",
                });
            foreach (var run in arr(j, "runs"))
                job.Runs.Add(new RemoteEvalRun
                {
                    Id = J.Str(run, "id") ?? "",
                    Started = J.Time(run, "started"),
                    DurationSec = J.Dbl(run, "durationSec"),
                    Score = J.Dbl(run, "score"),
                    Repeat = J.Int(run, "repeat", 1),
                    Cancelled = J.Bool(run, "cancelled"),
                    GenTps = J.Dbl(run, "genTps"),
                });
            st.Jobs.Add(job);
        }
        return st;
    }

    private static SettingsState ReadSettings(JsonElement? e)
    {
        if (e is not { } s) return new();
        var st = new SettingsState
        {
            Theme = J.Str(s, "theme") ?? "",
            IntervalMs = J.Int(s, "intervalMs", 1000),
            DataDir = J.Str(s, "dataDir") ?? "",
            MaxRecordings = J.Int(s, "maxRecordings"),
            MaxRecordingsMb = J.Int(s, "maxRecordingsMb"),
            MaxRecordingMb = J.Int(s, "maxRecordingMb"),
            MaxLogFiles = J.Int(s, "maxLogFiles"),
            MaxLogsMb = J.Int(s, "maxLogsMb"),
            MaxCsvMb = J.Int(s, "maxCsvMb"),
            StartTimeoutMin = J.Int(s, "startTimeoutMin"),
            GpuTopCount = J.Int(s, "gpuTopCount"),
            BenchRegressionPct = J.Int(s, "benchRegressionPct", 10),
            ServerPort = J.Int(s, "serverPort"),
            CsvLog = J.Str(s, "csvLog") ?? "",
        };
        foreach (var m in arr(s, "manualServers"))
            st.ManualServers.Add(new RemoteManualServer
            {
                Name = J.Str(m, "name") ?? "",
                Url = J.Str(m, "url") ?? "",
                Log = J.Str(m, "log") ?? "",
                Kind = J.Str(m, "kind") ?? "",
            });
        return st;
    }

    private static AccessStateView ReadAccess(JsonElement? e)
    {
        if (e is not { } a) return new();
        var v = new AccessStateView
        {
            Remote = J.Bool(a, "remote"),
            Code = J.Str(a, "code") ?? "",
            Port = J.Int(a, "port"),
            LanAddress = J.Str(a, "lanAddress") ?? "",
            AllAddresses = J.Str(a, "lanAddressAll") ?? "",
            PairUrl = J.Str(a, "pairUrl") ?? "",
            Url = J.Str(a, "url") ?? "",
            CodeExpires = J.TimeOrNull(a, "codeExpires"),
        };
        foreach (var r in arr(a, "requests"))
            v.Requests.Add(new RemotePairRequest
            {
                Id = J.Str(r, "id") ?? "",
                Name = J.Str(r, "name") ?? "",
                Kind = PairKinds.Normalize(J.Str(r, "kind")),
                Address = J.Str(r, "address") ?? "",
                Expires = J.Time(r, "expires"),
            });
        foreach (var d in arr(a, "devices"))
            v.Devices.Add(new RemoteDevice
            {
                Id = J.Str(d, "id") ?? "",
                Name = J.Str(d, "name") ?? "",
                Role = AccessRole.Normalize(J.Str(d, "role")),
                Created = J.Time(d, "created"),
                LastSeen = J.TimeOrNull(d, "lastSeen"),
                Address = J.Str(d, "address") ?? "",
            });
        return v;
    }

    private static RemoteServer ReadServer(JsonElement e)
    {
        var s = new RemoteServer
        {
            Key = J.Str(e, "key") ?? "",
            Name = J.Str(e, "name") ?? "",
            Model = J.Str(e, "model") ?? "–",
            Url = J.Str(e, "url") ?? "",
            Host = J.Str(e, "host") ?? "127.0.0.1",
            Port = J.Int(e, "port"),
            Backend = J.Str(e, "backend") ?? "llama.cpp",
            Version = J.Str(e, "version") ?? "",
            State = J.Str(e, "state") ?? "offline",
            Online = J.Bool(e, "online"),
            Loading = J.Bool(e, "loading"),
            Sleeping = J.Bool(e, "sleeping"),
            Manual = J.Bool(e, "manual"),
            Pid = J.IntOrNull(e, "pid"),
            StartTicks = J.Int(e, "startTicks"),
            Program = J.Str(e, "program") ?? "",
            WorkingDir = J.Str(e, "workingDir") ?? "",
            Args = J.StrList(e, "args"),
            CommandLine = J.Str(e, "commandLine") ?? "",
            CommandLineReadable = J.Bool(e, "commandLineReadable"),
            HasSecrets = J.Bool(e, "hasSecrets"),
            Env = J.Map(e, "env"),
            LogFile = J.Str(e, "logFile") ?? "",
            ModelPath = J.Str(e, "modelPath") ?? "",
            NameIsGeneric = J.Bool(e, "nameIsGeneric"),
            CanSave = J.Bool(e, "canSave"),
            SaveBlock = J.Str(e, "saveBlock") ?? "None",
            Mode = J.Str(e, "mode") ?? "normal",
            Strata = J.Bool(e, "strata"),
            Current = J.Dbl(e, "tps"),
            Peak = J.Dbl(e, "peakTps"),
            AverageActive = J.Dbl(e, "avgActiveTps"),
            GeneratedTotal = J.Int(e, "generated"),
            VramGb = J.DblOrNull(e, "vramGb"),
            SharedGb = J.DblOrNull(e, "sharedGb"),
            RamGb = J.DblOrNull(e, "ramGb"),
            CommitGb = J.DblOrNull(e, "commitGb"),
            CpuPercent = J.DblOrNull(e, "cpu"),
            ModelFileGb = J.DblOrNull(e, "modelFileGb"),
            SpillGb = J.DblOrNull(e, "spillGb"),
            QueueCount = J.Int(e, "queue"),
            SpecDrafted = J.IntOrNull(e, "specDrafted"),
            SpecAccepted = J.IntOrNull(e, "specAccepted"),
            Unified = J.Bool(e, "unified"),
            External = J.Str(e, "external") ?? "notrunning",
            ChildCount = J.Int(e, "children"),
            Recording = J.Bool(e, "recording"),
            Clients = J.StrList(e, "clients").ToArray(),
        };
        foreach (var sl in arr(e, "slots"))
            s.Slots.Add(new SlotView(J.Int(sl, "id"), J.Bool(sl, "busy"), J.Bool(sl, "prompt"), J.Dbl(sl, "tps"),
                J.Int(sl, "generated"), J.Int(sl, "ctxUsed"), J.Int(sl, "ctxMax"), J.Int(sl, "maxTokens"),
                0, J.Dbl(sl, "promptProgress"), 0));
        foreach (var m in arr(e, "models"))
            s.Models.Add(new LoadedModel(J.Str(m, "name") ?? "", (long)(J.Dbl(m, "sizeGb") * 1073741824.0),
                (long)(J.Dbl(m, "vramGb") * 1073741824.0), J.TimeOrNull(m, "expiresAt"),
                J.Int(m, "contextLength") > 0 ? J.Int(m, "contextLength") : null, J.Str(m, "detail") ?? "", J.Str(m, "state") ?? ""));

        if (J.Obj(e, "props") is { } p)
            s.Props = new LlamaProps(J.Str(p, "buildInfo"), J.Str(p, "modelPath"), J.Str(p, "modelAlias"),
                J.Int(p, "totalSlots"), J.Int(p, "nCtx"), J.Bool(p, "isSleeping"), J.Str(p, "role"), J.Str(p, "modelFtype"));
        s.PropsJson = J.Str(e, "propsJson");
        foreach (var f in arr(e, "recent"))
            s.Finished.Add(new FinishedRequest(J.Int(f, "seq"), J.Str(f, "server") ?? "", J.Str(f, "model") ?? "",
                J.Int(f, "task"), J.Int(f, "slot"), J.Int(f, "promptTokens"), J.Dbl(f, "promptTps"), J.Dbl(f, "genTps"),
                J.Int(f, "genTokens"), J.Dbl(f, "seconds"), J.TimeOrNull(f, "seen"),
                Enum.TryParse<ReqStatus>(J.Str(f, "status"), true, out var st) ? st : ReqStatus.Done,
                J.Str(f, "client") ?? "", J.Int(f, "promptTotal"), J.Str(f, "serverKey") ?? ""));
        var history = new double[ServerWatcher.HistoryLength];
        int n = 0;
        foreach (var t in arr(e, "history")) if (n < history.Length) history[n++] = t.GetDouble();
        s.History = history;
        s.HistoryCount = n;
        var prompts = new bool[ServerWatcher.HistoryLength];
        n = 0;
        foreach (var t in arr(e, "promptFlags")) if (n < prompts.Length) prompts[n++] = t.ValueKind == JsonValueKind.True;
        s.PromptFlags = prompts;
        return s;
    }
}

// Lesen von JSON ohne Ausnahmen: fehlende oder falsch getypte Felder ergeben den Vorgabewert
internal static class J
{
    public static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static double Dbl(JsonElement e, string name, double def = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : def;

    public static double? DblOrNull(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    public static int Int(JsonElement e, string name, int def = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : def;

    public static int? IntOrNull(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    public static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static DateTime Time(JsonElement e, string name)
    {
        var t = TimeOrNull(e, name);
        return t ?? default;
    }

    public static DateTime? TimeOrNull(JsonElement e, string name)
    {
        var s = Str(e, name);
        return string.IsNullOrEmpty(s) ? null : DateTime.TryParse(s, Strings.Inv, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null;
    }

    public static List<string> StrList(JsonElement e, string name)
    {
        var list = new List<string>();
        foreach (var v in Arr(e, name)) if (v.ValueKind == JsonValueKind.String) list.Add(v.GetString()!);
        return list;
    }

    public static Dictionary<string, string> Map(JsonElement e, string name)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
            foreach (var p in v.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) map[p.Name] = p.Value.GetString()!;
        return map;
    }
}

// ── Die gelesenen Modelle ──

// Anhaltender Hinweis aus dem Zustand: Text, ob es eine Warnung ist, und die Logdatei zum Nachschlagen
public sealed record NoticeState(string Text, bool Alarm, string LogFile);

public sealed class ProxyState
{
    public bool Enabled { get; init; }
    public bool Running { get; init; }
    public bool BindLan { get; init; }
    public int Port { get; init; }
    public string TargetKey { get; init; } = "";
    public string TargetValue { get; init; } = "";
    public string TargetName { get; init; } = "";
    public string ServedKey { get; init; } = "";
    public int SecondsToRetry { get; init; }
    // Auswahl „serviertes Modell": lokale Modelle und Cloud-Modelle (Value = der Wert der Auswahl)
    public List<ProxyChoiceState> Choices { get; init; } = new();
    public List<ProxyProviderState> Providers { get; init; } = new();
    public List<ProxyRemoteState> Remotes { get; init; } = new();
    // Claude Desktop auf den Stykker-Proxy umgeschaltet (3P-Konfiguration der App)
    public ClaudeState Claude { get; init; } = new();
}

// Zustand des Schalters „Claude Desktop auf den Stykker-Proxy"
public sealed class ClaudeState
{
    public bool Supported { get; init; }
    public bool Enabled { get; init; }
    public bool Running { get; init; }
    public int RunningCount { get; init; }
    public string Url { get; init; } = "";
}

// Ein angehängter Stykker-Rechner (Name und Basis-URL seines Proxys)
public sealed class ProxyRemoteState
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
}

// Ein Eintrag der Auswahl „serviertes Modell" (Value, Anzeigename, ob es ein Cloud-Modell ist)
public sealed class ProxyChoiceState
{
    public string Value { get; init; } = "";
    public string Display { get; init; } = "";
    public bool Cloud { get; init; }
    // Bei einem Cloud-Modell „<Anbieter>/<Modell>" – damit die TUI das Ziel ohne den Schlüssel nennen kann
    public string PublicModel { get; init; } = "";
}

// Ein eingetragener Cloud-Anbieter. Der Schlüssel wird nie übertragen, nur „hasKey".
public sealed class ProxyProviderState
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public bool HasKey { get; init; }
    public bool Ready { get; init; }
    public List<string> Models { get; init; } = new();
    public string Error { get; init; } = "";
}

public sealed class RecordingState
{
    public bool Global { get; set; }
    public List<RemoteRecordingSession> Sessions { get; } = new();
    public bool Any => Sessions.Count > 0;
}

public sealed class RemoteRecordingSession
{
    public string Key { get; init; } = "";
    public string Id { get; init; } = "";
    public DateTime Started { get; init; }
    public double ElapsedSec { get; init; }
    public double LastTps { get; init; }
    public long Bytes { get; init; }
    public bool Global { get; init; }
}

public sealed class RemoteRecording
{
    public string Id { get; init; } = "";
    public string File { get; init; } = "";
    public DateTime Started { get; init; }
    public double DurationSec { get; init; }
    public string Target { get; init; } = "";
    public string Model { get; init; } = "";
    public string Gpu { get; init; } = "";
    public bool Proxy { get; init; }
    public int Requests { get; init; }
    public double BusyPct { get; init; }
    public int TokensIn { get; init; }
    public int TokensOut { get; init; }
    public double CachePct { get; init; }
    public double AvgTps { get; init; }
    public double PeakTps { get; init; }
    public double TtftP50 { get; init; }
    public double TtftP95 { get; init; }
    public double MaxVramGb { get; init; }
    public double EnergyWh { get; init; }
    public int ToolCalls { get; init; }
    public int Truncated { get; init; }
    public int Cancelled { get; init; }
}

// Ein Server, den der Monitor gestartet hat und der noch nicht lauscht ("starting …") oder fehlgeschlagen ist.
// Im Fenster die Karte über dem Server, in der TUI und im Web ein Hinweis auf denselben Startvorgang.
public sealed class RemoteLaunch
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string Program { get; init; } = "";
    public int Port { get; init; }
    public string LogFile { get; init; } = "";
    public string State { get; init; } = "starting";   // "starting" (hört noch nicht) oder "failed"
    public string ProfileId { get; init; } = "";        // Kennung des gemerkten Profils, wenn der Start von dort kam
    public DateTime Started { get; init; }
    public int Pid { get; init; }
    public string FailureReason { get; init; } = "";
    public int? ExitCode { get; init; }
    public bool StillRunning { get; init; }
    public List<string> LogTail { get; init; } = new();
    public bool Failed => State == "failed";
}

public sealed class RemoteProfile
{
    public string Id { get; init; } = "";
    public string Key { get; init; } = "";        // stabil aus Programm und Argumenten, wie bei Profil und Verlauf
    public string Name { get; init; } = "";
    public string Program { get; init; } = "";
    public List<string> Args { get; init; } = new();
    public Dictionary<string, string> Env { get; init; } = new();
    public string WorkingDir { get; init; } = "";
    public int Port { get; init; }
    public string ModelPath { get; init; } = "";
    public string Note { get; init; } = "";
    public bool HasSecrets { get; init; }
    public DateTime Created { get; init; }
    public DateTime? LastStarted { get; init; }
    public bool RestartOnCrash { get; init; }   // see docs/ui.md: opt-in je Profil
    public int MaxRestarts { get; init; }
    public int UnloadAfterIdleMin { get; init; }   // see docs/ui.md: 0 = aus
    public bool CanStart => !string.IsNullOrWhiteSpace(Program);

    // Für die Vorprüfung des Startknopfes (Port, Modelldatei, VRAM, RAM): derselbe Startplan wie im Core
    public LaunchSpec Spec(double? measuredVramGb = null) =>
        new(Name, Program, Args, WorkingDir.Length == 0 ? null : WorkingDir,
            Guid.TryParse(Id, out var id) ? id : null, measuredVramGb, Env);
}

public sealed class RemoteHistory
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Program { get; init; } = "";
    public List<string> Args { get; init; } = new();
    public Dictionary<string, string> Env { get; init; } = new();
    public string WorkingDir { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public int Port { get; init; }
    public int Ctx { get; init; }
    public bool HasSecrets { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }
    public int Runs { get; init; }
    public double TotalSeconds { get; init; }
    public double BestTps { get; init; }
    public double MeanTps { get; init; }
    public double MaxVramGb { get; init; }
    public double? ModelSizeGb { get; init; }
}

public sealed class RemoteBenchmark
{
    public string Id { get; init; } = "";
    public DateTime Started { get; init; }
    public double DurationSec { get; init; }
    public string Title { get; init; } = "";
    public string Server { get; init; } = "";
    public string Model { get; init; } = "";
    public string Quant { get; init; } = "";
    public string Gpu { get; init; } = "";
    public string Build { get; init; } = "";
    public int ContextPerSlot { get; init; }
    public int Slots { get; init; }
    public bool Cancelled { get; init; }
    public string RecordingFile { get; init; } = "";
    public RemoteBenchRegression? Regression { get; init; }
    public List<RemoteBenchStep> Steps { get; } = new();
}

// Rückgang der Erzeugungsgeschwindigkeit gegenüber dem früheren Lauf derselben Konfiguration
public sealed class RemoteBenchRegression
{
    public string Title { get; init; } = "";
    public double DropPct { get; init; }
    public double Previous { get; init; }
    public double Current { get; init; }

    public string Text => Strings.BenchRegressionAlert(DropPct, Previous, Current);
}

public sealed class RemoteBenchStep
{
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public int TargetPromptTokens { get; init; }
    public double PromptTps { get; init; }
    public double GenTps { get; init; }
    public double TotalSec { get; init; }
    public bool Ok { get; init; }
    public string Note { get; init; } = "";
    public double AggregateTps { get; init; }
}

public sealed class EvalState
{
    public bool Running { get; init; }
    public bool AllowCode { get; init; }
    public string Python { get; init; } = "";
    public string LlamaServer { get; init; } = "";
    public List<string> ModelRoots { get; init; } = new();
    public List<RemoteEvalModel> Models { get; } = new();
    public List<RemoteEvalJob> Jobs { get; } = new();
}

public sealed class RemoteEvalModel
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Exe { get; init; } = "";
    public string Args { get; init; } = "";
    public int Port { get; init; }
    public bool Enabled { get; init; }
    public string Source { get; init; } = "";
    public string ModelFile { get; init; } = "";
    public double SizeGb { get; init; }
}

public sealed class RemoteEvalJob
{
    public string Id { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string Model { get; init; } = "";
    public string Suite { get; init; } = "";
    public int Repeat { get; init; }
    public string State { get; init; } = "queued";
    public string Note { get; init; } = "";
    public int Done { get; init; }
    public int Total { get; init; }
    public string Current { get; init; } = "";
    public double Score { get; init; }
    public DateTime? Started { get; init; }
    public DateTime? Finished { get; init; }
    public bool Finished2 => Finished.HasValue;
    public List<RemoteEvalTask> Live { get; } = new();
    public List<RemoteEvalRun> Runs { get; } = new();
}

public sealed class RemoteEvalTask
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Category { get; init; } = "";
    public double Score { get; init; }
    public bool Passed { get; init; }
    public bool Skipped { get; init; }
    public string Error { get; init; } = "";
    public string Note { get; init; } = "";
}

public sealed class RemoteEvalRun
{
    public string Id { get; init; } = "";
    public DateTime Started { get; init; }
    public double DurationSec { get; init; }
    public double Score { get; init; }
    public int Repeat { get; init; }
    public bool Cancelled { get; init; }
    public double GenTps { get; init; }
}

public sealed class SettingsState
{
    public string Theme { get; init; } = "";
    public int IntervalMs { get; init; }
    public string DataDir { get; init; } = "";
    public int MaxRecordings { get; init; }
    public int MaxRecordingsMb { get; init; }
    public int MaxRecordingMb { get; init; }
    public int MaxLogFiles { get; init; }
    public int MaxLogsMb { get; init; }
    public int MaxCsvMb { get; init; }
    public int StartTimeoutMin { get; init; }
    public int GpuTopCount { get; init; }
    public int BenchRegressionPct { get; init; }
    public int ServerPort { get; init; }
    public string CsvLog { get; init; } = "";
    public List<RemoteManualServer> ManualServers { get; } = new();
}

public sealed class RemoteManualServer
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string Log { get; init; } = "";
    public string Kind { get; init; } = "";
}

public sealed class AccessStateView
{
    public bool Remote { get; init; }
    public string Code { get; init; } = "";
    public int Port { get; init; }
    public string LanAddress { get; init; } = "";
    public string AllAddresses { get; init; } = "";
    public string PairUrl { get; init; } = "";
    public string Url { get; init; } = "";
    public DateTime? CodeExpires { get; init; }
    public List<RemotePairRequest> Requests { get; } = new();
    public List<RemoteDevice> Devices { get; } = new();

    // Dieselbe Sicht aus der Zugangsdatei, wenn kein Server läuft (Fenster, TUI). Offene Anfragen gibt es dann
    // nicht: die leben nur im Speicher des Servers.
    public static AccessStateView FromLocal(AccessControl a, int port)
    {
        var v = new AccessStateView
        {
            Remote = a.RemoteEnabled,
            Code = a.Code,
            Port = port,
            LanAddress = NetInfo.LanAddress() ?? "",
            PairUrl = NetInfo.PairUrl(port, a.Code),
            Url = NetAddr.Url("127.0.0.1", port),
            CodeExpires = a.CodeExpires,
        };
        foreach (var d in a.Devices)
            v.Devices.Add(new RemoteDevice { Id = d.Id, Name = d.Name, Role = d.Role, Created = d.Created, LastSeen = d.LastSeen, Address = d.Address ?? "" });
        return v;
    }
}

// Eine offene Kopplungsanfrage (das neue Gerät zeigt den Code, hier steht nur, wer fragt)
public sealed class RemotePairRequest
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = PairKinds.Browser;
    public string Address { get; init; } = "";
    public DateTime Expires { get; init; }
}

public sealed class RemoteDevice
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Role { get; init; } = AccessRole.Admin;
    public DateTime Created { get; init; }
    public DateTime? LastSeen { get; init; }
    public string Address { get; init; } = "";
}

// Ein Server aus der Sicht eines Clients (Fenster, TUI, Skript). Die Eigenschaften heißen wie beim ServerWatcher,
// damit die TUI und die Listen dieselben Feldnamen verwenden können.
public sealed class RemoteInstalledModel
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Quantization { get; init; } = "";
    public bool Loaded { get; init; }
    public int? MaxContext { get; init; }
}

public sealed class RemoteServer
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Model { get; init; } = "–";
    public string Url { get; init; } = "";
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; }
    public string Backend { get; init; } = "llama.cpp";
    public string Version { get; init; } = "";
    public string State { get; init; } = "offline";
    public bool Online { get; init; }
    public bool Loading { get; init; }
    public bool Sleeping { get; init; }
    public bool Manual { get; init; }
    public int? Pid { get; init; }
    public long StartTicks { get; init; }        // Startzeit des Prozesses (für die Laufzeit in den Details)
    public string Program { get; init; } = "";
    public string WorkingDir { get; init; } = "";
    public List<string> Args { get; init; } = new();
    public string CommandLine { get; init; } = "";
    public bool CommandLineReadable { get; init; }
    public bool HasSecrets { get; init; }
    public Dictionary<string, string> Env { get; init; } = new();
    public string LogFile { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public bool NameIsGeneric { get; init; }
    public bool CanSave { get; init; }
    public string SaveBlock { get; init; } = "None";
    public string Mode { get; init; } = "normal";
    public bool Strata { get; init; }
    public double Current { get; init; }
    public double Peak { get; init; }
    public double AverageActive { get; init; }
    public long GeneratedTotal { get; init; }
    public double? VramGb { get; init; }
    public double? SharedGb { get; init; }
    public double? RamGb { get; init; }
    public double? CommitGb { get; init; }
    public double? CpuPercent { get; init; }
    public double? ModelFileGb { get; init; }
    public double? SpillGb { get; init; }
    public int QueueCount { get; init; }
    public int? SpecDrafted { get; init; }      // null = der Server zählt kein spekulatives Decoding
    public int? SpecAccepted { get; init; }
    public bool Unified { get; init; }
    public string External { get; init; } = "notrunning";
    public int ChildCount { get; init; }
    public bool Recording { get; init; }
    public string[] Clients { get; init; } = Array.Empty<string>();
    public List<SlotView> Slots { get; } = new();
    public List<LoadedModel> Models { get; } = new();
    public List<RemoteInstalledModel> InstalledModels { get; } = new();
    public LlamaProps? Props { get; set; }
    public string? PropsJson { get; set; }
    public List<FinishedRequest> Finished { get; } = new();
    public double[] History { get; set; } = new double[ServerWatcher.HistoryLength];
    public int HistoryCount { get; set; }
    public bool[] PromptFlags { get; set; } = new bool[ServerWatcher.HistoryLength];

    public double HistoryAt(int i) => i >= 0 && i < HistoryCount ? History[i] : 0;
    public bool PromptAt(int i) => i >= 0 && i < HistoryCount && PromptFlags[i];
    public string DisplayName => Name;
    public string StateText => State;

    // Was die Oberflächen brauchen, ohne die Enums des Motors zu kennen (Fenster, TUI und Web lesen denselben Zustand)
    public bool IsLlama => Backend == "llama.cpp";
    public bool NoProcess => Pid == null;
    // "SaveBlock" ist hier der Text, deshalb muss der Typ ausgeschrieben werden
    public SaveBlock SaveBlockKind => Enum.TryParse<SaveBlock>(SaveBlock, true, out var b) ? b : global::StykkerLlm.Core.SaveBlock.None;
    public LlamaServerArgs Params => LlamaServerArgs.Parse(Args, Env);
    // Schlüssel wie Profil und Verlauf: damit eine Karte ohne Engine fragen kann, ob der Server schon gemerkt ist
    public string ProfileKey => Program.Length > 0 ? Library.MakeKey(Program, Args) : "";
}
