using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StykkerLlm.Core;

// CtxUsed = Token im Kontext des Slots (Prompt + bisher Erzeugtes); MaxTokens <= 0: unbegrenzt.
// PromptProgress 0..1 aus dem Log (oder -1, solange noch keine Fortschrittszeile kam).
public sealed record SlotView(int Id, bool Busy, bool ReadingPrompt, double Tps, int Generated, int CtxUsed, int CtxMax,
    int MaxTokens, int PromptDone, double PromptProgress, long Task);

public enum ReqStatus { Done, Truncated, Cancelled }

// Seen = null: Eintrag stammt aus dem beim Start eingelesenen alten Log (Uhrzeit unbekannt).
public sealed record FinishedRequest(int Seq, string Server, string Model, int Task, int Slot, int PromptTokens, double PromptTps,
    double GenTps, int GenTokens, double Seconds, DateTime? Seen, ReqStatus Status, string Client = "", int PromptTotal = 0, string ServerKey = "");

// Fragt einen llama-server ab (/slots, /v1/models, /props) und liest dessen Log inkrementell mit.
// Schlüssel im ServerRegistry ist Host:Port. Nach Dispose() wird nichts mehr abgefragt und alles freigegeben.
public sealed class ServerWatcher : IDisposable
{
    public const int HistoryLength = 300;
    private const int OfflineAfterFails = 5;
    private static int _seqCounter;

    public ServerInfo Info { get; private set; }
    public string Key => Info.Key;

    // Der Schlüssel, unter dem **gespeicherte Profile** diesen Server führen (Programm + Argumente, wie `Profile.Key`).
    // `Key` oben ist Host:Port – damit lässt sich kein Profil finden, deshalb dieser zweite Schlüssel. Leer, wenn der
    // Prozess keine lesbare Kommandozeile hat (dann kam er nicht aus einem Profil).
    public string ProfileKey => Info.Program is { Length: > 0 } program ? Library.MakeKey(program, Info.Args) : "";
    public string Url => Info.Url;
    public string? Log => Info.LogFile;
    // Anzeigename: Alias bzw. Modelldatei aus der Kommandozeile, sonst Angaben aus /props
    public string Name => !Info.NameIsGeneric ? Info.Name
        : !string.IsNullOrEmpty(Props?.ModelAlias) ? Props!.ModelAlias!
        : Props?.ModelPath is { Length: > 0 } pp ? ServerInfo.ModelName(pp)
        : Info.Name;
    public string Model { get; private set; } = "–";
    public bool Online { get; private set; }
    // Server antwortet mit HTTP 503: Modell wird noch geladen (nicht offline)
    public bool Loading { get; private set; }
    // Server schläft (--sleep-idle-seconds): Modell entladen, /slots wird dann nicht abgefragt (würde ihn aufwecken)
    public bool Sleeping { get; private set; }
    public LlamaProps? Props { get; private set; }
    // Rohtext der letzten /props-Antwort (für das Details-Fenster)
    public string? PropsJson { get; private set; }
    public IReadOnlyList<SlotView> Slots { get; private set; } = Array.Empty<SlotView>();
    public double[] History { get; } = new double[HistoryLength];   // Ringpuffer Gesamt-Token/s
    private readonly bool[] _promptFlags = new bool[HistoryLength];   // in diesem Schritt las irgendein Slot den Prompt
    public int HistoryCount { get; private set; }
    private int _historyHead;
    public double Current { get; private set; }
    // Letzte Anfragen: das Pollen ändert die Liste, die Oberflächen lesen jede Sekunde – deshalb gesperrt, gelesen als Kopie
    private readonly List<FinishedRequest> _finished = new();
    public IReadOnlyList<FinishedRequest> Finished { get { lock (_finished) return _finished.ToArray(); } }
    private void AddFinished(FinishedRequest f) { lock (_finished) { _finished.Insert(0, f); if (_finished.Count > 12) _finished.RemoveAt(_finished.Count - 1); } }
    // Nur für Anfragen, die der Monitor live mitbekommen hat (nicht für das beim Start eingelesene Log)
    public event Action<FinishedRequest>? RequestFinished;

    // erzeugt = Token abgeschlossener Anfragen (Log) + laufende n_decoded der Slots
    private long _generatedDone;
    private long _generatedRunning;
    public long GeneratedTotal => _generatedDone + _generatedRunning;

