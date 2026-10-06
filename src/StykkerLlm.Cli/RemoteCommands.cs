using StykkerLlm.Core;

namespace StykkerLlm.Cli;

// Alles, was der Server schickt, ohne eigene Engine: Zustand für status/list/show, Aktionen für start/stop/unload.
// Läuft der Server nicht, machen die Befehle wie bisher ihre Arbeit selbst (Session ohne Server).
public static class RemoteCommands
{
    // ── status ──

    public static void PrintStatus(Session s, CliArgs a)
    {
        var state = s.Server;
        if (state == null) return;
        var o = Console.Out;
        if (a.Json)
        {
            Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                schema = StateJson.Schema,
                source = "server",
                state.Time,
                state.Ticks,
                gpu = state.Gpu is { } g ? new { g.Name, g.Util, vramUsedGb = g.MemUsedGb, vramTotalGb = g.MemTotalGb, g.TempC, g.PowerW, throttle = g.ThrottleText() } : null,
                system = state.Sys is { } sy ? new { sy.CpuPercent, sy.Cores, sy.RamUsedGb, sy.RamTotalGb } : null,
                servers = state.Servers.Select(sv => new
                {
                    sv.Key, sv.Name, sv.Backend, sv.Url, model = sv.Model, sv.State, sv.Online, sv.Manual, sv.Pid,
                    tps = sv.Current, vramGb = sv.VramGb, ramGb = sv.RamGb, cpu = sv.CpuPercent,
                    slots = sv.Slots.Count, busySlots = sv.Slots.Count(x => x.Busy), queue = sv.QueueCount,
                    clients = sv.Clients, models = sv.Models.Select(m => new { m.Name, sizeGb = m.SizeBytes / 1073741824.0, vramGb = m.VramBytes / 1073741824.0, m.ExpiresAt }),
                }).ToList(),
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = a.WatchSeconds == null }));
            return;
        }
        o.WriteLine(Out.Dim("server ") + NetAddr.Url("127.0.0.1", state.ServerPort == 0 ? 8078 : state.ServerPort) + Out.Dim("  ·  tick ") + state.Ticks);
        if (state.Gpu is { } gg)
            o.WriteLine($"{Out.Bold("GPU")}  {gg.Name}   {gg.Util:0} %   VRAM {gg.MemUsedGb:0.0} / {gg.MemTotalGb:0.0} GB   {gg.TempC:0} °C   {gg.PowerW:0} W" +
                (gg.Throttled ? "   " + Out.Yellow("throttled: " + gg.ThrottleText()) : ""));
        if (state.Sys is { } syy)
            o.WriteLine($"{Out.Bold("SYS")}  CPU {syy.CpuPercent:0} %   RAM {syy.RamUsedGb:0.0} / {syy.RamTotalGb:0.0} GB");
        if (state.Gpu != null || state.Sys != null) o.WriteLine();
        if (state.Servers.Count == 0) { o.WriteLine(Out.Dim(Strings.RunningEmpty)); return; }
        Out.Table(new[] { "", Strings.ColServer, Strings.ColBackend, Strings.ColUrl, Strings.ColModel, Strings.ColStatus, Strings.ColTps, Strings.GpuVram, Strings.ColSlot },
            state.Servers.Select(sv => (IReadOnlyList<string>)new[]
            {
                sv.Online ? Out.Green(Out.Dot(true)) : Out.Dim(Out.Dot(false)),
                Out.Cut(sv.Name, 28),
                sv.Backend,
                sv.Url,
                Out.Cut(ModelText(sv), 32),
                StateText(sv),
                sv.Online ? $"{sv.Current:0.0}" : "–",
                Out.Gb(sv.VramGb),
                SlotsText(sv),
            }), o);
    }

    private static string StateText(RemoteServer sv)
    {
        var text = sv.State switch
        {
            "busy" => Out.Green("busy"),
            "loading" => Out.Yellow("loading"),
            "offline" => Out.Red("offline"),
            var x => Out.Dim(x),
        };
        if (sv.SpillGb is double spill) text += " " + Out.Yellow(Strings.ChipSharedRam + " " + Out.Gb(spill));
        return text;
    }

    private static string ModelText(RemoteServer sv) =>
        sv.Models.Count > 0
            ? string.Join(", ", sv.Models.Select(m => m.Name + (Strings.Until(m.ExpiresAt) is { Length: > 0 } u ? " (" + u + ")" : "")))
            : sv.Model is { Length: > 0 } m && m != "–" ? m : "–";

    private static string SlotsText(RemoteServer sv)
    {
        var text = sv.Slots.Count == 0 ? "–" : $"{sv.Slots.Count(x => x.Busy)}/{sv.Slots.Count}";
        if (sv.QueueCount > 0) text += " " + Out.Yellow("+" + sv.QueueCount);
        return text;
    }

    // ── list ──

    public static int List(Session s, CliArgs a)
    {
        var state = s.Server!;
        var what = a.Words.FirstOrDefault()?.ToLowerInvariant() ?? "saved";
        var o = Console.Out;
        switch (what)
        {
            case "saved" or "profiles":
                if (state.Profiles.Count == 0) { o.WriteLine(Out.Dim(Strings.SavedEmpty)); return Commands.Ok; }
                Out.Table(new[] { Strings.ColName, Strings.ColModel, Strings.ColPort, Strings.ColLastStarted }, state.Profiles.Select(p => (IReadOnlyList<string>)new[]
                {
                    Out.Cut(p.Name, 32), Out.Cut(p.ModelPath.Length > 0 ? ServerInfo.ModelName(p.ModelPath) : "–", 40),
                    p.Port > 0 ? p.Port.ToString() : "–", p.LastStarted?.ToString("yyyy-MM-dd HH:mm") ?? Strings.NeverRun,
                }), o);
                return Commands.Ok;
            case "history":
                var q = string.Join(' ', a.Words.Skip(1));   // "history <text>" ist die Suche (wie das Suchfeld im Fenster)
                var hist = state.History.Where(h => Library.Matches(h, q)).ToList();
                if (hist.Count == 0) { o.WriteLine(Out.Dim(q.Length > 0 ? Strings.HistoryNoneMatch(q) : Strings.HistoryEmpty)); return Commands.Ok; }
                Out.Table(new[] { Strings.ColId, Strings.ColName, Strings.ColPort, Strings.ColLastSeen, Strings.ColRuns, Strings.ColBest, Strings.GpuVram }, hist.Select(h => (IReadOnlyList<string>)new[]
                {
                    h.Key[..Math.Min(8, h.Key.Length)],
                    Out.Cut(h.Name.Length > 0 ? h.Name : h.ModelPath.Length > 0 ? ServerInfo.ModelName(h.ModelPath) : "–", 32),
                    h.Port > 0 ? h.Port.ToString() : "–", h.LastSeen.ToString("yyyy-MM-dd HH:mm"), h.Runs.ToString(), $"{h.BestTps:0.0}",
                    h.MaxVramGb > 0 ? $"{h.MaxVramGb:0.0} GB" : "–",
                }), o);
                return Commands.Ok;
            case "bench" or "benchmarks":
                if (state.Benchmarks.Count == 0) { o.WriteLine(Out.Dim(Strings.BenchNoneYet)); return Commands.Ok; }
                Out.Table(new[] { Strings.ColStarted, Strings.ColName, Strings.ColModel, Strings.BenchSeriesGen }, state.Benchmarks.Select(b => (IReadOnlyList<string>)new[]
                {
                    b.Started.ToString("yyyy-MM-dd HH:mm"), Out.Cut(b.Title, 28), Out.Cut(b.Model, 32),
                    b.Regression is { } rg ? Out.Yellow($"{b.Steps.Where(x => x.Ok).Select(x => x.GenTps).DefaultIfEmpty(0).Max():0.0} ({Strings.BenchRegressionShort(rg.DropPct)})") : $"{b.Steps.Where(x => x.Ok).Select(x => x.GenTps).DefaultIfEmpty(0).Max():0.0}",
                }), o);
                foreach (var b in state.Benchmarks.Where(x => x.Regression != null).Take(3))
                    o.WriteLine("  " + Out.Yellow(b.Regression!.Text));
                return Commands.Ok;
            case "recordings":
                if (state.Recordings.Count == 0) { o.WriteLine(Out.Dim(Strings.RecordingNoneYet)); return Commands.Ok; }
                Out.Table(new[] { Strings.ColStarted, Strings.ColDuration, Strings.ColTarget, Strings.ColModel, Strings.ColRequests, Strings.ColAvg, Strings.ColPeak }, state.Recordings.Select(r => (IReadOnlyList<string>)new[]
                {
                    r.Started.ToString("yyyy-MM-dd HH:mm"), Fmt.Dur(r.DurationSec), Out.Cut(r.Target, 24), Out.Cut(r.Model, 32),
                    r.Requests.ToString(), $"{r.AvgTps:0.0}", $"{r.PeakTps:0.0}",
                }), o);
                return Commands.Ok;
            default:
                Out.Error($"list what? saved, history, bench or recordings (not '{what}')");
                return Commands.Usage;
        }
    }

    // ── show ──

    public static int Show(Session s, CliArgs a)
    {
        var state = s.Server!;
        var query = string.Join(' ', a.Words);
        var hist = state.History.FirstOrDefault(h => h.Key.StartsWith(query, StringComparison.OrdinalIgnoreCase) || h.Name == query);
        var prof = hist == null ? state.Profiles.FirstOrDefault(p => string.Equals(p.Name, query, StringComparison.OrdinalIgnoreCase)) : null;
        if (hist == null && prof == null) { Out.Error($"no history entry or saved profile matches '{query}'"); return Commands.NotFound; }

        var (name, program, args, workDir, model) = hist != null
            ? (hist.Name, hist.Program, (IReadOnlyList<string>)hist.Args, hist.WorkingDir, hist.ModelPath)
            : (prof!.Name, prof.Program, (IReadOnlyList<string>)prof.Args, prof.WorkingDir, prof.ModelPath);
        var command = CmdLine.Join(new[] { program }.Where(x => x.Length > 0).Concat(args));
        var parsed = LlamaServerArgs.Parse(args);
        var o = Console.Out;
        o.WriteLine(Out.Bold(name) + Out.Dim(hist != null ? $"   history {hist.Key[..Math.Min(8, hist.Key.Length)]}" : "   saved profile") + Out.Dim("   (from the server)"));
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
            Row(Strings.ColPort, hist.Port > 0 ? hist.Port.ToString() : null);
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
        var pars = parsed.Entries.Select(e => (IReadOnlyList<string>)new[] { e.Name.Length > 0 ? e.Name : Strings.RowArgument, e.Value ?? "" }).ToList();
        if (pars.Count == 0) o.WriteLine(Out.Dim(Strings.NoParameters));
        else Out.Table(new[] { Strings.ColOption, Strings.ColValue }, pars, o);
        return Commands.Ok;
    }

    // ── Aktionen ──

    public static async Task<int> StartAsync(Session s, CliArgs a, CancellationToken ct)
    {
        var query = string.Join(' ', a.Words);
        if (query.Length == 0) { Out.Error("start what? a saved profile name or a history id (see 'list saved' / 'list history')"); return Commands.Usage; }
        var state = s.Server!;
        var profile = state.Profiles.FirstOrDefault(p => p.Name == query || p.Id == query);
        if (profile != null)
        {
            var result = await s.Remote!.SendAsync("start", profile.Id, ct: ct).ConfigureAwait(false);
            Console.Out.WriteLine(result.Ok ? $"{Out.Green("started")} {profile.Name}" : Out.Red(result.Message));
            return result.Ok ? Commands.Ok : Commands.Error;
        }
        var hist = state.History.FirstOrDefault(h => h.Key.StartsWith(query, StringComparison.OrdinalIgnoreCase) || h.Name == query);
        if (hist == null) { Out.Error($"no saved profile or history entry matches '{query}'"); return Commands.NotFound; }
        var res2 = await s.Remote!.SendAsync("start-history", hist.Key, ct: ct).ConfigureAwait(false);
        Console.Out.WriteLine(res2.Ok ? $"{Out.Green("started")} {hist.Name}" : Out.Red(res2.Message));
        return res2.Ok ? Commands.Ok : Commands.Error;
    }

    public static async Task<int> StopAsync(Session s, CliArgs a, CancellationToken ct)
    {
        var query = string.Join(' ', a.Words);
        var state = s.Server!;
        var server = FindServer(state.Servers, query);
        if (server == null) { Out.Error($"no running server matches '{query}'"); return Commands.NotFound; }
        var result = await s.Remote!.SendAsync("stop", server.Key, ct: ct).ConfigureAwait(false);
        Console.Out.WriteLine(result.Ok ? $"{Out.Green("stopped")} {server.Name}" : Out.Red(result.Message));
        return result.Ok ? Commands.Ok : Commands.Error;
    }

    public static async Task<int> UnloadAsync(Session s, CliArgs a, CancellationToken ct)
    {
        if (a.Words.Count < 2) { Out.Error("usage: unload <server> <model>"); return Commands.Usage; }
        var state = s.Server!;
        var server = FindServer(state.Servers, a.Words[0]);
        if (server == null) { Out.Error($"no running server matches '{a.Words[0]}'"); return Commands.NotFound; }
        var model = server.Models.FirstOrDefault(m => m.Name.Contains(string.Join(' ', a.Words.Skip(1)), StringComparison.OrdinalIgnoreCase));
        if (model == null) { Out.Error($"{server.Name} has no loaded model matching that name"); return Commands.NotFound; }
        var result = await s.Remote!.SendAsync("unload", server.Key, model.Name, ct: ct).ConfigureAwait(false);
        Console.Out.WriteLine(result.Ok ? $"{Out.Green("unloaded")} {model.Name}" : Out.Red(result.Message));
        return result.Ok ? Commands.Ok : Commands.Error;
    }

    private static RemoteServer? FindServer(List<RemoteServer> servers, string query)
    {
        RemoteServer? Found(IEnumerable<RemoteServer> list) => list.FirstOrDefault(x =>
            x.Key.Equals(query, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
            || (int.TryParse(query, out var port) && x.Port == port));
        var direct = Found(servers);
        if (direct != null) return direct;
        var hits = servers.Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }
}
