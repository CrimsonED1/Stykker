using StykkerLlm.Core;

namespace StykkerLlm.Cli.Tui;

// Die Funktionen, die der TUI gegenüber Fenster und Web noch fehlten (docs/ui.md, Bereiche 3, 5, 6, 8 und 10):
// das Profilmenü (Bench/Edit/Remove), Verlauf merken und vergessen, Aufnahmen ansehen/löschen/vergleichen,
// GPU- und RAM-Liste je Prozess, VRAM freigeben, Server von Hand eintragen, einen Benchmark starten.
// Wie die übrigen TUI-Befehle: Läuft der Server, kommen Zustand und Aktionen von ihm; sonst aus der eigenen Engine.
internal static class TuiCommands
{
    private sealed record Live(string Key, string Name, double Elapsed, int Requests, bool Global, double Tps);

    // ── /saved <name|id> [rename <name> | note <text> | args <tokens> | bench | remove] ──

    public static async Task<int> SavedAsync(Session s, CliArgs a, CancellationToken ct)
    {
        if (a.Words.Count == 0) return await Commands.ListCore(TuiApp.List(a, "saved"), s).ConfigureAwait(false);
        if (a.Words.Count == 1) return Commands.ShowCore(a, s);   // /saved <name> zeigt das Profil wie /show
        var query = a.Words[0];
        string what = a.Words[1].ToLowerInvariant();
        var rest = a.Words.Skip(2).ToList();

        var prof = FindProfile(s, query);
        if (prof == null) { Out.Error($"no saved profile matches '{query}' (see /saved)"); return Commands.NotFound; }

        switch (what)
        {
            case "rename":
                if (rest.Count == 0) { Out.Error("rename to what?  /saved <name> rename <new name>"); return Commands.Usage; }
                var newName = string.Join(' ', rest);
                var rr = await Do(s, new ActionRequest { Action = "profile.rename", Arg = prof.Arg, Name = newName }, ct).ConfigureAwait(false);
                return Done(rr, rr.Ok ? Strings.TuiRenamed(prof.Name, newName) : null);
            case "note":
                if (rest.Count == 0) { Out.Error("note what?  /saved <name> note <text>"); return Commands.Usage; }
                return Done(await Do(s, new ActionRequest { Action = "profile.note", Arg = prof.Arg, Name = string.Join(' ', rest) }, ct).ConfigureAwait(false));
            case "args":
                if (rest.Count == 0) { Out.Error("args what?  /saved <name> args -m model.gguf --port 8081"); return Commands.Usage; }
                var ra = new ActionRequest { Action = "profile.args", Arg = prof.Arg };
                ra.Values["args"] = string.Join(' ', rest);
                return Done(await Do(s, ra, ct).ConfigureAwait(false), Strings.TuiArgsSet);
            case "remove":
                if (!await s.Prompt.ConfirmAsync(Strings.SectionSaved, Strings.RemoveProfileConfirm(prof.Name), warning: true).ConfigureAwait(false)) return Commands.Cancelled;
                var rm = await Do(s, new ActionRequest { Action = "profile.remove", Arg = prof.Arg }, ct).ConfigureAwait(false);
                return Done(rm, rm.Ok ? Strings.TuiSavedRemoved(prof.Name) : null);
            case "bench":
                return await BenchRunAsync(s, prof.Name, rest, ct).ConfigureAwait(false);
            default:
                Out.Error(Strings.TuiSavedMenu);
                return Commands.Usage;
        }
    }

    // ── /history <text> | /history save [name] <id> | /history forget <id> ──

    public static async Task<int> HistoryAsync(Session s, CliArgs a, CancellationToken ct)
    {
        if (a.Words.Count == 0) return await Commands.ListCore(TuiApp.List(a, "history"), s).ConfigureAwait(false);
        // "/history save [neuer Name] <Id>": der Name steht vor der Id, die mit einem Hex-Zeichen beginnt
        if (a.Words[0].ToLowerInvariant() is var head && (head == "save" || head == "forget"))
        {
            string rest = string.Join(' ', a.Words.Skip(1));
            var entry = FindHistory(s, rest);
            if (entry == null) { Out.Error($"no history entry matches '{rest}' (see /history)"); return Commands.NotFound; }
            if (head == "forget")
            {
                if (!await s.Prompt.ConfirmAsync(Strings.SectionHistory, Strings.ForgetConfirm(entry.Name), warning: true).ConfigureAwait(false)) return Commands.Cancelled;
                var rf = await Do(s, new ActionRequest { Action = "history.forget", Arg = entry.Key }, ct).ConfigureAwait(false);
                return Done(rf, rf.Ok ? Strings.TuiHistoryForgotten(entry.Name) : null);
            }
            if (entry.Program.Length == 0) { Out.Error(Strings.SaveBlockNoCommand); return Commands.Error; }
            string? name = a.Words.Count > 2 ? string.Join(' ', a.Words.Skip(1).Take(a.Words.Count - 2)) : null;
            var rs = await Do(s, new ActionRequest { Action = "profile.add-history", Arg = entry.Key, Name = name }, ct).ConfigureAwait(false);
            return Done(rs, rs.Ok ? Strings.TuiHistorySaved(name ?? entry.Name) : null);
        }
        return await Commands.ListCore(TuiApp.List(a, "history"), s).ConfigureAwait(false);   // "/history <text>" ist die Suche in der Liste
    }

