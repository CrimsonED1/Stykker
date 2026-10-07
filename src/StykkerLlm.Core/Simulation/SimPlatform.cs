namespace StykkerLlm.Core;

// Die Plattform der Simulation: lauschende Ports, Prozesse, GPU- und Systemwerte kommen aus der SimWorld.
// Beenden wirkt nur auf die Simulation. GPU- und Systemwerte lassen sich im Simulator abschalten (dann "nicht verfügbar").
public sealed class SimPlatform : IPlatform
{
    private readonly SimWorld _w;
    private readonly DateTime _t0 = DateTime.Now;
    private readonly Random _noise = new(7);

    private const int ExplorerPid = 40001, ClientBase = 40100;
    private static readonly (string Exe, string Cmd)[] Clients =
    {
        (@"C:\sim\clients\opencode.exe", ""), (@"C:\sim\clients\aider.exe", ""), (@"C:\sim\clients\node.exe", ""),
    };

    public SimPlatform(SimWorld world) => _w = world;

    public int ProcessorCount => 16;
    public string? GpuDriver => _w.ShowGpu ? "sim-1.0" : null;

    private double Elapsed => (DateTime.Now - _t0).TotalSeconds;

    // ── Netz ──

    public IReadOnlyList<ListenerInfo> ReadListeners()
    {
        var list = new List<ListenerInfo>();
        foreach (var s in _w.Servers)
        {
            list.Add(new ListenerInfo("127.0.0.1", s.Port, s.Pid));
            if (s.Spec.Kind == BackendKind.LmStudio) list.Add(new ListenerInfo("127.0.0.1", s.EnginePort, s.EnginePid));
        }
        return list;
    }

    // Verbindungen simulierter Clients zu Servern, die gerade arbeiten oder vor Kurzem gearbeitet haben
    public IReadOnlyList<ConnectionInfo> ReadConnections()
    {
        var list = new List<ConnectionInfo>();
        int i = 0;
        foreach (var s in _w.Servers)
        {
            if (!s.HasEngine) { i++; continue; }
            bool recent = s.BusySlots > 0 || (DateTime.Now - s.LastActivity).TotalSeconds < 20;
            if (!recent) { i++; continue; }
            int port = s.Spec.Kind == BackendKind.LmStudio ? s.Port : s.Port;
            list.Add(new ConnectionInfo("127.0.0.1", 52000 + i, "127.0.0.1", port, ClientBase + i % Clients.Length));
            if (i % 2 == 0) list.Add(new ConnectionInfo("127.0.0.1", 53000 + i, "127.0.0.1", port, ClientBase + (i + 1) % Clients.Length));
            i++;
        }
        return list;
    }

    // ── Prozesse ──

    private SimWorld.Server? ByPid(int pid, out bool engine)
    {
        engine = false;
        foreach (var s in _w.Servers)
        {
            if (s.Pid == pid) return s;
            if (s.EnginePid == pid && pid != 0) { engine = true; return s; }
        }
        return null;
    }

    public long? ProcessStartTicks(int pid)
    {
        if (pid is ExplorerPid) return 500;
        if (pid >= ClientBase && pid < ClientBase + Clients.Length) return 600 + pid;
        var s = ByPid(pid, out bool eng);
        return s == null ? null : eng ? s.StartTicks + 1 : s.StartTicks;
    }

    public ProcessDetails? ReadProcessBasic(int pid) =>
        ReadProcess(pid) is { } d ? d with { CommandLine = null, WorkingDir = null, Environment = new Dictionary<string, string>() } : null;