    // Speicher und Clients des Server-Prozesses. Sie werden alle paar Sekunden in einem Hintergrund-Task
    // gemessen (PDH, Prozessabfragen) und als unveränderlicher Schnappschuss übergeben.
    private sealed record MemInfo(double? Vram, double? Shared, double? Ram, double? Commit, double? Cpu, string[] Clients)
    {
        public static readonly MemInfo Empty = new(null, null, null, null, null, Array.Empty<string>());
    }
    private volatile MemInfo _mem = MemInfo.Empty;
    // Ollama meldet den belegten VRAM selbst (size_vram); sonst die Messung des Serverprozesses.
    // LM Studio: Programm plus die von ihm gestarteten Engines (llama-server-Kinder); deren VRAM kommt aus der PDH-Messung je PID.
    public double? VramGb => Kind == BackendKind.Ollama ? (Online ? Models.Sum(m => m.VramBytes) / 1073741824.0 : null) : WithChildren(_mem.Vram, c => c.VramGb);
    public double? SharedGb => WithChildren(_mem.Shared, c => c.SharedGb);
    public double? RamGb => WithChildren(_mem.Ram, c => c.RamGb);            // Working Set: tatsächlich belegter RAM
    public double? CommitGb => WithChildren(_mem.Commit, c => c.CommitGb);   // Private Bytes: zugesagter Speicher
    public double? CpuPercent => WithChildren(_mem.Cpu, c => c.CpuPercent);  // CPU-Zeit des Prozesses, auf alle Kerne bezogen

    private double? WithChildren(double? own, Func<ServerWatcher, double?> get)
    {
        var kids = Children;
        if (kids.Count == 0) return own;
        double? sum = own;
        foreach (var k in kids) if (get(k) is double v) sum = (sum ?? 0) + v;
        return sum;
    }
    public int? Pid => Info.Pid;                 // Prozess, der am Port lauscht (auch wenn /slots nicht antwortet)
    public string[] Clients => _mem.Clients;     // Programme mit Verbindung zum Server
    public bool Unified { get; private set; }    // kv_unified: alle Slots teilen einen Kontext
    public double? ModelFileGb { get; private set; }
    public int? QueueCount { get; private set; }   // Anfragen, die auf einen freien Slot warten (llama-server /metrics, requests_deferred)

    // Spekulatives Decoding: entworfene und angenommene Token seit dem Start des Servers.
    // Ohne Entwürfe bleibt Drafted = 0, und die Oberflächen zeigen nichts an.
    public LlamaSpecApi.Spec Spec { get; private set; }
    public bool SpecActive => Spec.Drafted > 0;
    public double SpecRate => LlamaSpecApi.Rate(Spec);

    // Leerlauf: seit wann dieser Server zum letzten Mal gearbeitet hat. null = noch nie; dann
    // zählt `Since` (seit der Monitor ihn sieht). Die Engine markiert das nach jedem Takt.
    public DateTime? LastBusyAt { get; private set; }
    public DateTime Since { get; } = DateTime.Now;
    internal void MarkBusy(DateTime now) => LastBusyAt = now;

    // Speicher, der im Windows-Shared-Memory liegt (also im RAM und damit langsam) statt im Speicher der Karte:
    // Ollama meldet es direkt (size_vram kleiner als size), sonst zählt die gemessene Shared-GPU-Belegung des Prozesses.
    public static double? SpillGb(IReadOnlyList<LoadedModel> models, double? sharedGb)
    {
        double byModel = 0;
        foreach (var m in models)
            if (m.SizeBytes > 0 && m.VramBytes > 0 && m.SizeBytes > m.VramBytes)
                byModel = Math.Max(byModel, (m.SizeBytes - m.VramBytes) / 1073741824.0);
        if (byModel > 0) return byModel;
        return sharedGb is double g && g >= 0.25 ? g : null;
    }

    // nur vom Hintergrund-Task benutzt (unter _memLock)
    private readonly object _memLock = new();
    private bool _disposed;
    private TimeSpan _cpuPrev; private DateTime _cpuPrevAt; private int _cpuPrevPid;
    private IGpuMemoryQuery? _gpuQuery;
    private volatile bool _memBusy, _memReset;
    private DateTime _lastMemQuery = DateTime.MinValue;
    private DateTime _lastMetricsQuery = DateTime.MinValue;
    internal double? MetricsEverySeconds;   // Tests (wie ExternalEverySeconds)

    private readonly HttpClient _http;
    private readonly IPlatform _platform;
    private readonly Func<NetSnapshot> _net;
    private readonly ClientNamer _namer;
    private readonly Dictionary<int, (long Task, int N, DateTime T)> _prev = new();
    private readonly Dictionary<int, (long Task, double Tg3, DateTime At)> _live = new();
    private readonly Dictionary<int, Pending> _pending = new();
    private readonly HashSet<int> _cancelled = new();
    private readonly Dictionary<int, (long Task, double Frac, DateTime At)> _progress = new();   // Prompt-Fortschritt je Slot
    private readonly Dictionary<int, int> _maxTok = new();                                      // max_tokens je Aufgabe
    private readonly Dictionary<int, int> _promptTotal = new();                                 // Prompt-Länge je Aufgabe (aus /slots, solange sie eingelesen wird)
    private readonly HashSet<int> _doneTasks = new();
    private readonly Queue<int> _doneOrder = new();
    private long _logPos = -1;
    private DateTime _lastModelQuery = DateTime.MinValue;
    private DateTime _lastPropsQuery = DateTime.MinValue;
    private int _fails;

