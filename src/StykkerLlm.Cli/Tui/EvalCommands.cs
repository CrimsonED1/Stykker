using StykkerLlm.Core;
using StykkerLlm.Core.Eval;

namespace StykkerLlm.Cli.Tui;

// Modelltests in der TUI (Bereich 9 aus docs/ui.md). Läuft der Server, kommen Warteschlange, Rangliste und
// Modellliste von ihm und die Aktionen gehen an ihn; sonst arbeitet der Befehl wie `stykker eval` direkt.
internal static class EvalCommands
{
    // /eval – Warteschlange, /eval results – Rangliste, /eval models – je Modell ein Satz, /eval suites – vorhandene Suiten
    public static async Task<int> RunAsync(Session s, CliArgs a)
    {
        var what = a.Words.FirstOrDefault()?.ToLowerInvariant() ?? "runs";
        if (s.UsesServer && s.Server?.Eval != null) return await FromServerAsync(s, a, what).ConfigureAwait(false);
        // Ohne Server laufen nur die eigenen Testläufe ("/eval <server>"); die Listen und Aktionen gehören dem Server
        if (OnlyServer.Contains(what))
        {
            Out.Error(Strings.TuiServerOnly);
            return Commands.Error;
        }
        return await EvalCommand.RunAsync(a, CancellationToken.None).ConfigureAwait(false);
    }

    // Wörter, die nur der Server beantworten kann (Warteschlange, Rangliste, Katalog, Aktionen)
    private static readonly string[] OnlyServer = { "runs", "queue", "results", "rank", "models", "catalog", "suites", "start", "stop", "clear", "discover", "try", "all", "spread" };

