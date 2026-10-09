using System.Net;

namespace StykkerLlm.Core;

// Listener und Verbindungen einmal pro Durchlauf; wird an alle Beobachter weitergegeben
public sealed record NetSnapshot(IReadOnlyList<ListenerInfo> Listeners, IReadOnlyList<ConnectionInfo> Connections)
{
    public static readonly NetSnapshot Empty = new(Array.Empty<ListenerInfo>(), Array.Empty<ConnectionInfo>());
}

public sealed record DiscoveryResult(NetSnapshot Net, IReadOnlyList<ServerInfo> Servers);

// Ein Erkennungsdurchlauf: lauschende Ports -> Prozess -> "ist das ein bekannter Modell-Server?".
// llama.cpp wird am Programmnamen "llama-server*" oder an /props erkannt, Ollama an /api/version, LM Studio an /api/v0/models
// (umbenannte Programme, Container-Wrapper). Ergebnisse je Prozess (PID + Startzeit) werden zwischengespeichert:
// Kommandozeile, Umgebung und Probenergebnis werden nicht alle 3 s neu gelesen. Nicht threadsicher: ein Durchlauf nach dem anderen.
public sealed class ServerDiscovery
{
    internal sealed class ProcEntry
    {
        public ProcessDetails? Details;
        public bool Deep;   // Kommandozeile, Arbeitsordner und Umgebung wurden gelesen (oder der Versuch ist gescheitert)
        public string ExeName = "";
        public string ParentName = "";
        // Eltern-, Großeltern- und Urgroßelternprozess (nächster zuerst), für die Zuordnung von Kindprozessen zu LM Studio / Ollama
        public IReadOnlyList<(int Pid, string Name)> Ancestors = Array.Empty<(int, string)>();
        public SecretValue? ApiKey;
    }

    private sealed class ProbeState
    {
        public ProbeVerdict Verdict;
        public BackendKind Kind;
        public LlamaProps? Props;
        public string? Version;
        public DateTime At;
        public int Attempts;
    }

    private const int MaxProbeAttempts = 4;

    private readonly IPlatform _platform;
    private readonly IReadOnlyList<IBackendProbe> _probes;
    private readonly HttpClient _http;
    private readonly int _selfPid;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<(int Pid, long Start), ProcEntry> _procs = new();
    private readonly Dictionary<(int Pid, long Start, int Port), ProbeState> _states = new();

    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(15);
    public int MaxParallelProbes { get; set; } = 6;
    // Anzahl gesendeter Proben seit dem Start (Tests und Diagnose)
    public int ProbesSent => _probesSent;
    private int _probesSent;