    // ── /record [<server>|all] ──

    public static async Task<int> RecordAsync(Session s, CliArgs a, CancellationToken ct)
    {
        string target = string.Join(' ', a.Words);
        if (target.Length == 0) target = "all";
        bool global = target is "all" or "*";
        var running = Active(s);

        // Läuft für dieses Ziel schon eine Aufnahme? Dann beenden, sonst beginnen (wie der Record-Knopf)
        var active = global ? running.FirstOrDefault(x => x.Global) : running.FirstOrDefault(x => !x.Global && Match(x.Name, x.Key, target));
        if (active != null)
        {
            var stop = await Do(s, new ActionRequest { Action = "record.stop", Arg = global ? "all" : active.Key }, ct).ConfigureAwait(false);
            if (!stop.Ok) return Done(stop, null);
            Console.Out.WriteLine(Out.Green(Strings.MenuStopRecording) + "  " + Out.Dim((active.Global ? Strings.AllServers : active.Name) + " " + Fmt.Dur(active.Elapsed)));
            var rest = Active(s);
            if (rest.Count > 0) Print(rest);
            return Commands.Ok;
        }
        var start = await Do(s, new ActionRequest { Action = "record.start", Arg = global ? "all" : target }, ct).ConfigureAwait(false);
        if (!start.Ok) return Done(start, null);
        Console.Out.WriteLine(Out.Green(Strings.MenuRecordAll));
        Print(Active(s));
        return Commands.Ok;
    }

    private static void Print(List<Live> sessions)
    {
        if (sessions.Count == 0) { Console.Out.WriteLine(Out.Dim(Strings.NoActiveRecording)); return; }
        Console.Out.WriteLine(Out.Dim(Strings.RecordingNow(string.Join(", ", sessions.Select(x =>
            (x.Global ? Strings.AllServers : x.Name) + " " + Fmt.Dur(x.Elapsed))))));
    }

    private static List<Live> Active(Session s)
    {
        if (s.UsesServer && s.Server is { } state)
            return state.Recording.Sessions.Select(x => new Live(x.Key, Name(s, x.Key) ?? x.Key, x.ElapsedSec, 0, x.Global, x.LastTps)).ToList();
        return s.Engine.Recorder.Sessions.Select(x => new Live(x.Target, Name(s, x.Target) ?? x.Target, x.ElapsedSec, x.Requests, x.IsGlobal, x.LastTps)).ToList();
    }

    // ── /recordings [<id> | compare <a> <b> | delete <id>] ──

