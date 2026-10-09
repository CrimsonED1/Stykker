using System.Diagnostics;
using System.Text.Json;

namespace StykkerLlm.Core.Eval;

// Ein Modell für die Test-Warteschlange: wie der Server gestartet wird (Programm, Argumente, Port)
public sealed class EvalModelDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Exe { get; set; } = "";
    public string Args { get; set; } = "";
    public int Port { get; set; }
    public bool Enabled { get; set; } = true;
    public string Source { get; set; } = "manual";   // profile | gguf | manual
    public string ModelFile { get; set; } = "";
    public double SizeGb { get; set; }
}

public sealed class EvalQueueSettings
{
    public string LlamaServer { get; set; } = "llama-server";   // ohne Pfad: aus PATH; eigener Pfad in den Einstellungen
    public bool AllowCode { get; set; }
    public List<string> ModelRoots { get; set; } = new() { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "models") };
}

public enum EvalJobState { Queued, Starting, Running, Done, Failed, Skipped, Cancelled }

// Ein Auftrag: ein Modell × eine Suite (× Wiederholungen)
public sealed class EvalJob
{
    public Guid Id { get; } = Guid.NewGuid();
    public required EvalModelDef Model { get; init; }
    public required string Suite { get; init; }
    public EvalSuite? Inline { get; init; }            // 
    public int Repeat { get; init; } = 1;
    public EvalJobState State { get; internal set; } = EvalJobState.Queued;
    public string Note { get; internal set; } = "";
    public int Done { get; internal set; }
    public int Total { get; internal set; }
    public string Current { get; internal set; } = "";
    public List<EvalTaskResult> Live { get; } = new();
    public List<EvalRun> Runs { get; } = new();
    public DateTime? Started { get; internal set; }
    public DateTime? Finished { get; internal set; }
    public double Score => Runs.Count > 0 ? Runs.Average(r => r.Total) : Live.Count(t => !t.Skipped && !t.Error) is int n and > 0
        ? 100.0 * Live.Where(t => !t.Skipped && !t.Error).Sum(t => t.Score) / n : 0;
}