    private static async Task<int> FromServerAsync(Session s, CliArgs a, string what)
    {
        var state = s.Server!.Eval!;
        var o = Console.Out;
        switch (what)
        {
            case "runs" or "queue":
                o.WriteLine(Out.Bold(Strings.EvalTitle) + Out.Dim(state.Running ? "   ·   " + Strings.EvalRunning : "   ·   " + Strings.EvalIdle));
                if (state.Jobs.Count == 0) { o.WriteLine(Out.Dim(Strings.EvalNoJobs)); break; }
                Out.Table(new[] { Strings.ColStatus, Strings.ColModel, Strings.EvalSuite, Strings.ColRep, Strings.ColDone, Strings.EvalScore, Strings.ColCurrent },
                    state.Jobs.Select(j => (IReadOnlyList<string>)new[]
                    {
                        StateColor(j.State), Out.Cut(j.Model, 28), Out.Cut(j.Suite, 18), j.Repeat.ToString(Strings.Inv),
                        $"{j.Done}/{j.Total}", $"{j.Score:0.0}",
                        j.Current.Length > 0 ? Out.Cut(j.Current, 32) : j.Note.Length > 0 ? Out.Dim(j.Note) : "–",
                    }), o);
                // Der laufende Auftrag zeigt seine letzten Aufgaben (wie im Fenster)
                var live = state.Jobs.FirstOrDefault(j => j.Live.Count > 0);
                if (live != null && state.Running)
                {
                    o.WriteLine();
                    o.WriteLine(Out.Dim("  " + live.Model + " · " + live.Suite));
                    foreach (var t in live.Live.TakeLast(8))
                        o.WriteLine($"   {(t.Passed ? Out.Green("✓") : t.Skipped ? Out.Dim("–") : Out.Red("✗"))} {Out.Cut(t.Title, 52)} {t.Score * 100:0} %  {Out.Dim(t.Note)}");
                }
                if (state.Models.Count == 0) o.WriteLine(Out.Dim("  " + Strings.EvalNoModels));
                o.WriteLine();
                o.WriteLine(Out.Dim("  " + Strings.EvalQueueHint));
                break;
            case "results" or "rank":
                o.WriteLine(Out.Bold(Strings.EvalResults));
                if (state.Jobs.Count == 0) { o.WriteLine(Out.Dim(Strings.EvalNoJobs)); break; }
                Out.Table(new[] { Strings.ColModel, Strings.EvalScore, Strings.ColRuns, Strings.EvalSuite, Strings.ColDone }, state.Jobs
                    .Where(j => j.Runs.Count > 0 || j.State == "done")
                    .OrderByDescending(j => j.Score)
                    .Select(j => (IReadOnlyList<string>)new[]
                    {
                        Out.Cut(j.Model, 34), $"{j.Score:0.0}", j.Runs.Count.ToString(Strings.Inv), Out.Cut(j.Suite, 18),
                        j.Finished?.ToString("yyyy-MM-dd HH:mm") ?? "–",
                    }), o);
                break;
            case "models":
                o.WriteLine(Out.Bold(Strings.EvalModels));
                if (state.Models.Count == 0) { o.WriteLine(Out.Dim(Strings.EvalNoModels)); break; }
                Out.Table(new[] { Strings.ColName, Strings.ColPort, Strings.ColSizeGb, Strings.EvalSource, Strings.ColOn, Strings.ColModelFile }, state.Models.Select(m => (IReadOnlyList<string>)new[]
                {
                    Out.Cut(m.Name, 30), m.Port.ToString(Strings.Inv), $"{m.SizeGb:0.0}", m.Source, m.Enabled ? "✓" : "–", Out.Cut(m.ModelFile, 40),
                }), o);
                break;
            case "start":
            {
                if (state.Models.Count == 0) { o.WriteLine(Out.Dim(Strings.EvalNoModels)); break; }
                var req = new ActionRequest { Action = "eval.enqueue", Number = 1 };
                req.Ids = state.Models.Where(m => m.Enabled).Select(m => m.Id).ToList();
                req.Values["suites"] = string.Join(",", new[] { EvalSuites.BuiltInName });
                var result = await s.Remote!.SendAsync(req).ConfigureAwait(false);
                o.WriteLine(result.Ok ? $"{Out.Green("queued")} {Strings.EvalQueued(result.Message)}" : Out.Red(result.Message));
                break;
            }
            case "stop":
            {
                var result = await s.Remote!.SendAsync("eval.stop").ConfigureAwait(false);
                o.WriteLine(result.Ok ? Out.Green(Strings.EvalQueueStops) : Out.Red(result.Message));
                break;
            }
            case "clear":
            {
                var result = await s.Remote!.SendAsync("eval.clear").ConfigureAwait(false);
                o.WriteLine(result.Ok ? Out.Green(Strings.EvalJobsCleared) : Out.Red(result.Message));
                break;
            }
            case "discover":
            {
                var result = await s.Remote!.SendAsync("eval.discover").ConfigureAwait(false);
                o.WriteLine(result.Ok ? $"{Out.Green("found")} {Strings.EvalFound(result.Message)}" : Out.Red(result.Message));
                break;
            }
            case "catalog" or "suites":
                PrintCatalog(s, state, a.Words.Skip(1).FirstOrDefault());
                break;
            case "try":
            {
                // /eval try <Modell-Id oder -Name> <Suite> – ein Test an einem Modell (wie „Try on model" im Web)
                if (a.Words.Count < 3) { Out.Error(Strings.EvalUsageTry); return Commands.Usage; }
                var m = Model(state, string.Join(' ', a.Words[1..^1]));
                var suite = string.Join(' ', a.Words.Skip(2));
                if (m == null) { Out.Error(Strings.EvalNoModelMatches(string.Join(' ', a.Words[1..^1]))); return Commands.NotFound; }
                var req = new ActionRequest { Action = "eval.try", Arg = m.Id, Arg2 = suite };
                var res = await s.Remote!.SendAsync(req).ConfigureAwait(false);
                o.WriteLine(res.Ok ? $"{Out.Green(Strings.EvalTry)} {m.Name} · {res.Message}" : Out.Red(res.Message));
                break;
            }
            case "all":
            {
                // /eval all <Suite> – die Suite an alle aktiven Modelle (wie „Send suite to all models")
                if (a.Words.Count < 2) { Out.Error(Strings.EvalUsageAll); return Commands.Usage; }
                var ids = state.Models.Where(x => x.Enabled).Select(x => x.Id).ToList();
                if (ids.Count == 0) { o.WriteLine(Out.Dim(Strings.EvalNoModels)); break; }
                var req = new ActionRequest { Action = "eval.enqueue", Number = 1, Ids = ids };
                req.Values["suites"] = string.Join(' ', a.Words.Skip(1));
                var res = await s.Remote!.SendAsync(req).ConfigureAwait(false);
                o.WriteLine(res.Ok ? Out.Green($"{Strings.EvalSendAll}: {res.Message}") : Out.Red(res.Message));
                break;
            }
            case "spread":
            {
                // /eval spread <Suite> – an alle Modelle auf allen PCs, jeweils dort, wo das Modell liegt (Nodes, N3)
                if (a.Words.Count < 2) { Out.Error(Strings.EvalUsageSpread); return Commands.Usage; }
                var server = s.Server!;
                var self = NodeStateJson.Self(server, server.Access.Url);
                var files = NodeScheduler.Files(self, server.Nodes.List);
                if (files.Count == 0) { o.WriteLine(Out.Dim(Strings.EvalNoModels)); break; }
                var req = new ActionRequest { Action = "eval.distribute", Number = 1, Ids = files };
                req.Values["suites"] = string.Join(' ', a.Words.Skip(1));
                var res = await s.Remote!.SendAsync(req).ConfigureAwait(false);
                o.WriteLine(res.Ok ? Out.Green($"{Strings.EvalSpread}: {res.Message}") : Out.Red(res.Message));
                break;
            }
            default:
                Out.Error(Strings.EvalUsage);
                return Commands.Usage;
        }
        return Commands.Ok;
    }

