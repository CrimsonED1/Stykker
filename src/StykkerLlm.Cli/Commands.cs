using StykkerLlm.Core;

namespace StykkerLlm.Cli;

// Die Einzelbefehle. Rückgabe = Exit-Code (siehe Program.Help).
public static class Commands
{
    public const int Ok = 0, Error = 1, Usage = 2, NotFound = 3, Cancelled = 4;

    // ── status ──

    public static async Task<int> StatusAsync(CliArgs a, CancellationToken ct)
    {
        using var s = new Session(a, write: false);
        if (s.Simulated) await Task.Delay(1500, ct).ConfigureAwait(false);   // der Simulator braucht einen Moment für erste Anfragen
        await s.TickAsync(2, ct).ConfigureAwait(false);
        if (a.WatchSeconds is not int every)
        {
            PrintStatus(s, a);
            return Ok;
        }
        bool clear = !a.Json && !Console.IsOutputRedirected;
        while (!ct.IsCancellationRequested)
        {
            if (clear) Console.Write("\u001b[H\u001b[2J\u001b[3J");
            PrintStatus(s, a);
            // weiter im Messtakt (für Token/s), ausgegeben wird alle N Sekunden
            try { await s.TickAsync(Math.Max(1, every * 1000 / a.IntervalMs), ct, pauseFirst: true).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        return Ok;
    }

    internal static void PrintStatus(Session s, CliArgs a)
    {
        // Läuft der Server, ist er die Quelle: seinen Zustand zeigen, nicht den der mitlesenden Engine
        if (s.UsesServer) { RemoteCommands.PrintStatus(s, a); return; }
        var e = s.Engine;
        if (a.Json)
        {
            // --watch: eine Zeile je Messung (NDJSON), sonst lesbar eingerückt. Über Console.Out (in der Oberfläche: ins Protokoll).
            using var ms = new MemoryStream();
            StatusJson.Write(ms, e, s.Limited, indented: a.WatchSeconds == null, DateTimeOffset.Now);
            Console.Out.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
            Console.Out.Flush();
            return;
        }
        var o = Console.Out;
        if (s.Simulated) o.WriteLine(Out.Cyan("SIMULATION") + Out.Dim(" – simulated servers, nothing real is touched"));
        if (s.Limited) o.WriteLine(Out.Yellow("limited") + Out.Dim(" – no process/GPU access on this platform yet; only servers added by URL are shown"));
        if (e.Gpu is { } g)
            o.WriteLine($"{Out.Bold("GPU")}  {g.Name}   {g.Util:0} %   VRAM {g.MemUsedGb:0.0} / {g.MemTotalGb:0.0} GB   {g.TempC:0} °C   {g.PowerW:0} W" +
                (g.Throttled ? "   " + Out.Yellow("throttled: " + g.ThrottleText()) : ""));
        if (e.Sys is { } sy)
            o.WriteLine($"{Out.Bold("SYS")}  CPU {sy.CpuPercent:0} %   RAM {sy.RamUsedGb:0.0} / {sy.RamTotalGb:0.0} GB");
        if (e.Gpu != null || e.Sys != null) o.WriteLine();

        var servers = e.Servers;
        if (servers.Count == 0) { o.WriteLine(Out.Dim(Strings.RunningEmpty)); return; }
        Out.Table(new[] { "", Strings.ColServer, Strings.ColBackend, Strings.ColUrl, Strings.ColModel, Strings.ColStatus, Strings.ColTps, Strings.GpuVram, Strings.ColSlot },
            servers.Select(sv => (IReadOnlyList<string>)new[]
            {
                sv.Online ? Out.Green(Out.Dot(true)) : Out.Dim(Out.Dot(false)),
                Out.Cut(sv.Name, 28),
                BackendName(sv.Kind),
                sv.Url,
                Out.Cut(ModelText(sv), 32),
                StateText(sv),
                sv.Online ? $"{sv.Current:0.0}" : "–",
                Out.Gb(sv.VramGb),
                SlotsText(sv),
            }), o);
    }

    public static string BackendName(BackendKind k) => k switch
    {
        BackendKind.Ollama => "ollama",
        BackendKind.LmStudio => "lmstudio",
        BackendKind.Vllm => "vllm",
        _ => "llama.cpp",
    };

    // Zustand als festes Wort (auch für JSON): offline, loading, sleeping, idle, busy
    public static string State(ServerWatcher s) =>
        !s.Online ? (s.Loading ? "loading" : "offline")
        : s.Sleeping ? "sleeping"
        : s.Slots.Any(x => x.Busy) || s.Current > 0.05 ? "busy" : "idle";

    private static string StateText(ServerWatcher s)
    {
        var text = State(s) switch
        {
            "busy" => Out.Green("busy"),
            "loading" => Out.Yellow("loading"),
            "offline" => Out.Red("offline"),
            var x => Out.Dim(x),
        };
        // Speicher im Shared Memory (RAM, langsam) deutlich machen
        if (ServerWatcher.SpillGb(s.Models, s.SharedGb) is double spill) text += " " + Out.Yellow(Strings.ChipSharedRam + " " + Out.Gb(spill));
        return text;
    }

    private static string ModelText(ServerWatcher s) =>
        s.Models.Count > 0
            ? string.Join(", ", s.Models.Select(m => m.Name + (Strings.Until(m.ExpiresAt) is { Length: > 0 } u ? " (" + u + ")" : "")))
            : s.Model is { Length: > 0 } m && m != "–" ? m : "–";

    private static string SlotsText(ServerWatcher s)
    {
        var text = s.Slots.Count == 0 ? "–" : $"{s.Slots.Count(x => x.Busy)}/{s.Slots.Count}";
        if (s.QueueCount is int q && q > 0) text += " " + Out.Yellow("+" + q);   // wartende Anfragen (llama-server /metrics)
        return text;
    }

    // ── list ──

    public static async Task<int> ListAsync(CliArgs a)
    {
        using var s = new Session(a, write: false);
        return await ListCore(a, s).ConfigureAwait(false);
    }

    internal static async Task<int> ListCore(CliArgs a, Session s)
    {
        if (s.UsesServer) return RemoteCommands.List(s, a);
        var what = a.Words.FirstOrDefault()?.ToLowerInvariant() ?? "saved";
        var lib = s.Engine.Library;
        var o = Console.Out;
        switch (what)
        {
            case "saved" or "profiles":
                if (a.Json) { WriteJson(lib.Profiles.Select(p => new { p.Id, p.Name, p.Program, p.Port, model = p.ModelPath, p.Note, p.LastStarted })); break; }
                if (lib.Profiles.Count == 0) { o.WriteLine(Out.Dim(Strings.SavedEmpty)); break; }
                Out.Table(new[] { Strings.ColName, Strings.ColModel, Strings.ColPort, Strings.ColLastStarted }, lib.Profiles.Select(p => (IReadOnlyList<string>)new[]
                {
                    Out.Cut(p.Name, 32), Out.Cut(p.ModelPath != null ? ServerInfo.ModelName(p.ModelPath) : "–", 40),
                    p.Port?.ToString() ?? "–", p.LastStarted?.ToString("yyyy-MM-dd HH:mm") ?? Strings.NeverRun,
                }), o);
                break;
            case "history":
                var q = string.Join(' ', a.Words.Skip(1));   // "list history <text>" ist die Suche (wie das Suchfeld im Fenster)
                var hist = lib.History.Where(h => Library.Matches(h, q)).ToList();
                if (a.Json) { WriteJson(hist.Select(h => new { h.Key, h.Name, h.Program, h.Port, model = h.ModelPath, h.LastSeen, h.Runs, h.BestTps, meanTps = h.MeanTps, h.MaxVramGb })); break; }
                if (hist.Count == 0) { o.WriteLine(Out.Dim(q.Length > 0 ? Strings.HistoryNoneMatch(q) : Strings.HistoryEmpty)); break; }
                Out.Table(new[] { Strings.ColId, Strings.ColName, Strings.ColPort, Strings.ColLastSeen, Strings.ColRuns, Strings.ColBest, Strings.GpuVram }, hist.Select(h => (IReadOnlyList<string>)new[]
                {
                    h.Key[..8], Out.Cut(h.Name.Length > 0 ? h.Name : h.ModelPath != null ? ServerInfo.ModelName(h.ModelPath) : "–", 32),
                    h.Port?.ToString() ?? "–", h.LastSeen.ToString("yyyy-MM-dd HH:mm"), h.Runs.ToString(), $"{h.BestTps:0.0}",
                    h.MaxVramGb > 0 ? $"{h.MaxVramGb:0.0} GB" : "–",
                }), o);
                break;
            case "bench" or "benchmarks":
                if (a.Json) { WriteJson(lib.Benchmarks.Select(b => new { b.Id, b.Started, b.Title, b.Model, b.Quant, b.Gpu, b.Settings, genTps = BestGen(b), b.Cancelled })); break; }
                if (lib.Benchmarks.Count == 0) { o.WriteLine(Out.Dim(Strings.BenchNoneYet)); break; }
                Out.Table(new[] { Strings.ColStarted, Strings.ColName, Strings.ColModel, Strings.ColQuant, Strings.BenchSeriesGen, Strings.ColSettings }, lib.Benchmarks.Select(b => (IReadOnlyList<string>)new[]
                {
                    b.Started.ToString("yyyy-MM-dd HH:mm"), Out.Cut(b.Title, 28), Out.Cut(b.Model, 32), b.Quant,
                    $"{BestGen(b):0.0}", Out.Cut(b.Settings, 40),
                }), o);
                break;
            case "recordings":
                var recs = await RecordingStore.ListAsync(s.Engine.Paths.RecordingsDir).ConfigureAwait(false);
                if (a.Json) { WriteJson(recs.Select(r => new { r.Id, r.Started, r.DurationSec, r.Target, r.Model, r.Requests, r.TokensIn, r.TokensOut, r.AvgTps, r.PeakTps, r.TtftP50, r.TtftP95, r.EnergyWh, r.File })); break; }
                if (recs.Count == 0) { o.WriteLine(Out.Dim(Strings.RecordingNoneYet)); break; }
                Out.Table(new[] { Strings.ColStarted, Strings.ColDuration, Strings.ColTarget, Strings.ColModel, Strings.ColRequests, Strings.ColAvg, Strings.ColPeak }, recs.Select(r => (IReadOnlyList<string>)new[]
                {
                    r.Started.ToString("yyyy-MM-dd HH:mm"), Fmt.Dur(r.DurationSec), Out.Cut(r.Target, 24), Out.Cut(r.Model, 32),
                    r.Requests.ToString(), $"{r.AvgTps:0.0}", $"{r.PeakTps:0.0}",
                }), o);
                break;
            default:
                Out.Error($"list what? saved, history, bench or recordings (not '{what}')");
                return Usage;
        }
        return Ok;
    }

    // ── show ──

    // Ein Verlaufseintrag (Id-Anfang wie in "list history" oder Name) bzw. ein gemerktes Profil: Kommandozeile mit geschwärzten
    // Geheimnissen, erkannte Parameter, Umgebung (Whitelist) und Laufstatistik. Nur gespeicherte Werte, nichts wird gestartet.
    public static int Show(CliArgs a)
    {
        if (a.Words.Count == 0) { Out.Error("show what? a history id or name (see 'list history') or a saved profile name"); return Usage; }
        using var s = new Session(a, write: false);
        if (s.UsesServer) return RemoteCommands.Show(s, a);
        return ShowCore(a, s);
    }

    internal static int ShowCore(CliArgs a, Session s)
    {
        var query = string.Join(' ', a.Words);
        if (query.Length == 0) { Out.Error("show what? a history id or name (see 'list history') or a saved profile name"); return Usage; }
        var lib = s.Engine.Library;
        var hist = lib.History.FirstOrDefault(h => h.Key.StartsWith(query, StringComparison.OrdinalIgnoreCase));
        var ambH = new List<HistoryEntry>();
        if (hist == null) hist = Find(lib.History, h => h.Name, query, out ambH);
        Profile? prof = null;
        if (hist == null && ambH.Count == 0)
        {
            prof = Find(lib.Profiles, p => p.Name, query, out var ambP);
            if (ambP.Count > 0) return Ambiguous(query, ambP.Select(p => p.Name));
        }
        if (ambH.Count > 0) return Ambiguous(query, ambH.Select(h => $"{h.Key[..8]}  {h.Name}"));
        if (hist == null && prof == null) { Out.Error($"no history entry or saved profile matches '{query}'"); return NotFound; }

        var (name, program, args, env, workDir, model) = hist != null
            ? (hist.Name, hist.Program, (IReadOnlyList<string>)hist.Args, (IReadOnlyDictionary<string, string>)hist.Env, hist.WorkingDir, hist.ModelPath)
            : (prof!.Name, prof.Program, prof.Args, prof.Env, prof.WorkingDir, prof.ModelPath);
        var command = CmdLine.Join(new[] { program }.Concat(args));
        var parsed = LlamaServerArgs.Parse(args, env);

        if (a.Json)
        {
            var obj = new Dictionary<string, object?>
            {
                ["kind"] = hist != null ? "history" : "profile", ["id"] = hist?.Key ?? prof!.Id.ToString(), ["name"] = name,
                ["program"] = program, ["args"] = args, ["commandLine"] = command, ["workingDir"] = workDir, ["model"] = model,
                ["env"] = env, ["parameters"] = parsed.Entries.Select(e => new { name = e.Name, value = e.Value }).ToList(),
            };
            if (hist != null)
            {
                obj["firstSeen"] = hist.FirstSeen; obj["lastSeen"] = hist.LastSeen; obj["runs"] = hist.Runs; obj["totalSeconds"] = hist.TotalSeconds;
                obj["bestTps"] = hist.BestTps; obj["meanTps"] = hist.MeanTps; obj["maxVramGb"] = hist.MaxVramGb; obj["ctx"] = hist.Ctx; obj["port"] = hist.Port;
            }
            else { obj["note"] = prof!.Note; obj["lastStarted"] = prof.LastStarted; }
            WriteJsonValue(obj);
            return Ok;
        }

        var o = Console.Out;
        o.WriteLine(Out.Bold(name) + Out.Dim(hist != null ? $"   history {hist.Key[..8]}" : "   saved profile"));
        o.WriteLine();
        o.WriteLine(Out.Dim(Strings.SecCommand));
        o.WriteLine("  " + command);
        o.WriteLine();
        var rows = new List<IReadOnlyList<string>>();
        void Row(string k, string? v) { if (!string.IsNullOrEmpty(v)) rows.Add(new[] { k, v }); }
        Row(Strings.WorkingFolder, workDir);
        Row(Strings.ColModelFile, model);
        if (hist != null)
        {
            Row(Strings.ColPort, hist.Port?.ToString());
            Row(Strings.ColCtx, hist.Ctx?.ToString());
            Row(Strings.RowFirstSeen, hist.FirstSeen.ToString("yyyy-MM-dd HH:mm"));
            Row(Strings.ColLastSeen, hist.LastSeen.ToString("yyyy-MM-dd HH:mm"));
            Row(Strings.ColRuns, hist.Runs.ToString());
            Row(Strings.RowRunningTime, Fmt.Dur(hist.TotalSeconds));
            Row(Strings.RowTpsBest, hist.BestTps > 0 ? $"{hist.BestTps:0.0}" : null);
            Row(Strings.RowTpsAverage, hist.MeanTps > 0 ? $"{hist.MeanTps:0.0}" : null);
            Row(Strings.RowGpuMax, hist.MaxVramGb > 0 ? $"{hist.MaxVramGb:0.0} GB" : null);
        }
        else
        {
            Row(Strings.FieldNote, prof!.Note);
            Row(Strings.ColLastStarted, prof.LastStarted?.ToString("yyyy-MM-dd HH:mm") ?? Strings.NeverRun);
        }
        Out.Table(new[] { Strings.ColProperty, Strings.ColValue }, rows, o);
        o.WriteLine();
        var pars = parsed.Entries.Select(e => (IReadOnlyList<string>)new[] { e.Name.Length > 0 ? e.Name : Strings.RowArgument, e.Value ?? "" })
            .Concat(env.Select(kv => (IReadOnlyList<string>)new[] { Strings.EnvRow(kv.Key), kv.Value })).ToList();
        if (pars.Count == 0) o.WriteLine(Out.Dim(Strings.NoParameters));
        else Out.Table(new[] { Strings.ColOption, Strings.ColValue }, pars, o);
        return Ok;
    }

    private static double BestGen(BenchResult b) => b.Steps.Where(x => x.Ok).Select(x => x.GenTps).DefaultIfEmpty(0).Max();

    private static void WriteJson<T>(IEnumerable<T> items) => WriteJsonValue(items.ToList());

    private static void WriteJsonValue(object value)
    {
        // Listen und show sind nicht Teil des festen status-Schemas; anonyme Typen genügen (Reflection, kein AOT nötig)
        var json = System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        });
        Console.Out.WriteLine(json);
    }