    public static async Task<int> RecordingsAsync(Session s, CliArgs a, CancellationToken ct)
    {
        if (a.Words.Count == 0) return await Commands.ListCore(TuiApp.List(a, "recordings"), s).ConfigureAwait(false);
        var recs = RecordingStore.List(s.Engine.Paths.RecordingsDir);
        string what = a.Words[0].ToLowerInvariant();
        var o = Console.Out;

        if (what == "compare")
        {
            if (a.Words.Count < 3) { Out.Error(Strings.TuiRecordingsUsage); return Commands.Usage; }
            var one = Pick(recs, a.Words[1]);
            var two = Pick(recs, a.Words[2]);
            if (one == null || two == null) { Out.Error(Strings.TuiRecordingsUsage); return Commands.NotFound; }
            o.WriteLine(Out.Bold(Strings.CompareTitle));
            Out.Table(new[] { Strings.ColProperty, Name(one), Name(two), Strings.CompareDelta },
                RecordingMetrics.Compare(one, two).Select(r => (IReadOnlyList<string>)new[]
                {
                    r.Label, r.A, r.B, r.Better > 0 ? Out.Green(r.Delta) : r.Better < 0 ? Out.Red(r.Delta) : Out.Dim(r.Delta),
                }), o);
            return Commands.Ok;
        }

        if (what == "delete")
        {
            if (a.Words.Count < 2) { Out.Error(Strings.TuiRecordingsUsage); return Commands.Usage; }
            var victim = Pick(recs, string.Join(' ', a.Words.Skip(1)));
            if (victim == null) { Out.Error(Strings.TuiRecordingsUsage); return Commands.NotFound; }
            if (!await s.Prompt.ConfirmAsync(Strings.RecordingsTitle, Strings.DeleteRecordingsConfirm(1), warning: true).ConfigureAwait(false)) return Commands.Cancelled;
            var del = await Do(s, new ActionRequest { Action = "record.delete", Arg = victim.File }, ct).ConfigureAwait(false);
            return Done(del, del.Ok ? Strings.RecordingDeleted : null);
        }

        var sum = Pick(recs, string.Join(' ', a.Words));
        if (sum == null) { Out.Error($"no recording matches '{string.Join(' ', a.Words)}' (see /recordings)"); return Commands.NotFound; }
        var data = RecordingStore.Load(sum.File);
        if (data == null) { Out.Error(Strings.RecordingUnreadable); return Commands.Error; }
        o.WriteLine(Out.Bold(Name(sum)) + Out.Dim($"   {Fmt.Dur(sum.DurationSec)}  ·  {(sum.Proxy ? Strings.ModeProxy : Strings.ModeLogOnly)}"));
        o.WriteLine();
        Out.Table(new[] { Strings.ColProperty, Strings.ColValue }, new List<IReadOnlyList<string>>
        {
            new[] { Strings.TileRequests, sum.Requests.ToString(Strings.Inv) },
            new[] { Strings.TileBusy, Strings.N0(sum.BusyPct) + " %" },
            new[] { Strings.TileTokens, Strings.N0(sum.TokensIn) + " → " + Strings.N0(sum.TokensOut) },
            new[] { Strings.TileCache, Strings.N0(sum.CachePct) + " %" },
            new[] { Strings.TileTps, Strings.N1(sum.AvgTps) + " / " + Strings.N1(sum.PeakTps) },
            new[] { Strings.TileVram, Strings.N1(sum.MaxVramGb) + " GB" },
            new[] { Strings.TileEnergy, Strings.N2(sum.EnergyWh) + " Wh" },
            new[] { Strings.TileTtft, sum.TtftSamples > 0 ? Strings.N2(sum.TtftP50) + " / " + Strings.N2(sum.TtftP95) + " s" : "–" },
        }, o);
        o.WriteLine();
        o.WriteLine(Out.Bold(Strings.SecTimeline));
        Out.Table(new[] { Strings.ColTime, Strings.ColServer, Strings.ColModel, Strings.ColReqPrompt, Strings.ColReqGeneration, Strings.ColReqTokens, Strings.ColStatus },
            data.Requests.OrderByDescending(x => x.Start).Take(40).Select(q => (IReadOnlyList<string>)new[]
            {
                q.Start.ToString("HH:mm:ss", Strings.Inv), Out.Cut(q.ServerName, 20), Out.Cut(q.Model, 24),
                q.PromptTps > 0 ? Strings.N0(q.PromptTps) : "–",
                q.GenTps > 0 ? Strings.N1(q.GenTps) : "–",
                q.GenTokens.ToString(Strings.Inv), q.Status,
            }), o);
        return Commands.Ok;
    }

