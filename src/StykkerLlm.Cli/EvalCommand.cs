using System.Globalization;
using System.Text.Json;
using StykkerLlm.Core;
using StykkerLlm.Core.Eval;

namespace StykkerLlm.Cli;

// stykker eval <server|port|url>  – Test-Suite gegen ein Modell laufen lassen und bewerten
// stykker eval results [N]        – die letzten N Läufe vergleichen (--md datei: als Markdown speichern)
// stykker eval suites             – vorhandene Suiten (eingebaut + eigene im Datenordner unter eval\)
internal static class EvalCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task<int> RunAsync(CliArgs a, CancellationToken ct)
    {
        var paths = a.DataDir != null ? new AppPaths(Path.GetFullPath(a.DataDir)) : AppPaths.Default();
        var first = a.Words.FirstOrDefault() ?? "";
        if (first.Length == 0) { Out.Error("eval what? a running server (name or port), a URL, 'results' or 'suites'"); return Commands.Usage; }
        if (first.Equals("suites", StringComparison.OrdinalIgnoreCase)) return Suites(paths);
        if (first.Equals("results", StringComparison.OrdinalIgnoreCase)) return Results(a, paths);
        if (first.Equals("models", StringComparison.OrdinalIgnoreCase)) return Models(a, paths);

        var suite = PickSuite(a, paths);
        if (suite == null) return Commands.NotFound;

        // Ziel: URL direkt, sonst ein laufender Server (lesend erkannt, stört die offene App nicht)
        string url, name, modelFile = "", settings = "", gpu = "", machine = ""; int ctxLimit = 0; SecretValue? key = null; string? model = null;
        var target = string.Join(' ', a.Words);
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = target.TrimEnd('/'); if (url.EndsWith("/v1")) url = url[..^3];
            name = url;
            // Entferntes Ziel: Modellpfad, ctx und Slots stehen nur in dessen /props. Ohne diese Angaben
            // ließe sich ein Lauf später nicht zuordnen und nicht mit dem eigenen Rechner vergleichen.
            var probe = await EvalTarget.ProbeAsync(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, url, ct).ConfigureAwait(false);
            machine = probe.Machine;
            modelFile = probe.ModelFile;
            settings = probe.Settings;
            if (string.IsNullOrEmpty(model)) model = probe.Model;
        }
        else
        {
            using var s = new Session(a, write: false);
            await s.TickAsync(2, ct).ConfigureAwait(false);
            var (server, code) = Commands.FindServer(s.Engine, target);
            if (server == null) return code;
            url = server.Url; name = server.Name; key = server.Info.ApiKey;
            ctxLimit = server.Slots.FirstOrDefault()?.CtxMax ?? 0;
            if (ctxLimit == 0 && server.Props?.NCtx is int n and > 0) ctxLimit = n;
            if (server.Kind != BackendKind.LlamaCpp) model = server.Models.FirstOrDefault()?.Name;
            // Für die Modell-Übersicht: Datei (stabiler Schlüssel), Kontext/Slots, GPU
            modelFile = server.Info.Params?.Model ?? server.Props?.ModelPath ?? model ?? "";
            if (modelFile.Length > 0) modelFile = Path.GetFileName(modelFile);
            settings = $"ctx {ctxLimit} · slots {Math.Max(1, server.Slots.Count)}";
            gpu = s.Engine.Gpu?.Name ?? "";
        }

        var cats = a.Only?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tasks = suite.Tasks.Where(t => cats == null || cats.Contains(t.Category, StringComparer.OrdinalIgnoreCase)).ToList();
        if (tasks.Count == 0) { Out.Error("no tasks left (check --only)"); return Commands.Usage; }

        // Von Modellen geschriebenen Code nur nach Rückfrage ausführen
        PythonRunner? python = null, checks = null;
        // pycheck prüft nur den Antworttext mit unseren eigenen Tests: Python ja, Rückfrage nein
        if (tasks.Any(t => t.Check.Type.Equals("pycheck", StringComparison.OrdinalIgnoreCase)))
        {
            checks = PythonRunner.Find();
            if (checks == null) Out.Warn("no Python found – answer checks (pycheck) are skipped");
        }
        if (tasks.Any(t => t.Check.Type.Equals("python", StringComparison.OrdinalIgnoreCase) || t.Check.Type.Equals("agent", StringComparison.OrdinalIgnoreCase)))
        {
            python = PythonRunner.Find();
            if (python == null) Out.Warn("no Python found – coding tasks are skipped (install Python 3 or put it on PATH)");
            else if (!await new ConsolePrompt(a.Yes).ConfirmAsync("Run model-written code?",
                         $"Coding tasks run the model's Python code with tests on this PC ({python.Executable}),\n" +
                         "in a temporary folder with a time limit. Agent tasks let the model run commands there. It is not a sandbox.", warning: true).ConfigureAwait(false))
            {
                python = null;
                Console.Error.WriteLine(Out.Dim("coding tasks are skipped"));
            }
        }

        Console.Out.WriteLine($"{Out.Bold("eval")} {name}  ·  suite {suite.Name} v{suite.Version}  ·  {tasks.Count} tasks{(ctxLimit > 0 ? $"  ·  ctx {ctxLimit:N0}" : "")}" +
                              (a.Repeat > 1 ? $"  ·  {a.Repeat} runs" : ""));
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var runs = new List<EvalRun>();
        var group = Guid.NewGuid();
        int baseSeed = suite.Seed;
        // Wiederholungen mit wechselndem Seed: zeigt, wie stabil ein Modell ist (Streuung in "eval models")
        for (int rep = 1; rep <= a.Repeat && !ct.IsCancellationRequested; rep++)
        {
            suite.Seed = baseSeed + (rep - 1) * 1000;
            if (a.Repeat > 1) Console.Out.WriteLine(Out.Dim($"run {rep}/{a.Repeat}  ·  seed {suite.Seed}"));
            int i = 0;
            var run = await EvalRunner.RunAsync(http, url, suite, new EvalOptions
            {
                Categories = cats, Python = python, PythonChecks = checks, ApiKey = key, Model = model, ContextLimit = ctxLimit,
                Progress = (t, r) =>
                {
                    if (r == null) { i++; Console.Out.Write($"  [{i,2}/{tasks.Count}] {Out.Pad(t.Category, 9)} {Out.Pad(Out.Cut(t.Title, 26), 26)} "); return; }
                    var mark = r.Skipped ? Out.Dim("skip") : r.Error ? Out.Red("err") : r.Passed ? Out.Green(Out.Unicode ? "✓ ok" : "ok") : r.Score > 0 ? Out.Yellow("½") : Out.Red(Out.Unicode ? "✗" : "x");
                    var stats = r.Skipped || r.Error ? "" : Out.Dim($"{r.Seconds,5:0.0} s  {r.GenTokens,5} tok  {r.GenTps,5:0} t/s");
                    Console.Out.WriteLine($"{Out.Pad(mark, 5)} {stats}{(r.Note.Length > 0 && !r.Passed ? "  " + Out.Dim(Out.Cut(r.Note, 60)) : "")}");
                },
            }, ct).ConfigureAwait(false);
            run.Server = name; run.ModelFile = modelFile; run.Settings = settings; run.Gpu = gpu;
            run.Machine = machine.Length > 0 ? machine : EvalTarget.MachineOf(url);
            run.Repeat = rep; run.Group = group;
            if (string.IsNullOrEmpty(run.Model)) run.Model = name;
            if (run.Tasks.Count > 0)
            {
                var file = EvalSuites.Save(paths.EvalResultsDir, run);
                Console.Out.WriteLine(Out.Dim($"saved: {file}"));
            }
            runs.Add(run);
            if (run.Cancelled) break;
        }
        suite.Seed = baseSeed;

        Console.Out.WriteLine();
        PrintSummary(runs);
        if (runs.Count > 1)
        {
            var totals = runs.Select(r => r.Total).ToList();
            double mean = totals.Average(), sd = Math.Sqrt(totals.Sum(t => (t - mean) * (t - mean)) / (totals.Count - 1));
            Console.Out.WriteLine($"{Out.Bold("mean")} {mean:0} %  ±{sd:0}  over {runs.Count} runs");
        }
        if (a.Md != null) { File.WriteAllText(a.Md, EvalExport.Markdown(runs)); Console.Out.WriteLine(Out.Dim($"markdown: {a.Md}")); }
        if (a.Json) Console.Out.WriteLine(JsonSerializer.Serialize(runs, new JsonSerializerOptions { WriteIndented = true }));
        return runs.Any(r => r.Cancelled) ? Commands.Cancelled : Commands.Ok;
    }

    private static EvalSuite? PickSuite(CliArgs a, AppPaths paths)
    {
        if (a.Suite == null) return EvalSuites.BuiltIn();
        if (File.Exists(a.Suite)) return EvalSuites.Load(a.Suite);
        if (EvalSuites.BuiltInNames.Contains(a.Suite.ToLowerInvariant())) return EvalSuites.BuiltIn(a.Suite.ToLowerInvariant());
        var s = EvalSuites.All(paths.EvalSuitesDir).FirstOrDefault(x => x.Name.Equals(a.Suite, StringComparison.OrdinalIgnoreCase));
        if (s == null) Out.Error($"no suite '{a.Suite}' (see 'stykker eval suites')");
        return s;
    }

    private static int Suites(AppPaths paths)
    {
        var rows = EvalSuites.All(paths.EvalSuitesDir).Select(s => (IReadOnlyList<string>)new[]
        {
            s.Name, $"v{s.Version}", s.Tasks.Count.ToString(Inv),
            string.Join(", ", s.Tasks.GroupBy(t => t.Category).Select(g => $"{g.Key} {g.Count()}")),
        });
        Out.Table(new[] { Strings.EvalSuite, Strings.ColVersion, Strings.ColTasks, Strings.ColCategories }, rows);
        Console.Out.WriteLine(Out.Dim($"own suites: {paths.EvalSuitesDir}\\*.json (same format as the built-in one)"));
        return Commands.Ok;
    }

    private static int Results(CliArgs a, AppPaths paths)
    {
        int n = a.Words.Count > 1 && int.TryParse(a.Words[1], out var k) ? Math.Clamp(k, 1, 12) : 5;
        var runs = EvalSuites.LoadRuns(paths.EvalResultsDir).Select(x => x.Run).Take(n).Reverse().ToList();
        if (runs.Count == 0) { Console.Out.WriteLine("no eval runs yet – try 'stykker eval <server>'"); return Commands.Ok; }
        PrintSummary(runs);
        if (a.Md != null) { File.WriteAllText(a.Md, EvalExport.Markdown(runs)); Console.Out.WriteLine(Out.Dim($"markdown: {a.Md}")); }
        return Commands.Ok;
    }

    // Eine Zeile je Modell: Mittel über alle Läufe derselben Suite, Streuung, Kategorien, Geschwindigkeit
    private static int Models(CliArgs a, AppPaths paths)
    {
        var suite = PickSuite(a, paths);
        if (suite == null) return Commands.NotFound;
        var runs = EvalSuites.LoadRuns(paths.EvalResultsDir).Select(x => x.Run).ToList();
        var sums = EvalOverview.Summarize(runs, suite.Name, suite.Version);
        if (sums.Count == 0) { Console.Out.WriteLine($"no runs for suite {suite.Name} v{suite.Version} yet – try 'stykker eval <server>'"); return Commands.Ok; }
        var cats = sums.SelectMany(s => s.Categories.Keys).Distinct().OrderBy(EvalSuites.CategoryOrder).ThenBy(c => c).ToList();
        var header = new List<string> { Strings.ColModel, Strings.EvalMachine, Strings.EvalTotal, Strings.ColRuns };
        header.AddRange(cats.Select(c => c.ToUpperInvariant()));
        header.AddRange(new[] { Strings.BenchSeriesGen, Strings.BenchSeriesPrompt, Strings.EvalTime });
        var rows = sums.Select(s =>
        {
            var row = new List<string> { Out.Cut(s.Model, 28), Out.Cut(s.Machine.Length > 0 ? s.Machine : "–", 18), Out.Bold(Color(s.Total, s.Runs > 1 ? $"{s.Total:0} ±{s.TotalSd:0}" : $"{s.Total:0}")), s.Runs.ToString(Inv) };
            row.AddRange(cats.Select(c => s.Categories.TryGetValue(c, out var v) ? Color(v, $"{v:0}") : "–"));
            row.AddRange(new[] { $"{s.GenTps:0}", s.PromptTps > 0 ? $"{s.PromptTps:0}" : "–", $"{s.Seconds:0} s" });
            return (IReadOnlyList<string>)row;
        });
        Console.Out.WriteLine($"{Out.Bold("models")}  ·  suite {suite.Name} v{suite.Version}  ·  percent per category, mean over runs (± spread)");
        Out.Table(header, rows);
        if (a.Md != null)
        {
            var sb = new System.Text.StringBuilder($"# Models – suite {suite.Name} v{suite.Version}\n\n| Model | Total | Runs | " + string.Join(" | ", cats) + " | Gen t/s | Prompt t/s |\n|---|---|---|" + string.Concat(cats.Select(_ => "---|")) + "---|---|\n");
            foreach (var s in sums)
                sb.Append($"| {s.Model} | **{s.Total:0}**{(s.Runs > 1 ? $" ±{s.TotalSd:0}" : "")} | {s.Runs} | " + string.Join(" | ", cats.Select(c => s.Categories.TryGetValue(c, out var v) ? v.ToString("0", Inv) : "–")) + $" | {s.GenTps:0} | {s.PromptTps:0} |\n");
            File.WriteAllText(a.Md, sb.ToString());
            Console.Out.WriteLine(Out.Dim($"markdown: {a.Md}"));
        }
        return Commands.Ok;
    }

    // Kategorien × Läufe; Gesamtnote fett
    private static void PrintSummary(IReadOnlyList<EvalRun> runs)
    {
        var cats = runs.SelectMany(r => r.Categories.Select(c => c.Category)).Distinct().OrderBy(EvalSuites.CategoryOrder).ThenBy(c => c).ToList();
        var header = new List<string> { "" };
        // Bei Läufen von verschiedenen Rechnern ist der Modellname allein nicht genug: die Maschine
        // kommt in die Kopfzeile, sonst sind zwei Spalten nicht unterscheidbar.
        bool machines = runs.Select(r => r.Machine).Distinct().Count() > 1;
        header.AddRange(runs.Select(r => Out.Cut(r.Title, machines ? 14 : 24)));
        var rows = new List<IReadOnlyList<string>>();
        if (machines)
        {
            var who = new List<string> { Strings.EvalMachine.ToLowerInvariant() };
            who.AddRange(runs.Select(r => Out.Dim(Out.Cut(r.Machine, 18))));
            rows.Add(who);
        }
        foreach (var c in cats)
        {
            var row = new List<string> { c };
            row.AddRange(runs.Select(r => r.Categories.FirstOrDefault(x => x.Category == c) is { Count: > 0 } x
                ? Color(x.Percent, $"{x.Percent,3:0} %  ({x.Passed}/{x.Count})") : "–"));
            rows.Add(row);
        }
        var total = new List<string> { Out.Bold(Strings.RowTotal) };
        total.AddRange(runs.Select(r => Out.Bold(Color(r.Total, $"{r.Total,3:0} %"))));
        rows.Add(total);
        var time = new List<string> { Out.Dim(Strings.EvalTime.ToLowerInvariant()) };
        time.AddRange(runs.Select(r => Out.Dim($"{r.DurationSec:0} s · {r.Started:MM-dd HH:mm}")));
        rows.Add(time);
        Out.Table(header, rows);
    }

    private static string Color(double pct, string text) => pct >= 80 ? Out.Green(text) : pct >= 50 ? Out.Yellow(text) : Out.Red(text);
}