    public ServerDiscovery(IPlatform platform, IReadOnlyList<IBackendProbe>? probes = null, HttpClient? http = null,
        int? selfPid = null, Func<DateTime>? now = null)
    {
        _platform = platform;
        _probes = probes ?? BackendProbes.Default();
        _http = http ?? new HttpClient();
        _selfPid = selfPid ?? Environment.ProcessId;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public static bool IsLlamaName(string exeName) => exeName.StartsWith("llama-server", StringComparison.OrdinalIgnoreCase);

    // Ein Ollama-Runner (vom Ollama-Server gestarteter Kindprozess mit Zufallsport) ist selbst kein Server, den man anzeigt
    internal static bool IsOllamaRunner(ProcEntry e)
    {
        var exe = e.ExeName;
        bool parentOllama = e.ParentName.StartsWith("ollama", StringComparison.OrdinalIgnoreCase);
        if (exe.StartsWith("ollama_llama_server", StringComparison.OrdinalIgnoreCase)) return true;
        if (exe.StartsWith("ollama", StringComparison.OrdinalIgnoreCase))
            return parentOllama && CmdLine.Split(e.Details?.CommandLine).Skip(1).Any(t => t == "runner");
        return parentOllama;   // beliebig benannter Kindprozess von Ollama
    }

    public static bool IsLmStudioName(string n) =>
        n.StartsWith("LM Studio", StringComparison.OrdinalIgnoreCase) || n.StartsWith("LM-Studio", StringComparison.OrdinalIgnoreCase) ||
        n.StartsWith("lmstudio", StringComparison.OrdinalIgnoreCase) || n.Equals("lms", StringComparison.OrdinalIgnoreCase) ||
        n.Equals("llmster", StringComparison.OrdinalIgnoreCase);

    // Ein llama-server, den LM Studio oder Ollama selbst gestartet hat (Eltern-/Großelternprozess), ist kein eigener Server.
    // Gilt nur für Prozesse, die als llama.cpp erkannt wurden: der LM-Studio-Server selbst kann unter "lms" laufen.
    internal static BackendKind? ManagedBy(ProcEntry e)
    {
        if (e.Ancestors.Any(a => IsLmStudioName(a.Name))) return BackendKind.LmStudio;
        if (IsOllamaRunner(e) || e.Ancestors.Any(a => a.Name.StartsWith("ollama", StringComparison.OrdinalIgnoreCase))) return BackendKind.Ollama;
        return null;
    }

    // Prozesse, die auf keinen Fall per Probe erkannt werden: Ollama-Runner, "python -m ..." und Programme aus dem Windows-Ordner
    internal static bool IsExcludedFromProbe(ProcEntry e)
    {
        var exe = e.ExeName;
        if (IsOllamaRunner(e)) return true;
        // Der Stykker-Proxy einer anderen Monitor-Instanz (z. B. offenes Fenster, während die CLI läuft) ist kein eigener Server
        if (exe.StartsWith("StykkerLLM", StringComparison.OrdinalIgnoreCase) || exe.Equals("stykker", StringComparison.OrdinalIgnoreCase)) return true;
        // Die Tray-Anwendung "ollama app" hat einen eigenen Oberflächen-Port, der /api/version beantwortet, aber kein Server ist
        if (exe.StartsWith("ollama app", StringComparison.OrdinalIgnoreCase) || exe.StartsWith("ollama-app", StringComparison.OrdinalIgnoreCase)) return true;
        if (exe is "python" or "pythonw" or "python3" or "py" || exe.StartsWith("python3.", StringComparison.Ordinal))
        {
            var toks = CmdLine.Split(e.Details?.CommandLine);
            // „-m <Modul>" startet ein Python-Modul (Strata, vLLM) und wird übersprungen. „-m <Datei>" ist dagegen die
            // Modelldatei eines Servers, den der Monitor selbst gestartet hat – jeder mit „-m" startbare Profilaufruf
            // hat sie, und ohne diese Unterscheidung blieb jeder über ein Profil gestartete Python-Server unsichtbar
            // (live geprüft: der Prozess laeuft und antwortet, der Startvorgang bleibt trotzdem auf „starting").
            int i = 1;
            while (i < toks.Count - 1)
            {
                if (toks[i] != "-m") { i++; continue; }
                var wert = toks[i + 1];
                if (!LooksLikeModule(wert)) return false;          // Modelldatei: prüfen
                return !VllmApi.MentionsVllm(e.Details?.CommandLine);   // Modul: nur vLLM darf durch
            }
            return false;
        }
        var path = e.Details?.ImagePath;
        if (!string.IsNullOrEmpty(path))
        {
            // Ausnahme für den WSL-Port-Relay: ein Server in WSL2 oder Docker
            // ist auf der Windows-Seite nur über den Relay erreichbar, und der Relay ist ein Programm aus dem
            // Windows-Ordner – ohne diese Ausnahme blieb jeder solche Server unsichtbar (genau wie beim vLLM in WSL2).
            // Bewusst eng: nur die Relay-Namen kommen an die Probes, alle anderen Systemprogramme bleiben draußen.
            if (IsWslRelayName(exe)) return false;
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(win) && path.StartsWith(win + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Der Wert hinter „-m": ein Modulname wie „strata.server“ (Punkt, kein Pfad) oder ein Dateipfad wie
    // „C:\modelle\a.gguf“. Nur der Modulname bedeutet „Dienst, kein Modellserver“.
    private static bool LooksLikeModule(string value)
    {
        if (value.Length == 0) return true;
        if (value.Contains('/') || value.Contains('\\')) return false;                 // Pfad
        if (value.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) return false;  // Modelldatei
        if (value.Contains('.')) return true;                                          // strata.server, vllm.entrypoints…
        return true;                                                                   // einbuchstabiger Modulname
    }

    // Der Name des Relays hat sich mit den Windows-Versionen geändert (WSL2 zuerst wslrelay.exe, später wslhost/wslservice).
    // Namen, die nie einen Port halten, kosten nichts – sie stehen nur nie in der Liste der Listener.
    private static bool IsWslRelayName(string exe) =>
        exe.Equals("wslrelay", StringComparison.OrdinalIgnoreCase) ||
        exe.Equals("wslhost", StringComparison.OrdinalIgnoreCase) ||
        exe.Equals("wslservice", StringComparison.OrdinalIgnoreCase);

    private sealed record Candidate(int Pid, long Start, int Port, string Host, ProcEntry Proc, string By, LlamaProps? Props,
        BackendKind Kind = BackendKind.LlamaCpp, string? Version = null, IReadOnlyList<ManagedChild>? Children = null);

    // Erste Stufe für jeden lauschenden Prozess: Pfad, Name, Startzeit, Elternprozess (eingeschränktes Handle, kein fremder Speicher)
    private ProcEntry LoadBasic(int pid)
    {
        var d = _platform.ReadProcessBasic(pid);
        var exe = !string.IsNullOrEmpty(d?.ImagePath) ? ProcPath.Stem(d!.ImagePath) : _platform.ProcessName(pid) ?? "";
        string parent = "";
        if (d is { ParentPid: > 4 }) parent = _platform.ProcessName(d.ParentPid) ?? "";
        var anc = new List<(int, string)>();
        int pp = d?.ParentPid ?? 0;
        for (int depth = 0; depth < 3 && pp > 4; depth++)
        {
            var name = _platform.ProcessName(pp);
            if (name == null) break;   // Elternprozess beendet oder nicht lesbar: die Kette endet hier
            anc.Add((pp, name));
            pp = _platform.ReadProcessBasic(pp)?.ParentPid ?? 0;
        }
        return new ProcEntry { Details = d, ExeName = exe, ParentName = parent, Ancestors = anc };
    }

    // Zweite Stufe, nur nach Namensfilter oder positiver Probe: Kommandozeile, Arbeitsordner und Umgebung aus dem Prozessspeicher
    private void Deepen(ProcEntry e, int pid)
    {
        if (e.Deep) return;
        e.Deep = true;
        var raw = _platform.ReadProcess(pid);
        if (raw == null) return;
        e.ApiKey = ExtractApiKey(raw);   // nur im Speicher, für interne Anfragen
        e.Details = Sanitize(raw);
    }

    // Programme, deren Kommandozeile schon vor der Probe nötig ist: llama-server (Name genügt), python (-m ausschließen),
    // ollama (Runner erkennen). Alle anderen werden erst nach einer positiven Probe tiefer gelesen.
    internal static bool NeedsCommandLineFirst(string exeName) =>
        IsLlamaName(exeName) || exeName.StartsWith("ollama", StringComparison.OrdinalIgnoreCase) ||
        exeName is "python" or "pythonw" or "python3" or "py" || exeName.StartsWith("python3.", StringComparison.Ordinal);

    // API-Schlüssel aus --api-key, --api-key=, --api-key-file oder LLAMA_ARG_API_KEY. Bei mehreren Schlüsseln (Komma) der erste.
    internal static SecretValue? ExtractApiKey(ProcessDetails? d)
    {
        if (d == null) return null;
        string? key = null;
        var t = CmdLine.Split(d.CommandLine);
        for (int i = 1; i < t.Count && key == null; i++)
        {
            if (t[i] == "--api-key" && i + 1 < t.Count) key = t[i + 1];
            else if (t[i].StartsWith("--api-key=", StringComparison.Ordinal)) key = t[i]["--api-key=".Length..];
            else if (t[i] == "--api-key-file" && i + 1 < t.Count) key = FirstLine(t[i + 1], d.WorkingDir);
            else if (t[i].StartsWith("--api-key-file=", StringComparison.Ordinal)) key = FirstLine(t[i]["--api-key-file=".Length..], d.WorkingDir);
        }
        if (key == null && d.Environment.TryGetValue("LLAMA_ARG_API_KEY", out var ev)) key = ev;
        if (key != null) key = key.Split(',')[0].Trim();
        return string.IsNullOrEmpty(key) ? null : new SecretValue(key);
    }

    // Ein relativer Pfad gilt ab dem Arbeitsordner des Servers (nicht des Monitors)
    private static string? FirstLine(string path, string? workingDir)
    {
        try
        {
            if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(workingDir)) path = Path.Combine(workingDir, path);
            return File.ReadLines(path).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        }
        catch { return null; }
    }

    // Geheimnisse gar nicht erst im Zwischenspeicher halten: Kommandozeile und Umgebung werden sofort geschwärzt
    internal static ProcessDetails? Sanitize(ProcessDetails? d)
    {
        if (d == null) return null;
        string? cmd = d.CommandLine;
        if (!string.IsNullOrEmpty(cmd))
        {
            var tokens = CmdLine.Split(cmd);
            if (tokens.Count > 0)
            {
                var (red, _) = CmdLine.Redact(tokens.Skip(1).ToList());
                cmd = CmdLine.Join(new[] { tokens[0] }.Concat(red));
            }
        }
        var (env, _) = CmdLine.FilterEnvironment(d.Environment);
        return d with { CommandLine = cmd, Environment = env };
    }

    public async Task<DiscoveryResult> RunAsync(CancellationToken ct = default)
    {
        var listeners = _platform.ReadListeners();
        var conns = _platform.ReadConnections();
        var net = new NetSnapshot(listeners, conns);

        var groups = listeners.Where(l => l.Pid > 4 && l.Pid != _selfPid && l.Port > 0)
                              .GroupBy(l => (l.Pid, l.Port)).OrderBy(g => g.Key.Port).ThenBy(g => g.Key.Pid).ToList();

        // Prozesse lesen (neu nur für unbekannte PID + Startzeit) und Zwischenspeicher aufräumen
        var pidStart = new Dictionary<int, long>();
        foreach (var g in groups)
            if (!pidStart.ContainsKey(g.Key.Pid)) pidStart[g.Key.Pid] = _platform.ProcessStartTicks(g.Key.Pid) ?? 0;
        foreach (var k in _procs.Keys.Where(k => !pidStart.TryGetValue(k.Pid, out var s) || s != k.Start).ToList()) _procs.Remove(k);
        foreach (var k in _states.Keys.Where(k => !pidStart.TryGetValue(k.Pid, out var s) || s != k.Start).ToList()) _states.Remove(k);

        var found = new List<Candidate>();
        var toProbe = new List<Candidate>();
        foreach (var g in groups)
        {
            int pid = g.Key.Pid, port = g.Key.Port;
            long start = pidStart[pid];
            if (!_procs.TryGetValue((pid, start), out var proc)) _procs[(pid, start)] = proc = LoadBasic(pid);
            if (NeedsCommandLineFirst(proc.ExeName)) Deepen(proc, pid);
            string host = PickHost(g.Select(l => l.Address));

            if (IsLlamaName(proc.ExeName)) { found.Add(new Candidate(pid, start, port, host, proc, "name", null)); continue; }
            if (IsExcludedFromProbe(proc)) continue;

            var key = (pid, start, port);
            _states.TryGetValue(key, out var st);
            var cand = new Candidate(pid, start, port, host, proc, "probe", st?.Props, st?.Kind ?? BackendKind.LlamaCpp, st?.Version);
            if (st == null) toProbe.Add(cand);
            else if (st.Verdict == ProbeVerdict.Yes) found.Add(cand);
            else if (st.Verdict == ProbeVerdict.Retry && st.Attempts < MaxProbeAttempts && _now() - st.At >= RetryAfter) toProbe.Add(cand);
        }

        if (toProbe.Count > 0)
        {
            using var gate = new SemaphoreSlim(Math.Max(1, MaxParallelProbes));
            var results = await Task.WhenAll(toProbe.Select(async c =>
            {
                await gate.WaitAsync(ct);
                try { return (c, Outcome: await ProbeOne(NetAddr.Url(c.Host, c.Port), c.Proc, ct)); }
                finally { gate.Release(); }
            }));
            foreach (var (c, o) in results)
            {
                var key = (c.Pid, c.Start, c.Port);
                _states.TryGetValue(key, out var st);
                _states[key] = new ProbeState { Verdict = o.Verdict, Kind = o.Kind, Props = o.Props, Version = o.Version, At = _now(), Attempts = (st?.Attempts ?? 0) + 1 };
                if (o.Verdict == ProbeVerdict.Yes)
                {
                    Deepen(c.Proc, c.Pid);   // erst jetzt steht fest, dass es ein Modell-Server ist: Kommandozeile lesen
                    found.Add(c with { Props = o.Props, Kind = o.Kind, Version = o.Version });
                }
            }
        }

        found = AssignManagedChildren(found);
        var servers = found.Select(Build).OrderBy(s => s.Port).ThenBy(s => s.Host, StringComparer.Ordinal).ToList();
        return new DiscoveryResult(net, servers);
    }

    // Von LM Studio gestartete llama-server aus der Liste nehmen und der LM-Studio-Karte als Kinder zuordnen;
    // von Ollama gestartete verschwinden ganz (Ollama meldet seinen VRAM selbst)
    private static List<Candidate> AssignManagedChildren(List<Candidate> found)
    {
        var managed = found.Where(c => c.Kind == BackendKind.LlamaCpp && ManagedBy(c.Proc) != null).ToList();
        if (managed.Count == 0) return found;
        var rest = found.Except(managed).ToList();
        var owners = rest.Where(c => c.Kind == BackendKind.LmStudio).ToList();
        if (owners.Count == 0) return rest;
        // Besitzer: der LM-Studio-Server, der in der Prozesskette des Kindes steht, sonst der einzige LM-Studio-Server
        var byOwner = managed.Where(m => ManagedBy(m.Proc) == BackendKind.LmStudio)
            .GroupBy(m => owners.FirstOrDefault(o => m.Proc.Ancestors.Any(a => a.Pid == o.Pid)) ?? (owners.Count == 1 ? owners[0] : null))
            .Where(g => g.Key != null).ToDictionary(g => g.Key!, g => g.Select(ToChild).OrderBy(k => k.Port).ToList());
        for (int i = 0; i < rest.Count; i++)
            if (byOwner.TryGetValue(rest[i], out var kids)) rest[i] = rest[i] with { Children = kids };
        return rest;
    }

    private static ManagedChild ToChild(Candidate c)
    {
        var tokens = CmdLine.Split(c.Proc.Details?.CommandLine);
        var (red, _) = CmdLine.Redact(tokens.Skip(1).ToList());
        var model = tokens.Count > 1 ? LlamaServerArgs.Parse(red, new Dictionary<string, string>())?.Model : null;
        return new ManagedChild(c.Pid, c.Start, c.Host, c.Port, c.Proc.ApiKey, model);
    }

    private async Task<ProbeOutcome> ProbeOne(string baseUrl, ProcEntry proc, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);
        Interlocked.Increment(ref _probesSent);
        var target = new ProbeTarget(proc.ExeName, proc.ParentName, proc.Details?.CommandLine, proc.Details?.ImagePath);
        foreach (var p in _probes)
        {
            if (!p.AppliesTo(target)) continue;
            var o = await p.ProbeAsync(_http, baseUrl, cts.Token);
            if (o.Verdict != ProbeVerdict.No) return o;
        }
        return new ProbeOutcome(ProbeVerdict.No);
    }

    // Adresse, unter der der Listener erreichbar ist: bevorzugt IPv4 (0.0.0.0/127.x -> 127.0.0.1), dann IPv6
    internal static string PickHost(IEnumerable<string> addrs)
    {
        var list = addrs.ToList();
        var v4 = list.FirstOrDefault(a => !NetAddr.IsV6(a) && (NetAddr.IsAny(a) || NetAddr.IsLoopback(a)));
        if (v4 != null) return NetAddr.Reachable(v4);
        var v6 = list.FirstOrDefault(a => NetAddr.IsV6(a) && (NetAddr.IsAny(a) || NetAddr.IsLoopback(a)));
        if (v6 != null) return NetAddr.Reachable(v6);
        return list.Count > 0 ? list[0] : "127.0.0.1";
    }

    private static ServerInfo Build(Candidate c)
    {
        var d = c.Proc.Details;
        bool readable = !string.IsNullOrEmpty(d?.CommandLine);
        var tokens = readable ? CmdLine.Split(d!.CommandLine) : new List<string>();
        var args = tokens.Count > 1 ? tokens.Skip(1).ToList() : new List<string>();
        var (redArgs, hadArg) = CmdLine.Redact(args);
        var (env, hadEnv) = CmdLine.FilterEnvironment(d?.Environment ?? new Dictionary<string, string>());
        bool llama = c.Kind == BackendKind.LlamaCpp;
        var parsed = readable && llama ? LlamaServerArgs.Parse(redArgs, env) : null;
        string? program = !string.IsNullOrEmpty(d?.ImagePath) ? d!.ImagePath : tokens.Count > 0 ? tokens[0] : null;
        var mode = parsed?.Mode ?? (c.Props?.Role == "router" ? ServerMode.Router : ServerMode.Normal);
        return new ServerInfo
        {
            Key = NetAddr.Key(c.Host, c.Port), Host = c.Host, Port = c.Port, Pid = c.Pid, StartTicks = c.Start,
            ParentPid = d?.ParentPid ?? 0, DetectedBy = c.By, Program = program, WorkingDir = d?.WorkingDir,
            Args = redArgs, CommandLine = readable ? CmdLine.Join(new[] { program ?? "" }.Concat(redArgs)) : null,
            CommandLineReadable = readable, HasSecrets = hadArg || hadEnv, Env = env, Params = parsed, Mode = mode, Props = c.Props,
            ApiKey = c.Proc.ApiKey, Backend = c.Kind, BackendVersion = c.Version, Children = c.Children ?? Array.Empty<ManagedChild>(),
        };
    }
}
