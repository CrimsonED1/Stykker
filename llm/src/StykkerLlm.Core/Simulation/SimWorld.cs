using System.Globalization;
using System.Text;

namespace StykkerLlm.Core;

public enum SimState { Stopped, Running, Paused }

// Die simulierte Welt: Server mit Slots, Anfragen nach Poisson-Ankunft, Prompt- und Erzeugungsphase, GPU/System-Werte.
// Alles läuft ohne echte Prozesse und Sockets: SimPlatform und SimHandler zeigen den Zustand dem Monitor wie einen echten Rechner,
// ein synthetisches llama-server-Log (--log-file) lässt die Log-Auswertung, Aufnahme und CSV genau wie im Betrieb laufen.
// Step(now) schaltet die Zeit weiter (der SimHost ruft es im Takt auf; Tests rufen es von Hand mit künstlicher Uhr).
public sealed class SimWorld
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] ToolNames = { "read_file", "write_file", "run_command", "web_search", "list_directory", "grep" };
    private static readonly string[] Agents = { "opencode/1.4.2", "python-httpx/0.27.2", "OpenWebUI/0.6.1", "continue/1.0.8" };

    public sealed class Slot
    {
        public int Id;
        public bool Busy, Prompting, Cancelled;
        public long Task;
        public int PromptTokens, GenTarget, ReasoningTokens, MaxTokens;   // MaxTokens: Grenze des Clients (max_tokens); GenTarget: wo die Antwort von selbst endet
        public double PromptDone, Gen, Tps, PromptTps, CancelAt;
        public bool Truncate;
        public string[] Tools = Array.Empty<string>();
        public string Agent = "";
        public DateTime Start, GenStart, NextLog;
        public double PromptLogged;
    }

    public sealed class Server
    {
        public SimServerSpec Spec { get; }
        public int Pid { get; internal set; }
        public int EnginePid { get; internal set; }
        public int Port { get; internal set; }
        public int EnginePort { get; internal set; }
        public string ApiKey { get; internal set; } = "";
        public long StartTicks { get; internal set; }
        public string? LogPath { get; internal set; }
        public bool Loaded { get; internal set; } = true;   // Ollama: Modell im Speicher
        public Slot[] Slots { get; internal set; } = Array.Empty<Slot>();
        public int Queue { get; internal set; }
        public double CpuSeconds { get; internal set; }
        public DateTime LastActivity { get; internal set; } = DateTime.MinValue;
        public string Key => NetAddr.Key("127.0.0.1", Port);
        public bool HasEngine => Spec.Kind != BackendKind.Ollama;
        public int SlotContext => Math.Max(256, Spec.Context / Math.Max(1, Spec.Slots));
        public int BusySlots => Slots.Count(s => s.Busy);
        internal long TaskCounter;
        // Spekulatives Decoding: Zähler wie beim echten llama-server, null wenn kein Entwurfsmodell läuft
        public SpecCounters Draft { get; } = new();
        // Strata: abgeschlossene Anfragen für /metrics (neueste zuerst, höchstens 20)
        public List<StrataDone> StrataDone { get; } = new();
        internal Server(SimServerSpec spec) => Spec = spec;
    }

    public sealed record StrataDone(double Time, int PromptTokens, double PromptMs, int OutputTokens, double DecodeTps, double Seconds, string Finish);

    /// <summary>Zähler des spekulativen Decodings. Ohne Entwurfsmodell (DraftModel = 0) bleibt alles bei null.</summary>
    public sealed class SpecCounters
    {
        public int DraftModel { get; set; }
        public long DraftTokens { get; set; }
        public long DraftAccepted { get; set; }
        public long DraftSteps { get; set; }

        public bool HasDraftModel => DraftModel > 0;
        public bool Active => HasDraftModel && DraftTokens > 0;

        public void Count(double generatedTokens, double rate)
        {
            if (DraftModel <= 0) return;
            long gen = (long)generatedTokens;
            long drafted = gen * DraftModel;
            DraftSteps += gen;
            DraftTokens += drafted;
            DraftAccepted += (long)(drafted * Math.Clamp(rate, 0, 1));   // angenommene Entwürfe
        }
    }

    private static void RememberStrata(Server s, StrataDone d)
    {
        s.StrataDone.Insert(0, d);
        if (s.StrataDone.Count > 20) s.StrataDone.RemoveAt(s.StrataDone.Count - 1);
    }

    private readonly List<Server> _servers = new();
    private readonly Random _rng;
    private int _nextPid = 41000, _nextEnginePort = 50100;
    private DateTime _last;

    public object Gate { get; } = new();
    public SimState State { get; private set; } = SimState.Stopped;
    public bool ShowGpu { get; set; } = true;
    public bool ShowSystem { get; set; } = true;
    public string GpuName { get; set; } = "Simulated GPU (24 GB)";
    public double GpuTotalGb { get; set; } = 24;
    // Ordner für die synthetischen Server-Logs (null = keine Logdateien)
    public string? LogDir { get; set; }
    // Ein Server wurde hinzugefügt oder entfernt
    public event Action? Changed;
    // Eine Anfrage ist zu Ende: so hätte sie der Aufnahme-Proxy gesehen (Serverschlüssel, Beobachtung). Der Host reicht sie nur an einen laufenden Proxy weiter.
    public event Action<string, ProxyRecord>? Observed;

    public SimWorld(int? seed = null) => _rng = seed is int s ? new Random(s) : new Random();

    public IReadOnlyList<Server> Servers { get { lock (Gate) return _servers.ToList(); } }

    // ── Zustand ──

    public void Start() { lock (Gate) { State = SimState.Running; _last = default; } }
    public void Pause() { lock (Gate) { if (State == SimState.Running) State = SimState.Paused; } }
    public void Resume() { lock (Gate) { if (State == SimState.Paused) State = SimState.Running; } }
    public void Stop() { lock (Gate) State = SimState.Stopped; }

    // ── Server hinzufügen und entfernen ──

    public Server Add(SimServerSpec spec)
    {
        spec.Normalize();
        Server s;
        lock (Gate)
        {
            s = new Server(spec) { Pid = _nextPid++, StartTicks = DateTime.Now.AddMinutes(-_rng.Next(5, 120)).ToFileTime() + _nextPid };
            s.Draft.DraftModel = spec.DraftModel;
            s.Port = spec.Kind switch
            {
                BackendKind.Ollama => FreePort(11434),
                BackendKind.LmStudio => FreePort(1234),
                _ => FreePort(8081),
            };
            if (spec.Strata) s.EnginePid = _nextPid++;   // strata.exe unter dem Python-Server
            if (spec.Kind == BackendKind.LmStudio)
            {
                s.EnginePid = _nextPid++;
                s.EnginePort = _nextEnginePort++;
                s.ApiKey = "sim-" + Guid.NewGuid().ToString("N")[..16];
            }
            s.Slots = spec.Kind == BackendKind.Ollama ? Array.Empty<Slot>() : Enumerable.Range(0, spec.Slots).Select(i => new Slot { Id = i }).ToArray();
            if (s.HasEngine && LogDir != null && !spec.Strata)
            {
                try
                {
                    Directory.CreateDirectory(LogDir);
                    s.LogPath = Path.Combine(LogDir, $"sim-{s.Port}.log");
                    File.WriteAllText(s.LogPath, "kv_unified = 'false'\n");
                }
                catch { s.LogPath = null; }
            }
            _servers.Add(s);
        }
        Changed?.Invoke();
        return s;
    }

    private int FreePort(int start)
    {
        int p = start;
        while (_servers.Any(x => x.Port == p)) p++;
        return p;
    }

    public bool Remove(Guid id)
    {
        bool removed;
        lock (Gate) removed = _servers.RemoveAll(s => s.Spec.Id == id) > 0;
        if (removed) Changed?.Invoke();
        return removed;
    }

    // Beenden über die Plattform ("Stop" auf der Karte): nur der Serverprozess selbst, nicht die Engine von LM Studio
    internal StopOutcome Terminate(int pid, long expectedStartTicks)
    {
        Server? s;
        lock (Gate) s = _servers.FirstOrDefault(x => x.Pid == pid);
        if (s == null) return StopOutcome.NotFound;
        if (expectedStartTicks != 0 && s.StartTicks != expectedStartTicks) return StopOutcome.ProcessChanged;
        return Remove(s.Spec.Id) ? StopOutcome.Stopped : StopOutcome.NotFound;
    }

    // "Start" eines gemerkten Profils im Simulator: ein simulierter llama-server mit den Werten aus der Kommandozeile erscheint nach kurzer Zeit
    public SimServerSpec SpecFromLaunch(LaunchSpec spec)
    {
        var a = LlamaServerArgs.Parse(spec.Args);
        int slots = a.Np is > 0 ? a.Np.Value : 1;
        var model = a.Model != null ? ServerInfo.ModelName(a.Model) : spec.Name;
        return new SimServerSpec
        {
            Kind = BackendKind.LlamaCpp, Name = spec.Name, Model = model, Context = a.Ctx is > 0 ? a.Ctx.Value : 8192, Slots = slots,
            TpsMin = 35, TpsMax = 60, RequestsPerMinute = 8, ModelGb = 5,
        };
    }

    public void StartFromLaunch(LaunchSpec spec, TimeSpan delay)
    {
        var s = SpecFromLaunch(spec);
        _ = Task.Delay(delay).ContinueWith(_ => Add(s));
    }

    // ── Zeit weiterschalten ──

    public void Step(DateTime now)
    {
        lock (Gate)
        {
            double dt = _last == default ? 0 : Math.Clamp((now - _last).TotalSeconds, 0, 1.0);
            _last = now;
            if (State == SimState.Stopped || dt <= 0) return;
            foreach (var s in _servers.ToList())
            {
                if (!s.HasEngine) continue;
                try { StepServer(s, now, dt); } catch { }
            }
        }
    }

    private double Uniform(double a, double b) => a + (b - a) * _rng.NextDouble();

    // Lognormal um den Median (für Prompt- und Antwortlängen)
    private double LogNormal(double median, double sigma)
    {
        double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
        double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return median * Math.Exp(sigma * z);
    }

    private void StepServer(Server s, DateTime now, double dt)
    {
        var log = new StringBuilder();
        var spec = s.Spec;
        if (State == SimState.Running && spec.RequestsPerMinute > 0 && _rng.NextDouble() < spec.RequestsPerMinute / 60.0 * dt) s.Queue++;
        if (s.Queue > 20) s.Queue = 20;
        double busyFrac = 0;
        foreach (var sl in s.Slots)
        {
            if (!sl.Busy && s.Queue > 0) { s.Queue--; Begin(s, sl, now); }
            if (!sl.Busy) continue;
            busyFrac += 1.0 / s.Slots.Length;
            s.LastActivity = now;
            if (sl.Prompting)
            {
                sl.PromptDone = Math.Min(sl.PromptTokens, sl.PromptDone + sl.PromptTps * dt);
                double frac = sl.PromptTokens > 0 ? sl.PromptDone / sl.PromptTokens : 1;
                if (frac < 1 && frac - sl.PromptLogged >= 0.25)
                {
                    sl.PromptLogged = frac;
                    log.Append("slot update_slots: id ").Append(sl.Id.ToString(Inv).PadLeft(2)).Append(" | task ").Append(sl.Task.ToString(Inv))
                       .Append(" | prompt processing, n_tokens = ").Append(((int)sl.PromptDone).ToString(Inv)).Append(", progress = ").AppendLine(frac.ToString("0.00", Inv));
                }
                if (frac >= 1)
                {
                    double ms = sl.PromptTokens / Math.Max(1, sl.PromptTps) * 1000;
                    log.Append("slot print_timing: id ").Append(sl.Id.ToString(Inv).PadLeft(2)).Append(" | task ").Append(sl.Task.ToString(Inv))
                       .Append(" |  prompt eval time = ").Append(ms.ToString("0.00", Inv)).Append(" ms / ").Append(sl.PromptTokens.ToString(Inv).PadLeft(5))
                       .Append(" tokens (").Append((ms / Math.Max(1, sl.PromptTokens)).ToString("0.00", Inv)).Append(" ms per token, ")
                       .Append(sl.PromptTps.ToString("0.00", Inv)).AppendLine(" tokens per second)");
                    sl.Prompting = false; sl.GenStart = now; sl.NextLog = now;
                }
                continue;
            }
            // Erzeugung: Geschwindigkeit schwankt leicht um den Wert dieser Anfrage
            sl.Tps = Math.Clamp(sl.Tps * (1 + (_rng.NextDouble() - 0.5) * 0.05), spec.TpsMin * 0.8, spec.TpsMax * 1.1);
            sl.Gen += sl.Tps * dt;
            // Spekulatives Decoding: je erzeugtem Token entwirft das kleine Modell DraftModel Token, davon werden
            // je Entwurfsdurchlauf rund 60 % angenommen (schwankt leicht, damit sich der Wert über die Zeit bewegt)
            if (s.Draft.HasDraftModel) s.Draft.Count(sl.Tps * dt, 0.58 + _rng.NextDouble() * 0.12);
            if (now >= sl.NextLog && sl.Gen >= 1)
            {
                sl.NextLog = now.AddSeconds(0.5);
                log.Append("slot print_timing: id ").Append(sl.Id.ToString(Inv).PadLeft(2)).Append(" | task ").Append(sl.Task.ToString(Inv)).Append(" | n_gen = ")
                   .Append(((int)sl.Gen).ToString(Inv)).Append(", tg = ").Append(sl.Tps.ToString("0.0", Inv)).Append(" t/s, tg_3s = ")
                   .Append(sl.Tps.ToString("0.0", Inv)).AppendLine(" t/s");
            }
            if (sl.Cancelled && sl.Gen >= sl.CancelAt && (now - sl.GenStart).TotalSeconds >= 1)
            {
                log.Append("srv  cancel_tasks: cancel task, id_task = ").AppendLine(sl.Task.ToString(Inv));
                Finish(s, sl, now, log, cancelled: true);
            }
            else if (sl.Gen >= sl.GenTarget) Finish(s, sl, now, log, cancelled: false);
        }
        s.CpuSeconds += dt * (0.2 + busyFrac * 3.0);
        if (log.Length > 0) Append(s, log.ToString());
    }

    private void Begin(Server s, Slot sl, DateTime now)
    {
        var spec = s.Spec;
        int ctx = s.SlotContext;
        sl.Busy = true; sl.Prompting = true; sl.Cancelled = false;
        sl.Task = ++s.TaskCounter * 3 + 1;
        sl.Start = now; sl.PromptDone = 0; sl.PromptLogged = 0; sl.Gen = 0;
        sl.PromptTokens = (int)Math.Clamp(LogNormal(700, 0.9), 30, Math.Max(40, ctx * 0.6));
        int room = Math.Max(30, ctx - sl.PromptTokens - 16);
        sl.GenTarget = (int)Math.Clamp(LogNormal(320, 0.8), 20, Math.Min(room, 4000));
        sl.Truncate = _rng.NextDouble() < 0.04;
        if (sl.Truncate) sl.GenTarget = Math.Min(room, Math.Max(40, sl.GenTarget));
        sl.Tps = Uniform(spec.TpsMin, spec.TpsMax);
        sl.PromptTps = sl.Tps * Uniform(8, 14);
        double think = spec.ThinkPercent / 100.0;
        sl.ReasoningTokens = think > 0 ? (int)Math.Round(sl.GenTarget * Math.Clamp(think * Uniform(0.7, 1.3), 0, 0.95)) : 0;
        sl.Tools = spec.ToolCalls && _rng.NextDouble() < 0.35
            ? Enumerable.Range(0, _rng.Next(1, 3)).Select(_ => ToolNames[_rng.Next(ToolNames.Length)]).Distinct().ToArray()
            : Array.Empty<string>();
        sl.Agent = Agents[_rng.Next(Agents.Length)];
        sl.Cancelled = _rng.NextDouble() < 0.03;
        sl.CancelAt = sl.GenTarget * Uniform(0.3, 0.8);
        if (sl.Tools.Length > 0) sl.Truncate = false;
        sl.MaxTokens = sl.Truncate ? sl.GenTarget : (sl.GenTarget < 900 ? 2048 : 4096);   // nur abgeschnittene Antworten laufen ins Limit
    }

    private void Finish(Server s, Slot sl, DateTime now, StringBuilder log, bool cancelled)
    {
        int gen = Math.Max(1, (int)Math.Min(sl.Gen, sl.GenTarget));
        double genMs = gen / Math.Max(0.1, sl.Tps) * 1000;
        double promptMs = sl.PromptTokens / Math.Max(1, sl.PromptTps) * 1000;
        string id = sl.Id.ToString(Inv).PadLeft(2), task = sl.Task.ToString(Inv);
        log.Append("slot print_timing: id ").Append(id).Append(" | task ").Append(task).Append(" |        eval time = ").Append(genMs.ToString("0.00", Inv))
           .Append(" ms / ").Append(gen.ToString(Inv).PadLeft(5)).Append(" tokens (").Append((genMs / gen).ToString("0.00", Inv)).Append(" ms per token, ")
           .Append(sl.Tps.ToString("0.00", Inv)).AppendLine(" tokens per second)");
        log.Append("slot print_timing: id ").Append(id).Append(" | task ").Append(task).Append(" |       total time = ").Append((genMs + promptMs).ToString("0.00", Inv))
           .Append(" ms / ").Append((gen + sl.PromptTokens).ToString(Inv).PadLeft(5)).AppendLine(" tokens");
        bool truncated = sl.Truncate && !cancelled;
        log.Append("srv  update_slots: release: id ").Append(id).Append(" | task ").Append(task).Append(" | stop processing: n_tokens = ")
           .Append((gen + sl.PromptTokens).ToString(Inv)).Append(", truncated = ").AppendLine(truncated ? "1" : "0");

        int reasoning = Math.Min(sl.ReasoningTokens, Math.Max(0, gen - 1));
        double ttft = promptMs / 1000 + 0.04;
        var first = sl.Start.AddSeconds(ttft);
        var firstContent = reasoning > 0 ? first.AddSeconds(reasoning / Math.Max(0.1, sl.Tps)) : first;
        string finish = cancelled ? "" : truncated ? "length" : sl.Tools.Length > 0 ? "tool_calls" : "stop";
        var rec = new ProxyRecord(s.Key, "/v1/chat/completions", sl.Start, now, first, firstContent, reasoning, gen - reasoning, sl.Tools,
            cancelled ? null : finish, sl.Agent, true, 200, cancelled);
        sl.Busy = false; sl.Prompting = false;
        if (s.Spec.Strata)
            RememberStrata(s, new StrataDone((now - DateTime.UnixEpoch).TotalSeconds, sl.PromptTokens, promptMs, gen, sl.Tps,
                (now - sl.Start).TotalSeconds, cancelled ? "cancelled" : truncated ? "length" : "stop"));
        var handler = Observed;
        if (handler != null) { try { handler(s.Key, rec); } catch { } }
    }

    private void Append(Server s, string text)
    {
        if (s.LogPath == null) return;
        try
        {
            using var fs = new FileStream(s.LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new UTF8Encoding(false).GetBytes(text);
            fs.Write(bytes, 0, bytes.Length);
        }
        catch { }
    }

    // ── Messwerte (für SimPlatform) ──

    // Server mit Anfragen in Arbeit, 0..1
    public double Load(Server s) => s.Slots.Length == 0 ? 0 : s.BusySlots / (double)s.Slots.Length;

    // Belegter Grafikspeicher eines Servers: Modell plus Kontextspeicher (grob 0,1 MB je 1000 Token Kontext und Milliarde Parameter wäre zu genau: pauschal)
    public double ServerVramGb(Server s) => s.Spec.ModelGb + s.Spec.Context * 0.00008 + 0.3;

    public double TotalVramGb()
    {
        lock (Gate) return _servers.Where(s => s.Spec.Kind != BackendKind.Ollama || s.Loaded).Sum(ServerVramGb) + 1.1;
    }

    public double TotalLoad()
    {
        lock (Gate)
        {
            var eng = _servers.Where(s => s.HasEngine).ToList();
            return eng.Count == 0 ? 0 : Math.Min(1, eng.Sum(Load) / Math.Max(1, eng.Count) * 1.15);
        }
    }

    public double TotalTps()
    {
        lock (Gate) return _servers.Sum(s => s.Slots.Where(x => x.Busy && !x.Prompting).Sum(x => x.Tps));
    }
}