    public ProcessDetails? ReadProcess(int pid)
    {
        if (pid == ExplorerPid) return new ProcessDetails(pid, 500, 4, @"C:\Windows\explorer.exe", null, null, new Dictionary<string, string>());
        if (pid >= ClientBase && pid < ClientBase + Clients.Length)
            return new ProcessDetails(pid, 600 + pid, ExplorerPid, Clients[pid - ClientBase].Exe, "\"" + Clients[pid - ClientBase].Exe + "\"", @"C:\sim", new Dictionary<string, string>());
        var s = ByPid(pid, out bool eng);
        if (s == null) return null;
        var spec = s.Spec;
        if (spec.Strata)
            return eng
                ? new ProcessDetails(pid, s.StartTicks + 1, s.Pid, @"C:\sim\strata\engine\strata.exe", "\"C:\\sim\\strata\\engine\\strata.exe\" --max-context " + spec.Context, @"C:\sim\strata", new Dictionary<string, string>())
                : new ProcessDetails(pid, s.StartTicks, ExplorerPid, @"C:\sim\strata\.venv\Scripts\python.exe",
                    "\"C:\\sim\\strata\\.venv\\Scripts\\python.exe\" \"C:\\sim\\strata\\serve\\server.py\" --engine strata --config \"C:\\sim\\strata\\strata-" + spec.Model + ".json\" --port " + s.Port,
                    @"C:\sim\strata", new Dictionary<string, string>());
        switch (spec.Kind)
        {
            case BackendKind.Ollama:
                return new ProcessDetails(pid, s.StartTicks, ExplorerPid, @"C:\sim\ollama\ollama.exe", "\"C:\\sim\\ollama\\ollama.exe\" serve", @"C:\sim\ollama", new Dictionary<string, string>());
            case BackendKind.Vllm:
                // Wie ein echter vLLM: ein Python-Prozess mit „-m vllm…“ – genau der Fall, für den die Erkennung
                // eine Ausnahme hat (ServerDiscovery.IsExcludedFromProbe)
                return new ProcessDetails(pid, s.StartTicks, ExplorerPid, @"C:\sim\vllm\.venv\Scripts\python.exe",
                    "\"C:\\sim\\vllm\\.venv\\Scripts\\python.exe\" -m vllm.entrypoints.openai.api_server --model " + spec.Model +
                    " --served-model-name " + spec.Model + " --port " + s.Port + " --max-model-len " + spec.Context,
                    @"C:\sim\vllm", new Dictionary<string, string>());
            case BackendKind.LmStudio when !eng:
                return new ProcessDetails(pid, s.StartTicks, ExplorerPid, @"C:\sim\LM Studio\LM Studio.exe", null, @"C:\sim\LM Studio", new Dictionary<string, string>());
            case BackendKind.LmStudio:
            {
                string cmd = "\"C:\\sim\\lmstudio\\llama-server.exe\" --model C:\\models\\" + spec.Model + ".gguf --host 127.0.0.1 --port " + s.EnginePort +
                             " --api-key " + s.ApiKey + " --ctx-size " + spec.Context + " --parallel " + spec.Slots;
                return new ProcessDetails(pid, s.StartTicks + 1, s.Pid, @"C:\sim\lmstudio\llama-server.exe", cmd, @"C:\sim\lmstudio", new Dictionary<string, string>());
            }
            default:
            {
                string cmd = "\"C:\\sim\\llama\\llama-server.exe\" -m C:\\models\\" + spec.Model + ".gguf --port " + s.Port + " --alias " + CmdLine.Quote(spec.Name.Length > 0 ? spec.Name : spec.Model) +
                             " -ngl 99 -c " + spec.Context + " -np " + spec.Slots + " --jinja" + (s.LogPath != null ? " --log-file " + CmdLine.Quote(s.LogPath) : "");
                return new ProcessDetails(pid, s.StartTicks, ExplorerPid, @"C:\sim\llama\llama-server.exe", cmd, @"C:\sim\llama",
                    new Dictionary<string, string> { ["LLAMA_ARG_CTX_SIZE"] = spec.Context.ToString() });
            }
        }
    }

    public string? ProcessName(int pid) => ReadProcess(pid) is { ImagePath: { } p } ? ProcPath.Stem(p) : null;

    public IReadOnlyList<(int Pid, string Name)> ChildProcesses(int pid)
    {
        var s = ByPid(pid, out bool eng);
        return s is { Spec.Strata: true } && !eng ? new[] { (s.EnginePid, "strata") } : Array.Empty<(int, string)>();
    }

    // Beenden wirkt nur auf die Simulation
    public StopOutcome Terminate(int pid, long expectedStartTicks, out string? error)
    {
        error = null;
        return _w.Terminate(pid, expectedStartTicks);
    }

    // ── Messwerte ──