    // Eine Aufnahme über ihre Id, einen Anfang davon, ihr Modell oder ihren Zeitpunkt finden
    private static RecordingSummary? Pick(List<RecordingSummary> recs, string query) =>
        recs.FirstOrDefault(r => r.Id == query)
        ?? recs.FirstOrDefault(r => r.Id.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        ?? recs.FirstOrDefault(r => r.Model.Contains(query, StringComparison.OrdinalIgnoreCase))
        ?? (DateTime.TryParse(query, out var when) ? recs.FirstOrDefault(r => Math.Abs((r.Started - when).TotalMinutes) < 1) : null);

    private static string Name(RecordingSummary r) => (r.Model.Length > 0 ? r.Model : r.Target.Length > 0 ? r.Target : Strings.AllServers)
        + " · " + r.Started.ToString("MM-dd HH:mm", Strings.Inv);

    // ── /gpu ──

    public static int Gpu(Session s)
    {
        var o = Console.Out;
        var state = s.Server;
        var gpu = state?.Gpu ?? s.Engine.Gpu;
        // Vor dem ersten Takt ist noch gar nichts gemessen – das ist kein fehlendes NVML
        if (gpu == null) o.WriteLine(Out.Dim(s.Engine.Ticks == 0 && !s.UsesServer ? Strings.TuiGpuWaiting : Strings.TuiGpuNone));
        else
        {
            o.WriteLine(Out.Bold(gpu.Name));
            Out.Table(new[] { Strings.ColProperty, Strings.ColValue }, new List<IReadOnlyList<string>>
            {
                new[] { Strings.GpuLoad, $"{gpu.Util.ToString("0", Strings.Inv)} %" },
                new[] { Strings.GpuVram, $"{Strings.N1(gpu.MemUsedGb)} / {Strings.N1(gpu.MemTotalGb)} GB" },
                new[] { Strings.VramFree, Strings.N1(gpu.MemFreeGb) + " GB" },
                new[] { Strings.GpuPower, $"{gpu.PowerW.ToString("0", Strings.Inv)} / {gpu.PowerLimitW.ToString("0", Strings.Inv)} W" },
                new[] { Strings.GpuTemp, $"{gpu.TempC.ToString("0", Strings.Inv)} °C" },
                new[] { Strings.GpuMemController, $"{gpu.MemUtil.ToString("0", Strings.Inv)} %" },
                new[] { Strings.GpuGfxClock, $"{gpu.GfxMhz.ToString("0", Strings.Inv)} / {gpu.GfxMaxMhz.ToString("0", Strings.Inv)} MHz" },
                new[] { Strings.GpuMemClock, $"{gpu.MemMhz.ToString("0", Strings.Inv)} / {gpu.MemMaxMhz.ToString("0", Strings.Inv)} MHz" },
                new[] { Strings.GpuThrottling, gpu.Throttled ? Out.Yellow(gpu.ThrottleText()) : Out.Green(Strings.GpuClocksFree) },
            }, o);
        }

        // Wie im Fenster: die drei größten Belegungen, der Rest als Zeile (kein Diagramm im Terminal)
        var util = s.UsesServer ? new Dictionary<string, double>() : GpuUtil(s);
        PrintTop(o, Strings.GpuConsumers, state?.VramTop ?? s.Engine.VramTop.ToList(), gpu?.MemTotalGb ?? 0, util);
        PrintTop(o, Strings.RamConsumers, state?.RamTop ?? s.Engine.RamTop.ToList(), state?.Sys?.RamTotalGb ?? s.Engine.Sys?.RamTotalGb ?? 0, null);
        return Commands.Ok;
    }

    private static void PrintTop(System.IO.TextWriter o, string what, List<(string Name, double Gb)> top, double totalGb, Dictionary<string, double>? util)
    {
        o.WriteLine();
        o.WriteLine(Out.Bold(what) + Out.Dim("   " + Strings.TuiMemSum(Math.Min(3, top.Count), top.Take(3).Sum(x => x.Gb))));
        if (top.Count == 0) { o.WriteLine(Out.Dim("  " + Strings.NoneYet)); return; }
        for (int i = 0; i < Math.Min(3, top.Count); i++)
        {
            var (name, gb) = top[i];
            string share = totalGb > 0 ? $"   {(gb / totalGb * 100).ToString("0", Strings.Inv)} %" : "";
            string load = util != null && util.TryGetValue(name, out var pct) ? Out.Dim($"   {pct.ToString("0", Strings.Inv)} % GPU") : "";
            o.WriteLine("  " + Out.Cut(name, 40) + "   " + Strings.N1(gb) + " GB" + share + load);
        }
        if (top.Count > 3) o.WriteLine(Out.Dim("  " + Strings.MemMore(top.Count - 3)));
    }

    // GPU-Auslastung je Prozess: nur mit eigener Plattform erreichbar, der Server schickt sie nicht mit
    private static Dictionary<string, double> GpuUtil(Session s)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var (name, pct) in s.Engine.Platform.ReadGpuUtilTop(64)) map[name] = pct; } catch { /* keine Auslastung je Prozess */ }
        return map;
    }

    // ── /freevram ──

    public static async Task<int> FreeVramAsync(Session s, CancellationToken ct)
    {
        if (!await s.Prompt.ConfirmAsync(Strings.FreeVramTitle, Strings.TuiFreeVramConfirm, warning: true).ConfigureAwait(false)) return Commands.Cancelled;
        var res = await Do(s, new ActionRequest { Action = "freevram" }, ct).ConfigureAwait(false);
        return Done(res, res.Ok ? Strings.BtnFreeVram : null);
    }

    // ── /add <url> [name] [logfile]  ·  /remove <server> ──

    public static async Task<int> AddAsync(Session s, CliArgs a, CancellationToken ct)
    {
        if (a.Words.Count == 0) { Out.Error(Strings.TuiAddUsage); return Commands.Usage; }
        var req = new ActionRequest { Action = "manual.add", Arg = a.Words[0], Name = a.Words.Count > 1 ? a.Words[1] : a.Words[0] };
        if (a.Words.Count > 2) req.Arg2 = string.Join(' ', a.Words.Skip(2));
        var res = await Do(s, req, ct).ConfigureAwait(false);
        return Done(res, res.Ok ? Strings.TuiAdded + req.Name : null);
    }

    public static async Task<int> RemoveServerAsync(Session s, CliArgs a, CancellationToken ct)
    {
        string query = string.Join(' ', a.Words);
        if (query.Length == 0) { Out.Error(Strings.TuiRemoveUsage); return Commands.Usage; }
        var server = FindServer(s, query);
        if (server == null) { Out.Error($"no running server matches '{query}' (see /status)"); return Commands.NotFound; }
        if (!await s.Prompt.ConfirmAsync(Strings.RemoveServerTitle, Strings.TuiRemoveServerConfirm(server.Value.Name), warning: true).ConfigureAwait(false)) return Commands.Cancelled;
        var res = await Do(s, new ActionRequest { Action = "manual.remove", Arg = server.Value.Key }, ct).ConfigureAwait(false);
        return Done(res, res.Ok ? Strings.TuiRemoved + server.Value.Name : null);
    }

    // ── /bench run <server> [chat] [tool] [parallel] [ctx=4096,8192] [gen=256] [slots=4] [repeats=1] [seed=42] ──

    public static async Task<int> BenchRunAsync(Session s, string target, List<string> words, CancellationToken ct)
    {
        if (target.Length == 0) { Out.Error(Strings.TuiBenchRunUsage); return Commands.Usage; }
        if (!s.UsesServer) { Out.Error(Strings.TuiServerOnly); return Commands.Error; }

        var req = new ActionRequest { Action = "bench.run", Arg = target };
        foreach (var w in words)
        {
            if (w.Equals("chat", StringComparison.OrdinalIgnoreCase)) req.Values["chat"] = "1";
            else if (w.Equals("tool", StringComparison.OrdinalIgnoreCase)) req.Values["tool"] = "1";
            else if (w.Equals("parallel", StringComparison.OrdinalIgnoreCase)) req.Values["parallel"] = "1";
            else if (w.StartsWith("ctx=", StringComparison.OrdinalIgnoreCase)) req.Values["contextSizes"] = w[4..];
            else if (w.StartsWith("gen=", StringComparison.OrdinalIgnoreCase)) req.Values["genTokens"] = w[4..];
            else if (w.StartsWith("slots=", StringComparison.OrdinalIgnoreCase)) req.Values["slots"] = w[6..];
            else if (w.StartsWith("repeats=", StringComparison.OrdinalIgnoreCase)) req.Values["repeats"] = w[8..];
            else if (w.StartsWith("seed=", StringComparison.OrdinalIgnoreCase)) req.Values["seed"] = w[5..];
            else { Out.Error(Strings.TuiBenchRunUsage); return Commands.Usage; }
        }
        Console.Out.WriteLine(Out.Dim(Strings.BenchTitle + " · " + target + " …"));
        var res = await s.Remote!.SendAsync(req, ct).ConfigureAwait(false);
        int code = Done(res, res.Ok ? res.Message : null);
        // Der Rückgang steht am gespeicherten Ergebnis; frischer Zustand holen, damit er gleich sichtbar wird
        if (code == Commands.Ok && await s.Remote!.GetStateAsync(ct).ConfigureAwait(false) is { } fresh
            && res.Data is { Length: > 0 } id && fresh.Benchmarks.FirstOrDefault(b => b.Id == id)?.Regression is { } reg)
            Console.Out.WriteLine(Out.Yellow(reg.Text));
        return code;
    }

    // ── Gemeinsam ──

    // Alles, was geschrieben wird, macht der Server (eine Engine im System); ohne Server die eigene, sofern der
    // Datenordner frei ist. Rückfragen laufen über die Eingabezeile wie beim Starten und Stoppen.
    private static async Task<ActionResult> Do(Session s, ActionRequest req, CancellationToken ct)
    {
        if (s.UsesServer) return await s.Remote!.SendAsync(req, ct).ConfigureAwait(false);
        if (s.Simulated || !s.ReadOnly)
            return await ActionApi.ExecuteAsync(req, new ActionContext { Engine = s.Engine, Launcher = s.Launcher }, s.Prompt, ct).ConfigureAwait(false);
        return ActionResult.Fail(Strings.ReadOnlyEngine);
    }

    // Meldung in der Farbe des Ergebnisses (null = kein Text, wenn nichts zu melden ist). Kommt die Meldung ohne
    // Text zurück (etwa von einem Server ohne Begründung), steht wenigstens der allgemeine Satz dort.
    private static int Done(ActionResult result, string? text = null)
    {
        if (!result.Ok) { Out.Error(result.Message.Length > 0 ? result.Message : Strings.ActionFailed); return Commands.Error; }
        if (text is { Length: > 0 }) Console.Out.WriteLine(Out.Green(text));
        return Commands.Ok;
    }

    private sealed record ProfileRef(string Arg, string Name);

    // Profil über Namen oder Id; ein unvollständiger Name genügt, wenn nur einer passt
    private static ProfileRef? FindProfile(Session s, string query)
    {
        if (s.UsesServer && s.Server is { } state)
        {
            var p = state.Profiles.FirstOrDefault(x => x.Name == query || x.Id == query);
            if (p == null) { var hits = new List<RemoteProfile>(); p = Commands.Find(state.Profiles, x => x.Name, query, out hits); }
            return p == null ? null : new ProfileRef(p.Id, p.Name);
        }
        var list = s.Engine.Library.Profiles;
        var prof = list.FirstOrDefault(x => x.Id.ToString("N") == query || x.Name == query);
        if (prof == null) { var hits = new List<Profile>(); prof = Commands.Find(list, x => x.Name, query, out hits); }
        return prof == null ? null : new ProfileRef(prof.Id.ToString("N"), prof.Name);
    }

    private sealed record HistoryRef(string Key, string Name, string Program);

    private static HistoryRef? FindHistory(Session s, string query)
    {
        if (s.UsesServer && s.Server is { } state)
        {
            var h = state.History.FirstOrDefault(x => x.Key == query || x.Name == query)
                ?? state.History.FirstOrDefault(x => x.Key.StartsWith(query, StringComparison.OrdinalIgnoreCase));
            if (h == null) { var hits = new List<RemoteHistory>(); h = Commands.Find(state.History, x => x.Name, query, out hits); }
            return h == null ? null : new HistoryRef(h.Key, h.Name.Length > 0 ? h.Name : query, h.Program);
        }
        var list = s.Engine.Library.History;
        var e = list.FirstOrDefault(x => x.Key == query || x.Name == query)
            ?? list.FirstOrDefault(x => x.Key.StartsWith(query, StringComparison.OrdinalIgnoreCase));
        if (e == null) { var hits = new List<HistoryEntry>(); e = Commands.Find(list, x => x.Name, query, out hits); }
        return e == null ? null : new HistoryRef(e.Key, e.Name.Length > 0 ? e.Name : query, e.Program);
    }

    private static bool Match(string name, string key, string query) =>
        key.Equals(query, StringComparison.OrdinalIgnoreCase) || name.Equals(query, StringComparison.OrdinalIgnoreCase)
        || name.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string? Name(Session s, string key)
    {
        if (s.UsesServer && s.Server is { } state)
            return state.Servers.FirstOrDefault(x => x.Key == key)?.Name ?? key;
        return s.Engine.Servers.FirstOrDefault(x => x.Key == key)?.Name ?? key;
    }

    private static (string Key, string Name)? FindServer(Session s, string query)
    {
        if (s.UsesServer && s.Server is { } state)
        {
            var sv = state.Servers.FirstOrDefault(x => x.Key == query || x.Name == query)
                ?? (int.TryParse(query, out var port) ? state.Servers.FirstOrDefault(x => x.Port == port) : null);
            return sv == null ? null : (sv.Key, sv.Name);
        }
        var w = s.Engine.Servers.FirstOrDefault(x => x.Key == query || x.Name == query)
            ?? (int.TryParse(query, out var p) ? s.Engine.Servers.FirstOrDefault(x => x.Info.Port == p) : null);
        return w == null ? null : (w.Key, w.Name);
    }
}