    private static RemoteEvalModel? Model(EvalState state, string query) =>
        state.Models.FirstOrDefault(m => m.Id == query || m.Name == query)
        ?? state.Models.FirstOrDefault(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

    // Eigene Tests wie im Fenster und im Web: Suiten mit ihren Aufgaben. Bearbeiten bleibt dort (JSON im Datenordner),
    // in der TUI stehen Ausprobieren und „an alle Modelle".
    private static void PrintCatalog(Session s, EvalState state, string? suite)
    {
        var o = Console.Out;
        var suites = EvalSuites.All(s.Engine.Paths.EvalSuitesDir);
        if (suites.Count == 0) { o.WriteLine(Out.Dim(Strings.EvalNoSuites)); return; }
        if (suite == null)
        {
            o.WriteLine(Out.Bold(Strings.EvalCatalog));
            Out.Table(new[] { Strings.EvalSuite, Strings.ColTasks, Strings.ColKind, Strings.ColDescription }, suites.Select(x => (IReadOnlyList<string>)new[]
            {
                Out.Cut(x.Name, 24), x.Tasks.Count.ToString(Strings.Inv),
                EvalSuites.IsBuiltIn(x) ? Out.Dim(Strings.EvalKindBuiltIn) : Out.Green(Strings.EvalKindOwn), x.Description,
            }), o);
            o.WriteLine(Out.Dim("  " + Strings.EvalCatalogHintTui));
            return;
        }
        var cur = suites.FirstOrDefault(x => x.Name.Equals(suite, StringComparison.OrdinalIgnoreCase));
        if (cur == null) { Out.Error(Strings.EvalNoSuite(suite)); return; }
        o.WriteLine(Out.Bold(cur.Name) + Out.Dim("   ·   " + Strings.EvalTasksCount(cur.Tasks.Count) + "   ·   " + cur.Description));
        Out.Table(new[] { Strings.EvalTaskNo, Strings.EvalCategory, Strings.EvalTitle2, Strings.EvalCheck, Strings.EvalMaxTokens }, cur.Tasks.Select((t, i) => (IReadOnlyList<string>)new[]
        {
            (i + 1).ToString(Strings.Inv), Out.Cut(t.Category, 12), Out.Cut(t.Title, 52), t.Check.Type, t.MaxTokens.ToString(Strings.Inv),
        }), o);
    }

    private static string StateColor(string state) => state switch
    {
        "done" => Out.Green("done"),
        "running" or "starting" => Out.Cyan(state),
        "failed" => Out.Red("failed"),
        "cancelled" => Out.Yellow("cancelled"),
        "skipped" => Out.Dim("skipped"),
        _ => Out.Dim(state),
    };
}