    public ProcessUsage? ReadUsage(int pid)
    {
        var s = ByPid(pid, out bool eng);
        if (s == null) return null;
        bool mainOfLm = (s.Spec.Kind == BackendKind.LmStudio || s.Spec.Strata) && !eng;   // Programm bzw. Python-Server; die Arbeit macht die Engine
        double ws = mainOfLm ? 0.35 : 0.5 + s.Spec.ModelGb * 0.12;
        return new ProcessUsage(ws, ws * 1.1, TimeSpan.FromSeconds(mainOfLm ? Elapsed * 0.01 : s.CpuSeconds));
    }

    private sealed class Query : IGpuMemoryQuery
    {
        private readonly Func<(double, double)?> _read;
        public Query(int pid, Func<(double, double)?> read) { Pid = pid; _read = read; }
        public int Pid { get; }
        public (double DedicatedGb, double SharedGb)? Read() => _read();
        public void Dispose() { }
    }

    public IGpuMemoryQuery? OpenGpuMemory(int pid)
    {
        if (ByPid(pid, out bool eng) == null) return null;
        return new Query(pid, () =>
        {
            if (!_w.ShowGpu) return null;
            var s = ByPid(pid, out bool e2);
            if (s == null) return null;
            if (s.Spec.Kind == BackendKind.LmStudio && !e2) return (0.2, 0.0);
            if (s.Spec.Strata && !e2) return (0.0, 0.0);
            if (s.Spec.Kind == BackendKind.Ollama && !s.Loaded) return (0.0, 0.0);
            return (_w.ServerVramGb(s), 0.0);
        });
    }

    public GpuSample? ReadGpu()
    {
        if (!_w.ShowGpu) return null;
        double busy = _w.TotalLoad();
        double util = Math.Clamp(3 + busy * 88 + (_noise.NextDouble() - 0.5) * 6, 0, 100);
        double used = Math.Min(_w.GpuTotalGb, _w.TotalVramGb());
        double power = 38 + util * 2.5;
        return new GpuSample(_w.GpuName, util, used, _w.GpuTotalGb, power, 300, 36 + util * 0.42, util * 0.55, 600 + util * 22, 2900, 13800, 14000, 0);
    }

    public SystemSample? ReadSystem()
    {
        if (!_w.ShowSystem) return null;
        double busy = _w.TotalLoad();
        var cores = Enumerable.Range(0, 16).Select(k => Math.Clamp((k % 4 == 0 ? 30 : 4) + busy * (k < 8 ? 40 : 12) + (_noise.NextDouble() - 0.5) * 8, 0, 100)).ToArray();
        return new SystemSample(Math.Clamp(6 + busy * 24 + (_noise.NextDouble() - 0.5) * 5, 0, 100), 16, 21.5 + busy * 2.0, 64, 30.2 + busy * 2.0, 78, cores);
    }

    public MemoryInfo? ReadMemoryInfo() => _w.ShowSystem ? new MemoryInfo("DDR5", 6000, 2, 64, 2) : null;

    public IReadOnlyList<(string Name, double Gb)>? ReadGpuTop(int count)
    {
        if (!_w.ShowGpu) return null;
        var list = _w.Servers.Where(s => s.HasEngine || s.Loaded)
            .Select(s => ((s.Spec.Kind == BackendKind.Ollama ? "ollama" : s.Spec.Strata ? "strata" : "llama-server") + $" ({(s.Spec.Kind == BackendKind.LmStudio || s.Spec.Strata ? s.EnginePid : s.Pid)})", _w.ServerVramGb(s)))
            .ToList();
        list.Add(("dwm (900)", 0.4));
        return list.OrderByDescending(x => x.Item2).Take(count).ToList();
    }

    public IReadOnlyList<(string Name, double Gb)>? ReadRamTop(int count)
    {
        if (!_w.ShowSystem) return null;
        var list = _w.Servers.Select(s => (s.Spec.Kind == BackendKind.Ollama ? "ollama" : s.Spec.Strata ? "strata" : s.Spec.Kind == BackendKind.LmStudio ? "LM Studio" : "llama-server",
            0.5 + s.Spec.ModelGb * (s.Spec.Strata ? 0.9 : 0.12))).ToList();
        list.AddRange(new[] { ("msedge", 2.1), ("explorer", 0.4), ("Code", 1.6), ("svchost", 1.4), ("MsMpEng", 0.3) });
        return list.GroupBy(x => x.Item1).Select(gr => (gr.Key, gr.Sum(x => x.Item2))).OrderByDescending(x => x.Item2).Take(count).ToList();
    }

    public void Dispose() { }
}