// Test-Warteschlange: Modelle verwalten (eval-models.json im Datenordner), Aufträge nacheinander abarbeiten.
// Je Modell wird der Server einmal gestartet, alle seine Suiten laufen, dann wird er beendet (nur der selbst gestartete Prozess).
// Ereignis Changed meldet jede Änderung (für Oberflächen: Web, Fenster, TUI); Aufrufe sind thread-sicher.
public sealed class EvalQueue : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private readonly List<EvalJob> _jobs = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private PythonRunner? _python;
    private bool _pythonSearched;

    public event Action? Changed;
    public List<EvalModelDef> Models { get; private set; } = new();
    public EvalQueueSettings Settings { get; private set; } = new();
    public bool AllowCode { get => Settings.AllowCode; set { Settings.AllowCode = value; SaveSettings(); } }
    public bool Running => _worker is { IsCompleted: false };
    public string ModelsFile => Path.Combine(_paths.Root, "eval-models.json");
    public string SettingsFile => Path.Combine(_paths.Root, "eval-settings.json");

    public EvalQueue(AppPaths paths)
    {
        _paths = paths;
        try { if (File.Exists(ModelsFile)) Models = JsonSerializer.Deserialize<List<EvalModelDef>>(File.ReadAllText(ModelsFile), Json) ?? new(); } catch { }
        try { if (File.Exists(SettingsFile)) Settings = JsonSerializer.Deserialize<EvalQueueSettings>(File.ReadAllText(SettingsFile), Json) ?? new(); } catch { }
    }

    public IReadOnlyList<EvalJob> Jobs { get { lock (_gate) return _jobs.ToList(); } }

    public void SaveModels()
    {
        try { Directory.CreateDirectory(_paths.Root); File.WriteAllText(ModelsFile, JsonSerializer.Serialize(Models, Json)); } catch { }
        Changed?.Invoke();
    }

    public void SaveSettings()
    {
        try { Directory.CreateDirectory(_paths.Root); File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Settings, Json)); } catch { }
        Changed?.Invoke();
    }

    public void EnqueueInline(EvalModelDef model, EvalSuite suite)
    {
        lock (_gate) _jobs.Add(new EvalJob { Model = model, Suite = suite.Name, Inline = suite });
        Changed?.Invoke();
    }

    public PythonRunner? Python
    {
        get
        {
            if (!_pythonSearched) { _pythonSearched = true; _python = PythonRunner.Find(); }
            return _python;
        }
    }

    // Modelle aus gespeicherten Profilen (llama-server) und gefundenen GGUF-Dateien vorschlagen; vorhandene bleiben unverändert
    public int Discover(Library library, IEnumerable<string> ggufRoots, string llamaServer)
    {
        int added = 0, port = 8110;
        var known = new HashSet<string>(Models.Select(m => m.ModelFile), StringComparer.OrdinalIgnoreCase);
        foreach (var p in library.Profiles)
        {
            var a = LlamaServerArgs.Parse(p.Args);
            if (a.Model == null || known.Contains(a.Model)) continue;
            var args = string.Join(' ', p.Args.Select(CmdLine.Quote));
            if (args.Contains("***")) continue;   // Geheimnisse: nur von Hand
            Models.Add(new EvalModelDef { Name = p.Name, Exe = p.Program, Args = args, Port = p.Port is > 0 ? p.Port.Value : port++, Source = "profile", ModelFile = a.Model, SizeGb = Size(a.Model) });
            known.Add(a.Model); added++;
        }
        foreach (var root in ggufRoots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.gguf", SearchOption.AllDirectories).ToList(); } catch { continue; }
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (known.Contains(f) || name.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase) || name.Contains("-0000", StringComparison.Ordinal) && !name.Contains("-00001-", StringComparison.Ordinal)) continue;
                if (new FileInfo(f).Length < 300L * 1024 * 1024 || f.Contains(@"\mtp\", StringComparison.OrdinalIgnoreCase)) continue;
                int p = port++;
                Models.Add(new EvalModelDef
                {
                    Name = Path.GetFileNameWithoutExtension(name).ToLowerInvariant(), Exe = llamaServer, Port = p, Source = "gguf", ModelFile = f, SizeGb = Size(f), Enabled = false,
                    Args = $"-m {CmdLine.Quote(f)} --host 127.0.0.1 --port {p} --fit on -c 65536 -fa on --jinja",
                });
                known.Add(f); added++;
            }
        }
        if (added > 0) SaveModels();
        return added;
    }

    private static double Size(string f) { try { return new FileInfo(f).Length / 1073741824.0; } catch { return 0; } }

    public void Enqueue(IEnumerable<EvalModelDef> models, IEnumerable<string> suites, int repeat = 1)
    {
        lock (_gate)
            foreach (var m in models)
                foreach (var s in suites)
                    _jobs.Add(new EvalJob { Model = m, Suite = s, Repeat = Math.Clamp(repeat, 1, 10) });
        Changed?.Invoke();
    }

    public void Remove(Guid id) { lock (_gate) _jobs.RemoveAll(j => j.Id == id && j.State is EvalJobState.Queued or EvalJobState.Done or EvalJobState.Failed or EvalJobState.Skipped or EvalJobState.Cancelled); Changed?.Invoke(); }
    public void ClearFinished() { lock (_gate) _jobs.RemoveAll(j => j.State is EvalJobState.Done or EvalJobState.Failed or EvalJobState.Skipped or EvalJobState.Cancelled); Changed?.Invoke(); }

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _worker = Task.Run(() => WorkAsync(ct));
        Changed?.Invoke();
    }

    public void Stop() { _cts?.Cancel(); Changed?.Invoke(); }

    private EvalJob? Next() { lock (_gate) return _jobs.FirstOrDefault(j => j.State == EvalJobState.Queued); }

    private async Task WorkAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        Process? server = null; EvalModelDef? serverFor = null; int ctxLimit = 0;
        try
        {
            while (!ct.IsCancellationRequested && Next() is { } job)
            {
                // Server wechseln, wenn der Auftrag ein anderes Modell braucht
                if (serverFor?.Id != job.Model.Id)
                {
                    StopServer(ref server); serverFor = null;
                    job.State = EvalJobState.Starting; job.Note = "starting server …"; Changed?.Invoke();
                    (server, ctxLimit, var why) = await StartServerAsync(http, job.Model, ct).ConfigureAwait(false);
                    if (server == null)
                    {
                        lock (_gate) foreach (var j in _jobs.Where(j => j.Model.Id == job.Model.Id && j.State == EvalJobState.Queued)) { j.State = EvalJobState.Skipped; j.Note = why; }
                        Changed?.Invoke();
                        continue;
                    }
                    serverFor = job.Model;
                }
                await RunJobAsync(http, job, ctxLimit, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            StopServer(ref server);
            lock (_gate) foreach (var j in _jobs.Where(j => j.State is EvalJobState.Running or EvalJobState.Starting)) { j.State = EvalJobState.Cancelled; j.Note = "stopped"; }
            Changed?.Invoke();
        }
    }

    private async Task RunJobAsync(HttpClient http, EvalJob job, int ctxLimit, CancellationToken ct)
    {
        EvalSuite suite;
        try { suite = job.Inline ?? EvalSuites.All(_paths.EvalSuitesDir).First(s => s.Name.Equals(job.Suite, StringComparison.OrdinalIgnoreCase)); }
        catch { job.State = EvalJobState.Failed; job.Note = $"suite '{job.Suite}' not found"; Changed?.Invoke(); return; }
        job.State = EvalJobState.Running; job.Started = DateTime.Now; job.Note = ""; job.Total = suite.Tasks.Count * job.Repeat; job.Done = 0;
        Changed?.Invoke();
        var group = Guid.NewGuid();
        int baseSeed = suite.Seed;
        for (int rep = 1; rep <= job.Repeat && !ct.IsCancellationRequested; rep++)
        {
            suite.Seed = baseSeed + (rep - 1) * 1000;
            var run = await EvalRunner.RunAsync(http, $"http://127.0.0.1:{job.Model.Port}", suite, new EvalOptions
            {
                Python = AllowCode ? Python : null, PythonChecks = Python, ContextLimit = ctxLimit,
                Progress = (t, r) =>
                {
                    if (r == null) job.Current = $"{t.Category} · {t.Title}";
                    else { lock (job.Live) job.Live.Add(r); job.Done++; job.Current = ""; }
                    Changed?.Invoke();
                },
            }, ct).ConfigureAwait(false);
            run.Server = job.Model.Name; run.Model = job.Model.Name; run.ModelFile = Path.GetFileName(job.Model.ModelFile);
            run.Settings = job.Model.Args.Length > 120 ? job.Model.Args[..120] : job.Model.Args;
            run.Repeat = rep; run.Group = group;
            if (run.Tasks.Count > 0 && job.Inline == null) EvalSuites.Save(_paths.EvalResultsDir, run);   // Probeläufe nicht in die Rangliste
            job.Runs.Add(run);
            if (run.Cancelled) break;
        }
        job.Finished = DateTime.Now;
        job.State = ct.IsCancellationRequested ? EvalJobState.Cancelled : EvalJobState.Done;
        Changed?.Invoke();
    }

    // Startet den Server des Modells und wartet auf /health; liefert Kontext je Slot (aus /props) oder einen Grund
    private static async Task<(Process? Proc, int Ctx, string Why)> StartServerAsync(HttpClient http, EvalModelDef m, CancellationToken ct)
    {
        if (!File.Exists(m.Exe)) return (null, 0, "server program not found: " + m.Exe);
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            var c = probe.ConnectAsync("127.0.0.1", m.Port);
            if (await Task.WhenAny(c, Task.Delay(300, ct)).ConfigureAwait(false) == c && probe.Connected) return (null, 0, $"port {m.Port} is already in use");
        }
        catch { }
        Process? p;
        try { p = Process.Start(new ProcessStartInfo(m.Exe, m.Args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(m.Exe) ?? "" }); }
        catch (Exception ex) { return (null, 0, "start failed: " + ex.Message); }
        if (p == null) return (null, 0, "start failed");
        var until = DateTime.Now.AddMinutes(8);
        while (DateTime.Now < until)
        {
            await Task.Delay(1500, ct).ConfigureAwait(false);
            if (p.HasExited) return (null, 0, $"server exited (code {p.ExitCode}) – not enough memory or unsupported model?");
            try
            {
                var h = await http.GetStringAsync($"http://127.0.0.1:{m.Port}/health", ct).ConfigureAwait(false);
                if (!h.Contains("\"ok\"", StringComparison.Ordinal)) continue;
                int ctx = 0;
                try { ctx = LlamaProps.Parse(await http.GetStringAsync($"http://127.0.0.1:{m.Port}/props", ct).ConfigureAwait(false))?.NCtx ?? 0; } catch { }
                return (p, ctx, "");
            }
            catch (HttpRequestException) { }
        }
        try { p.Kill(true); } catch { }
        return (null, 0, "server did not become ready within 8 min");
    }

    private static void StopServer(ref Process? p)
    {
        if (p == null) return;
        try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(5000); } } catch { }
        p.Dispose(); p = null;
    }

    public void Dispose() { _cts?.Cancel(); try { _worker?.Wait(8000); } catch { } }
}