    private sealed class Pending
    {
        public int PromptTok; public double PromptTps;
        public int GenTok; public double GenTps; public double TotalSec;
        public int LastGen;
    }

    public ServerWatcher(ServerInfo info, HttpClient http, IPlatform platform, Func<NetSnapshot> net, ClientNamer namer)
    {
        Info = info;
        _http = http;
        _platform = platform;
        _net = net;
        _namer = namer;
        SyncChildren(info.Children);
    }

    // Neue Beschreibung vom Erkennungsdurchlauf (gleicher Host:Port). Wechselt der Prozess, beginnt die Messung neu.
    public void UpdateInfo(ServerInfo info)
    {
        bool newProcess = info.Pid != Info.Pid || info.StartTicks != Info.StartTicks;
        if (newProcess) { _memReset = true; IsStrata = null; _strataLast = -1; }
        Info = info;
        SyncChildren(info.Children);
    }

    // ── Engines, die LM Studio selbst startet ──
    // Je Kind ein eigener, unsichtbarer Beobachter (nicht in der Serverliste: kein Stop, kein Save, keine Bibliothek, keine CSV).
    // Er liefert Slots, Token/s und VRAM je PID; die Karte des Programms zeigt die Werte.
    public IReadOnlyList<ServerWatcher> Children { get; private set; } = Array.Empty<ServerWatcher>();

    private void SyncChildren(IReadOnlyList<ManagedChild> want)
    {
        if (_disposed || (want.Count == 0 && Children.Count == 0)) return;
        var keep = new List<ServerWatcher>();
        foreach (var c in want)
        {
            var key = NetAddr.Key(c.Host, c.Port);
            var w = Children.FirstOrDefault(x => x.Key == key && x.Info.Pid == c.Pid && x.Info.StartTicks == c.StartTicks);
            w ??= new ServerWatcher(new ServerInfo
            {
                Key = key, Host = c.Host, Port = c.Port, Pid = c.Pid, StartTicks = c.StartTicks, DetectedBy = "child", ApiKey = c.ApiKey,
                Backend = BackendKind.LlamaCpp, Params = c.ModelPath == null ? null : LlamaServerArgs.Parse(new[] { "-m", c.ModelPath }),
            }, _http, _platform, _net, _namer);
            keep.Add(w);
        }
        var old = Children;
        Children = keep;
        foreach (var o in old.Where(o => !keep.Contains(o))) o.Dispose();
    }

    // ── Ollama und LM Studio (nur lesend) ──
    public BackendKind Kind => Info.Backend;
    public IReadOnlyList<LoadedModel> Models { get; private set; } = Array.Empty<LoadedModel>();
    // LM Studio: alle installierten Modelle (geladen oder nicht)
    public IReadOnlyList<LmStudioApi.ModelEntry> InstalledModels { get; private set; } = Array.Empty<LmStudioApi.ModelEntry>();
    public string? BackendVersion { get; private set; }
    private DateTime _lastExt = DateTime.MinValue;
    internal double? ExternalEverySeconds;   // Tests

    // Zustand der Karte eines Ollama-/LM-Studio-Servers
    public enum ExternalState { NotRunning, Idle, Loaded }
    public ExternalState External => !Online ? ExternalState.NotRunning : Models.Count == 0 ? ExternalState.Idle : ExternalState.Loaded;

    private async Task PollExternalAsync()
    {
        var now = DateTime.Now;
        StartMemoryQuery(now);
        var kids = Children;
        if (kids.Count > 0) await Task.WhenAll(kids.Select(c => c.PollAsync()));
        double kidsTps = kids.Sum(c => c.Current);
        bool kidsPrompt = kids.Any(c => c.Slots.Any(v => v.ReadingPrompt));
        double every = ExternalEverySeconds ?? (Kind == BackendKind.Ollama ? 2 : 3);
        if ((now - _lastExt).TotalSeconds < every)
        {
            if (kids.Count > 0) { Current = kidsTps; AddHistory(kidsTps, kidsPrompt); }   // Verlauf der Engines läuft in jedem Takt
            return;
        }
        _lastExt = now;
        double tps = kidsTps;
        try
        {
            if (Kind == BackendKind.Ollama)
            {
                BackendVersion ??= Info.BackendVersion ?? OllamaApi.ParseVersion(await GetStringAsync("/api/version"));
                var models = OllamaApi.ParsePs(await GetStringAsync("/api/ps")) ?? throw new InvalidDataException();
                Models = models;
            }
            else if (Kind == BackendKind.Vllm)
            {
                // vLLM meldet über /metrics; das Modell nennt /v1/models. Die VRAM-Angabe bleibt leer: vLLM liefert nur
                // eine Cache-Belegung in Prozent, keine Bytes – im Fenster erscheint dafür „–“ statt einer erfundenen Zahl.
                BackendVersion ??= Info.BackendVersion ?? VllmApi.ParseVersion(await GetStringAsync("/version"));
                var m = VllmApi.ParseMetrics(await GetStringAsync("/metrics"));
                tps = m.Tps ?? 0;
                QueueCount = m.Waiting;
                var names = VllmApi.ParseModels(await GetStringAsync("/v1/models")) ?? throw new InvalidDataException();
                Models = names.Select(n => new LoadedModel(n, 0, 0, null, null, "", "loaded")).ToList();
            }
            else
            {
                var all = LmStudioApi.ParseModels(await GetStringAsync("/api/v0/models")) ?? throw new InvalidDataException();
                InstalledModels = all;
                Models = LmStudioApi.Loaded(all);
            }
            _fails = 0; Online = true;
        }
        catch
        {
            if (++_fails >= 3 && Online) { Online = false; Models = Array.Empty<LoadedModel>(); _mem = MemInfo.Empty; }
            else if (!Online) { Models = Array.Empty<LoadedModel>(); }
            tps = Online ? kidsTps : 0;
        }
        Current = tps;
        AddHistory(tps, kidsPrompt);
    }