    // ── start ──

    public static async Task<int> StartAsync(CliArgs a, CancellationToken ct)
    {
        var query = string.Join(' ', a.Words);
        if (query.Length == 0) { Out.Error("start what? a saved profile name or a history id (see 'list saved' / 'list history')"); return Usage; }
        using var s = new Session(a, write: true);
        await s.TickAsync(1, ct).ConfigureAwait(false);   // laufende Server und GPU kennen (Prüfungen: Port belegt, VRAM)
        return await StartCore(a, s, ct).ConfigureAwait(false);
    }

    internal static async Task<int> StartCore(CliArgs a, Session s, CancellationToken ct)
    {
        var query = string.Join(' ', a.Words);
        if (query.Length == 0) { Out.Error("start what? a saved profile name or a history id (see 'list saved' / 'list history')"); return Usage; }
        if (s.UsesServer) return await RemoteCommands.StartAsync(s, a, ct).ConfigureAwait(false);
        if (!Writable(s)) return Error;
        var lib = s.Engine.Library;

        var profile = Find(lib.Profiles, p => p.Name, query, out var ambiguousP);
        HistoryEntry? hist = null;
        if (profile == null && ambiguousP.Count == 0)
            hist = lib.History.FirstOrDefault(h => h.Key.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                ?? Find(lib.History, h => h.Name, query, out _);
        if (ambiguousP.Count > 0) return Ambiguous(query, ambiguousP.Select(p => p.Name));
        if (profile == null && hist == null) { Out.Error($"no saved profile or history entry matches '{query}'"); return NotFound; }

        if (profile != null && s.Launcher.RunningFor(profile) is { Online: true } already)
        {
            Console.Out.WriteLine($"{profile.Name} is already running at {already.Url}");
            return Ok;
        }
        string name = profile?.Name ?? hist!.Name;
        bool started = profile != null ? await s.Launcher.StartProfileAsync(profile).ConfigureAwait(false)
                                        : await s.Launcher.StartHistoryAsync(hist!).ConfigureAwait(false);
        if (!started) return s.Prompt.Declined ? Cancelled : Error;
        Console.Out.WriteLine($"{Out.Green("started")} {name}");
        if (!a.Wait) return Ok;

        // Warten, bis der Server antwortet (Frist wie in der GUI)
        var key = profile?.Key ?? hist!.Key;
        var end = DateTime.Now + s.Engine.Settings.StartTimeout;
        Console.Error.Write(Out.Dim("waiting for the server to answer "));
        while (DateTime.Now < end && !ct.IsCancellationRequested)
        {
            await s.TickAsync(1, ct).ConfigureAwait(false);
            var w = s.Engine.Servers.FirstOrDefault(x => x.Info.Program != null && Library.MakeKey(x.Info.Program, x.Info.Args) == key);
            if (w is { Online: true }) { Console.Error.WriteLine(); Console.Out.WriteLine($"{Out.Green("ready")} {w.Name} at {w.Url}"); return Ok; }
            if (s.Engine.Registry.Launches.Any(l => l.State == LaunchState.Failed && (profile == null || l.ProfileId == profile.Id)))
            {
                Console.Error.WriteLine();
                Out.Error("the server process ended before it was ready (see its log in the data folder)");
                return Error;
            }
            Console.Error.Write(".");
            try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
        Console.Error.WriteLine();
        Out.Error("the server did not answer in time");
        return Error;
    }

    // ── stop / unload ──

    public static async Task<int> StopAsync(CliArgs a, CancellationToken ct)
    {
        var query = string.Join(' ', a.Words);
        if (query.Length == 0) { Out.Error("stop what? a running server name or port (see 'status')"); return Usage; }
        using var s = new Session(a, write: true);
        await s.TickAsync(1, ct).ConfigureAwait(false);
        return await StopCore(a, s, ct).ConfigureAwait(false);
    }

    internal static async Task<int> StopCore(CliArgs a, Session s, CancellationToken ct = default)
    {
        var query = string.Join(' ', a.Words);
        if (query.Length == 0) { Out.Error("stop what? a running server name or port (see 'status')"); return Usage; }
        if (s.UsesServer) return await RemoteCommands.StopAsync(s, a, ct).ConfigureAwait(false);
        if (!Writable(s)) return Error;
        var (server, code) = FindServer(s.Engine, query);
        if (server == null) return code;
        await s.Launcher.StopServerAsync(server).ConfigureAwait(false);
        if (s.Prompt.Declined) return Cancelled;
        if (s.Prompt.Failed) return Error;
        Console.Out.WriteLine($"{Out.Green("stopped")} {server.Name}");
        return Ok;
    }

    public static async Task<int> UnloadAsync(CliArgs a, CancellationToken ct)
    {
        if (a.Words.Count < 2) { Out.Error("usage: unload <server> <model>"); return Usage; }
        using var s = new Session(a, write: true);
        await s.TickAsync(1, ct).ConfigureAwait(false);
        return await UnloadCore(a, s, ct).ConfigureAwait(false);
    }

    internal static async Task<int> UnloadCore(CliArgs a, Session s, CancellationToken ct = default)
    {
        if (a.Words.Count < 2) { Out.Error("usage: unload <server> <model>"); return Usage; }
        if (s.UsesServer) return await RemoteCommands.UnloadAsync(s, a, ct).ConfigureAwait(false);
        if (!Writable(s)) return Error;
        var (server, code) = FindServer(s.Engine, a.Words[0]);
        if (server == null) return code;
        var modelQuery = string.Join(' ', a.Words.Skip(1));
        var model = Find(server.Models, m => m.Name, modelQuery, out var amb);
        if (amb.Count > 0) return Ambiguous(modelQuery, amb.Select(m => m.Name));
        if (model == null) { Out.Error($"{server.Name} has no loaded model matching '{modelQuery}'"); return NotFound; }
        await s.Launcher.UnloadModelAsync(server, model.Name).ConfigureAwait(false);
        if (s.Prompt.Declined) return Cancelled;
        if (s.Prompt.Failed) return Error;
        Console.Out.WriteLine($"{Out.Green("unloaded")} {model.Name}");
        return Ok;
    }

    // Schreibende Befehle: nicht neben der offenen App (sie hat den Datenordner); Rückfragen-Zustand des letzten Befehls vergessen
    private static bool Writable(Session s)
    {
        s.Prompt.Reset();
        if (!s.ReadOnly || s.Simulated) return true;
        Out.Error("read-only: StykkerLLM is open with this data folder. Use the app for this, or close it and restart stykker.");
        return false;
    }

    // Server nach Port ("8081", ":8081"), Schlüssel ("127.0.0.1:8081") oder Name
    internal static (ServerWatcher? Server, int Code) FindServer(MonitorEngine e, string query)
    {
        var q = query.TrimStart(':');
        var servers = e.Servers;
        if (int.TryParse(q, out int port))
        {
            var byPort = servers.Where(x => x.Info.Port == port).ToList();
            if (byPort.Count == 1) return (byPort[0], Ok);
        }
        var byKey = servers.FirstOrDefault(x => x.Key.Equals(q, StringComparison.OrdinalIgnoreCase));
        if (byKey != null) return (byKey, Ok);
        var byName = Find(servers, x => x.Name, query, out var amb);
        if (amb.Count > 0) return (null, Ambiguous(query, amb.Select(x => $"{x.Name} ({x.Url})")));
        if (byName != null) return (byName, Ok);
        Out.Error($"no running server matches '{query}'");
        return (null, NotFound);
    }

    // Genau gleich (ohne Groß/klein) gewinnt; sonst Anfang; sonst enthalten. Mehrere Treffer auf derselben Stufe = mehrdeutig.
    internal static T? Find<T>(IEnumerable<T> items, Func<T, string> name, string query, out List<T> ambiguous) where T : class
    {
        ambiguous = new();
        var list = items.ToList();
        foreach (var match in new Func<string, bool>[]
        {
            n => n.Equals(query, StringComparison.OrdinalIgnoreCase),
            n => n.StartsWith(query, StringComparison.OrdinalIgnoreCase),
            n => n.Contains(query, StringComparison.OrdinalIgnoreCase),
        })
        {
            var hits = list.Where(x => match(name(x))).ToList();
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1) { ambiguous = hits; return null; }
        }
        return null;
    }

    private static int Ambiguous(string query, IEnumerable<string> names)
    {
        Out.Error($"'{query}' matches more than one:\n  " + string.Join("\n  ", names));
        return NotFound;
    }
}