    // Ollama: Modell aus dem Speicher nehmen (keep_alive = 0). Nur nach ausdrücklicher Bestätigung aufrufen.
    public async Task<bool> UnloadAsync(string model)
    {
        if (Kind != BackendKind.Ollama || _disposed) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Url.TrimEnd('/') + "/api/generate")
            { Content = new StringContent(OllamaApi.UnloadBody(model), Encoding.UTF8, "application/json") };
            using var resp = await _http.SendAsync(req);
            _lastExt = DateTime.MinValue;
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // GET mit dem API-Schlüssel des Servers (falls einer gesetzt ist); der Wert verlässt diese Methode nie
    private Task<HttpResponseMessage> SendGetAsync(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Url.TrimEnd('/') + path);
        if (Info.ApiKey is { } key) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key.Reveal());
        return _http.SendAsync(req);
    }

    private async Task<string> GetStringAsync(string path)
    {
        using var resp = await SendGetAsync(path);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    public bool PromptAt(int i) => _promptFlags[(_historyHead - HistoryCount + i + HistoryLength) % HistoryLength];
    public double HistoryAt(int i) => History[(_historyHead - HistoryCount + i + HistoryLength) % HistoryLength];

    // Spitze und Ø aktiv gelten beide für den sichtbaren Verlauf
    public double Peak
    {
        get { double m = 0; for (int i = 0; i < HistoryCount; i++) m = Math.Max(m, HistoryAt(i)); return m; }
    }

    public double AverageActive()
    {
        double sum = 0; int n = 0;
        for (int i = 0; i < HistoryCount; i++) { var v = HistoryAt(i); if (v > 0) { sum += v; n++; } }
        return n == 0 ? 0 : sum / n;
    }

    // JSON ohne Ausnahmen lesen: fehlende oder falsch getypte Felder ergeben den Vorgabewert
    private static int JInt(JsonElement e, string name, int def = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : def;
    private static long JLong(JsonElement e, string name, long def = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : def;
    private static bool JBool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // Eine kaputte Antwort oder ein Fehler darf nie bis in die Oberfläche durchschlagen
    public async Task PollAsync()
    {
        if (_disposed) return;
        try { if (Kind != BackendKind.LlamaCpp) await PollExternalAsync(); else await PollCoreAsync(); }
        catch { /* Werte bleiben stehen, der nächste Takt versucht es neu */ }
    }

    private async Task PollCoreAsync()
    {
        var now = DateTime.Now;

        // /props alle 3 s (weckt einen schlafenden Server nicht auf); schläft er, wird /slots nicht angefasst
        if ((now - _lastPropsQuery).TotalSeconds >= 3) await QueryPropsAsync(now);
        if (Sleeping)
        {
            _fails = 0; Loading = false; Online = true;
            Slots = Array.Empty<SlotView>(); Current = 0; _generatedRunning = 0;
            QueueCount = null;
            try { ReadLog(); } catch { }
            StartMemoryQuery(now);
            AddHistory(0, false);
            return;
        }

        string? slotsJson = null;
        bool loading = false;
        try
        {
            using var resp = await SendGetAsync("/slots");
            if (resp.IsSuccessStatusCode) slotsJson = await resp.Content.ReadAsStringAsync();
            else if ((int)resp.StatusCode == 503) loading = true;   // Modell wird geladen
        }
        catch { }

        try { ReadLog(); } catch { }
        StartMemoryQuery(now);

        if (loading)
        {
            _fails = 0;
            GoOffline(true);
            return;
        }

        JsonDocument? doc = null;
        if (slotsJson != null)
        {
            try { doc = JsonDocument.Parse(slotsJson); } catch { }
            if (doc != null && doc.RootElement.ValueKind != JsonValueKind.Array) { doc.Dispose(); doc = null; }
        }

        if (doc == null)
        {
            // Erst nach mehreren Fehlschlägen in Folge offline; bis dahin die letzten Werte behalten
            if (++_fails < OfflineAfterFails)
            {
                if (Online) AddHistory(Current, Slots.Any(v => v.ReadingPrompt));
                return;
            }
            GoOffline(false);
            return;
        }
        _fails = 0;
        Loading = false;

        bool wasOnline = Online;
        Online = true;
        if (!wasOnline) _lastModelQuery = DateTime.MinValue;
        if ((now - _lastModelQuery).TotalSeconds > 15)
        {
            _lastModelQuery = now;   // auch bei Fehler: nicht öfter als alle 15 s versuchen
            await QueryModelAsync();
            if (IsStrata == null) await DetectStrataAsync();
        }
        if (IsStrata == true && await PollStrataAsync(doc, now)) return;

        // Wartende Anfragen: llama-server zählt sie in /metrics (requests_deferred). Alle 4 s reicht; Strata
        // liefert seine Live-Werte oben schon selbst.
        if ((now - _lastMetricsQuery).TotalSeconds >= (MetricsEverySeconds ?? 4))
        {
            _lastMetricsQuery = now;
            try
            {
                // Ein Abruf für beides: Warteschlange und die Zähler des spekulativen Decodings
                var metrics = await GetStringAsync("/metrics");
                QueueCount = LlamaMetricsApi.Parse(metrics).Deferred;
                Spec = LlamaSpecApi.Parse(metrics);
            }
            catch { QueueCount = null; }
        }

        var list = new List<SlotView>();
        double sum = 0;
        long running = 0;
        using (doc)
        {
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("id", out var idp) || !idp.TryGetInt32(out int id)) continue;
                bool busy = JBool(s, "is_processing");
                long task = JLong(s, "id_task", -1);
                int ctxMax = JInt(s, "n_ctx");
                int ctxNow = JInt(s, "n_prompt_tokens");   // Token im Kontext (Prompt + Erzeugtes)
                int promptDone = JInt(s, "n_prompt_tokens_processed");
                int n = 0, remain = -1;
                if (s.TryGetProperty("next_token", out var nt))
                {
                    var first = nt.ValueKind == JsonValueKind.Array ? (nt.GetArrayLength() > 0 ? nt[0] : default) : nt;
                    n = JInt(first, "n_decoded");
                    remain = JInt(first, "n_remain", -1);
                }
                int maxTok = 0;
                if (s.TryGetProperty("params", out var prm))
                {
                    maxTok = JInt(prm, "max_tokens");
                    if (maxTok == 0) maxTok = JInt(prm, "n_predict");
                }
                if (maxTok <= 0 && remain >= 0) maxTok = n + remain;

                double tps = 0;
                if (busy)
                {
                    // Eigene Messung aus der Differenz erzeugter Token ...
                    if (_prev.TryGetValue(id, out var pv) && pv.Task == task && n > pv.N)
                        tps = (n - pv.N) / Math.Max(0.2, (now - pv.T).TotalSeconds);
                    // ... genauer: der Server-eigene 3-s-Wert aus dem Log, falls frisch
                    if (_live.TryGetValue(id, out var lv) && lv.Task == task && (now - lv.At).TotalSeconds < 6) tps = lv.Tg3;
                    sum += tps;
                    if (!_doneTasks.Contains((int)task)) running += n;
                    if (maxTok > 0) _maxTok[(int)task] = maxTok;
                    if (n == 0 && ctxNow > 0) { _promptTotal[(int)task] = ctxNow; if (_promptTotal.Count > 64) _promptTotal.Clear(); }
                }
                _prev[id] = (task, n, now);
                double frac = -1;
                if (busy && n == 0 && _progress.TryGetValue(id, out var pg) && pg.Task == task) frac = pg.Frac;
                list.Add(new SlotView(id, busy, busy && n == 0, tps, n, ctxNow, ctxMax, busy ? maxTok : 0, promptDone, frac, task));
            }
        }
        Slots = list;
        _generatedRunning = running;
        Current = sum;
        AddHistory(sum, list.Any(v => v.ReadingPrompt));
    }

    // ── Strata: Live-Werte aus /metrics statt aus /slots und Log (siehe StrataApi) ──
    // null = noch nicht geprüft; die Erkennung läuft einmal je Serverprozess über /health
    public bool? IsStrata { get; private set; }
    private double _strataLast = -1;   // Zeitstempel der neuesten schon übernommenen abgeschlossenen Anfrage

    private async Task DetectStrataAsync()
    {
        try
        {
            using var resp = await SendGetAsync("/health");
            if (!resp.IsSuccessStatusCode) return;   // später erneut versuchen
            IsStrata = StrataApi.IsStrataHealth(await resp.Content.ReadAsStringAsync());
        }
        catch { }
    }

    // true = Werte gesetzt; false = /metrics unbrauchbar, dann gilt die normale /slots-Auswertung
    private async Task<bool> PollStrataAsync(JsonDocument slotsDoc, DateTime now)
    {
        StrataApi.Metrics? m;
        try { m = StrataApi.Parse(await GetStringAsync("/metrics")); } catch { m = null; }
        if (m == null) return false;
        slotsDoc.Dispose();

        bool initial = _strataLast < 0;
        foreach (var r in m.Requests.Where(r => r.Time > _strataLast).OrderBy(r => r.Time).TakeLast(12))
        {
            var f = new FinishedRequest(++_seqCounter, Name, Model, 0, 0, r.PromptTokens,
                r.PromptMs > 0 ? r.PromptTokens * 1000.0 / r.PromptMs : 0, r.DecodeTps, r.OutputTokens, r.Seconds,
                initial ? null : DateTime.Now, StrataApi.Status(r.Finish), initial ? "" : string.Join(" + ", Clients), r.PromptTokens, Key);
            AddFinished(f);
            if (!initial) { _generatedDone += r.OutputTokens; RequestFinished?.Invoke(f); }
        }
        _strataLast = Math.Max(0, m.Requests.Count > 0 ? m.Requests.Max(r => r.Time) : 0);

        var l = m.Live;
        int ctxMax = m.MaxContext > 0 ? m.MaxContext : Props?.NCtx ?? 0;
        double frac = l.Reading && l.PromptTotal > 0 ? Math.Clamp((double)l.PromptRead / l.PromptTotal, 0, 1) : -1;
        long task = l.Busy ? m.TotalRequests + 1 : -1;
        var slot = new SlotView(0, l.Busy, l.Reading, l.Busy && !l.Reading ? l.Tps : 0, l.Busy ? l.Generated : 0,
            l.Busy ? l.PromptTokens + l.Generated : 0, ctxMax, l.Busy ? l.MaxTokens : 0, l.Reading ? l.PromptRead : l.PromptTokens, frac, task);
        Slots = new[] { slot };
        _generatedRunning = slot.Generated;
        Current = slot.Tps;
        AddHistory(slot.Tps, slot.ReadingPrompt);
        return true;
    }

    // Nicht erreichbar (oder lädt noch): letzte Werte verwerfen, Verlauf läuft mit Nullen weiter
    private void GoOffline(bool loading)
    {
        Online = false; Loading = loading; Sleeping = false;
        Slots = Array.Empty<SlotView>(); Model = "–"; ModelFileGb = null;
        QueueCount = null;
        Spec = default;
        _mem = MemInfo.Empty;
        _memReset = true;                              // PDH-Abfrage und CPU-Zeiten im Hintergrund-Task neu beginnen
        _prev.Clear(); _live.Clear(); _progress.Clear();
        _generatedRunning = 0;
        AddHistory(0, false); Current = 0;
    }

    // /v1/models alle 15 s (Modellwechsel); Modellgröße aus meta.size, sonst aus der Modelldatei laut /props
    private async Task QueryModelAsync()
    {
        try
        {
            using var m = JsonDocument.Parse(await GetStringAsync("/v1/models"));
            string id = "–";
            long? size = null;
            if (m.RootElement.ValueKind == JsonValueKind.Object && m.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 &&
                data[0].ValueKind == JsonValueKind.Object)
            {
                if (data[0].TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.String) id = idp.GetString() ?? "–";
                if (data[0].TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                    meta.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var bytes) && bytes > 0) size = bytes;
            }
            Model = id;
            if (size != null) ModelFileGb = size.Value / 1073741824.0;
        }
        catch { }
    }

    // /props: Schlafzustand, Bauversion, Modellpfad. Weckt einen schlafenden Server nicht auf.
    private async Task QueryPropsAsync(DateTime now)
    {
        _lastPropsQuery = now;
        try
        {
            var raw = await GetStringAsync("/props");
            var p = LlamaProps.Parse(raw);
            if (p == null) return;
            Props = p; PropsJson = raw;
            Sleeping = p.IsSleeping;
            if (ModelFileGb == null && p.ModelPath is { Length: > 0 } path && File.Exists(path))
                ModelFileGb = new FileInfo(path).Length / 1073741824.0;
        }
        catch { }
    }

    // Alle paar Sekunden, nie auf dem UI-Thread: die Abfragen (PDH, Prozesse) können ein paar ms dauern
    private void StartMemoryQuery(DateTime now)
    {
        if (_memBusy || (now - _lastMemQuery).TotalSeconds < 3) return;
        _lastMemQuery = now;
        _memBusy = true;
        bool online = Online;
        _ = Task.Run(() =>
        {
            try { ReadMemory(online); }
            catch { }
            finally { _memBusy = false; }
        });
    }

    // Nur für Server auf diesem Rechner (mit bekannter PID): ist der Server online, dazu GPU-Speicher (PDH), RAM,
    // CPU und Clients. Läuft im Hintergrund-Task (nie zwei gleichzeitig je Server).
    private void ReadMemory(bool online)
    {
        lock (_memLock)
        {
            if (_disposed) return;
            if (_memReset)
            {
                _memReset = false;
                _gpuQuery?.Dispose(); _gpuQuery = null; _cpuPrevPid = 0;
            }
            var info = Info;
            if (info.Pid is not int pid || !online)
            {
                _mem = MemInfo.Empty;
                return;
            }
            // Strata: der lauschende Python-Server startet die Engine als Kindprozess; VRAM liegt dort, RAM und CPU zählen beide
            int? engine = IsStrata == true ? FindDescendant(pid, StrataApi.EngineProcess, 3) : null;
            int gpuPid = engine ?? pid;
            if (_gpuQuery == null || _gpuQuery.Pid != gpuPid)
            {
                _gpuQuery?.Dispose();
                _gpuQuery = _platform.OpenGpuMemory(gpuPid);
            }
            var clients = _namer.Names(_net().Connections, info.Port, Environment.ProcessId);
            var gm = _gpuQuery?.Read();
            double? cpuPct = null;
            double? ram = null, commit = null;
            var usage = _platform.ReadUsage(pid);
            if (engine is int ep && _platform.ReadUsage(ep) is { } eu)
                usage = usage == null ? eu : new ProcessUsage(usage.WorkingSetGb + eu.WorkingSetGb, usage.PrivateGb + eu.PrivateGb, usage.CpuTime + eu.CpuTime);
            if (usage is { } u)
            {
                ram = u.WorkingSetGb; commit = u.PrivateGb;
                var at = DateTime.Now;
                if (_cpuPrevPid == gpuPid && at > _cpuPrevAt && u.CpuTime >= _cpuPrev)
                    cpuPct = Math.Clamp(100.0 * (u.CpuTime - _cpuPrev).TotalSeconds / ((at - _cpuPrevAt).TotalSeconds * _platform.ProcessorCount), 0, 100);
                _cpuPrev = u.CpuTime; _cpuPrevAt = at; _cpuPrevPid = gpuPid;
            }
            _mem = new MemInfo(gm?.DedicatedGb, gm?.SharedGb, ram, commit, cpuPct, clients);
        }
    }

    // Nachfahre mit diesem Programmnamen (Breitensuche, höchstens depth Ebenen: venv-python -> python -> strata)
    private int? FindDescendant(int pid, string name, int depth)
    {
        var level = new List<int> { pid };
        for (int d = 0; d < depth && level.Count > 0; d++)
        {
            var next = new List<int>();
            foreach (var p in level)
                foreach (var c in _platform.ChildProcesses(p))
                {
                    if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c.Pid;
                    next.Add(c.Pid);
                }
            level = next;
        }
        return null;
    }

    public void Dispose()
    {
        lock (_memLock)
        {
            _disposed = true;
            _gpuQuery?.Dispose(); _gpuQuery = null;
        }
        var kids = Children;
        Children = Array.Empty<ServerWatcher>();
        foreach (var k in kids) k.Dispose();
    }

    private void AddHistory(double v, bool readingPrompt)
    {
        History[_historyHead] = v;
        _promptFlags[_historyHead] = readingPrompt;
        _historyHead = (_historyHead + 1) % HistoryLength;
        if (HistoryCount < HistoryLength) HistoryCount++;
    }

    // Liest nur neue, vollständige Zeilen (bis zum letzten Zeilenumbruch) per Byte-Offset.
    // _logPos rückt erst nach erfolgreichem Lesen vor.
    private void ReadLog()
    {
        var path = Log;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        string text;
        bool initial = _logPos < 0;
        long newPos;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = fs.Length;
            long pos = _logPos;
            bool skipPartial = false, restarted = false;
            if (initial)
            {
                pos = Math.Max(0, len - 256 * 1024);   // beim Start die letzten 256 KB
                skipPartial = pos > 0;
                ReadHeader(fs, len);
            }
            else if (len < pos)   // Log neu angelegt: Serverneustart
            {
                pos = 0; restarted = true;
                ReadHeader(fs, len);
            }
            const long max = 4L * 1024 * 1024;
            if (len - pos > max) { pos = len - max; skipPartial = true; }

            if (restarted)   // alter Zustand gehört zum früheren Serverlauf
            {
                _live.Clear(); _pending.Clear(); _cancelled.Clear(); _progress.Clear();
                _doneTasks.Clear(); _doneOrder.Clear(); _maxTok.Clear();
            }
            if (len == pos) { _logPos = pos; return; }

            var buf = new byte[len - pos];
            fs.Seek(pos, SeekOrigin.Begin);
            int got = 0;
            while (got < buf.Length) { int r = fs.Read(buf, got, buf.Length - got); if (r <= 0) break; got += r; }
            if (got <= 0) return;

            int last = Array.LastIndexOf(buf, (byte)'\n', got - 1);
            if (last < 0) { if (restarted) _logPos = 0; return; }   // noch keine vollständige Zeile: beim nächsten Mal weiter
            int from = skipPartial ? Array.IndexOf(buf, (byte)'\n') + 1 : 0;   // angeschnittene erste Zeile verwerfen
            text = Encoding.UTF8.GetString(buf, from, last + 1 - from);
            newPos = pos + last + 1;
        }
        catch { return; }
        _logPos = newPos;

        foreach (var line in text.Split('\n'))
        {
            if (line.Length < 20) continue;
            try { ParseLine(line, initial); } catch { /* eine unverständliche Zeile überspringen */ }
        }
        if (_pending.Count > 64)   // nie abgeschlossene Aufgaben nicht ewig mitschleppen
            foreach (var k in _pending.Keys.Order().Take(_pending.Count - 32).ToList()) _pending.Remove(k);
        if (_cancelled.Count > 64) _cancelled.Clear();
    }

    private static int I(Match m, int g) => LogPatterns.I(m, g);
    private static double D(Match m, int g) => LogPatterns.D(m, g);

    private void ParseLine(string line, bool initial)
    {
        Match m;
        if ((m = LogPatterns.Live().Match(line)).Success)
        {
            int slot = I(m, 1), t = I(m, 2);
            Pend(t).LastGen = I(m, 3);
            if (!initial)   // alte Werte aus dem Start-Log nicht als "frisch" behandeln
                _live[slot] = (t, D(m, 5), DateTime.Now);
        }
        else if ((m = LogPatterns.Prompt().Match(line)).Success)
        {
            var p = Pend(I(m, 1));
            p.PromptTok = I(m, 3);
            p.PromptTps = D(m, 4);
        }
        else if ((m = LogPatterns.Eval().Match(line)).Success)
        {
            var p = Pend(I(m, 1));
            p.GenTok = I(m, 3);
            p.GenTps = D(m, 4);
            p.TotalSec = Math.Max(p.TotalSec, D(m, 2) / 1000);
        }
        else if ((m = LogPatterns.Total().Match(line)).Success)
            Pend(I(m, 1)).TotalSec = D(m, 2) / 1000;
        else if ((m = LogPatterns.Progress().Match(line)).Success)
        {
            if (!initial) _progress[I(m, 1)] = (I(m, 2), D(m, 4), DateTime.Now);
        }
        else if ((m = LogPatterns.Cancel().Match(line)).Success)
            _cancelled.Add(I(m, 1));
        else if ((m = LogPatterns.Release().Match(line)).Success)
            OnRelease(I(m, 1), I(m, 2), m.Groups[4].Value == "1", initial);
    }

    // Serverstart-Zeilen am Loganfang (kv_unified); nur beim ersten Lesen und nach Neustart des Servers
    private void ReadHeader(FileStream fs, long len)
    {
        try
        {
            var buf = new byte[(int)Math.Min(len, 64 * 1024)];
            fs.Seek(0, SeekOrigin.Begin);
            int got = fs.Read(buf, 0, buf.Length);
            var m = LogPatterns.Unified().Match(Encoding.UTF8.GetString(buf, 0, got));
            if (m.Success) Unified = m.Groups[1].Value == "true";
        }
        catch { }
    }

    private Pending Pend(int task)
    {
        if (!_pending.TryGetValue(task, out var p)) _pending[task] = p = new Pending();
        return p;
    }

    private void OnRelease(int slot, int task, bool truncated, bool fromLog)
    {
        _pending.Remove(task, out var r);
        bool cancelled = _cancelled.Remove(task);
        _live.Remove(slot);
        if (r == null && !cancelled) return;
        r ??= new Pending();
        int gen = r.GenTok > 0 ? r.GenTok : r.LastGen;   // abgebrochen: Token aus der letzten n_gen-Zeile
        if (gen == 0 && !cancelled) return;
        bool atLimit = _maxTok.TryGetValue(task, out var mt) && mt > 0 && gen >= mt;   // Antwort lief ins max_tokens-Limit
        _maxTok.Remove(task);
        if (_maxTok.Count > 64) _maxTok.Clear();
        var status = cancelled ? ReqStatus.Cancelled : truncated || atLimit ? ReqStatus.Truncated : ReqStatus.Done;
        var f = new FinishedRequest(++_seqCounter, Name, Model, task, slot, r.PromptTok, r.PromptTps, r.GenTps, gen, r.TotalSec,
            fromLog ? null : DateTime.Now, status, fromLog ? "" : string.Join(" + ", Clients), _promptTotal.Remove(task, out var pt) ? pt : 0, Key);
        AddFinished(f);
        if (_doneTasks.Add(task)) { _doneOrder.Enqueue(task); if (_doneOrder.Count > 256) _doneTasks.Remove(_doneOrder.Dequeue()); }
        if (!fromLog) { _generatedDone += gen; RequestFinished?.Invoke(f); }
    }
}
