using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StykkerLlm.Core.Eval;

public sealed class EvalOptions
{
    public IReadOnlyCollection<string>? Categories { get; set; }   // null = alle
    public PythonRunner? Python { get; set; }                     // null = Coding-Aufgaben (Modell-Code) werden übersprungen
    public PythonRunner? PythonChecks { get; set; }               // für pycheck (prüft nur den Antworttext, ohne Zustimmung); null = Python
    public SecretValue? ApiKey { get; set; }
    public string? Model { get; set; }                            // "model"-Feld der Anfrage (Proxy/Ollama); null = "local"
    public int ContextLimit { get; set; }                         // Kontext je Slot; 0 = unbekannt (dann nichts überspringen)
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public Action<EvalTask, EvalTaskResult?>? Progress { get; set; }   // vor (Ergebnis null) und nach jeder Aufgabe
}

// Führt eine Suite gegen einen OpenAI-kompatiblen Server aus (/v1/chat/completions, ohne Stream) und bewertet jede Antwort.
public static class EvalRunner
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task<EvalRun> RunAsync(HttpClient http, string baseUrl, EvalSuite suite, EvalOptions opt, CancellationToken ct)
    {
        var run = new EvalRun
        {
            Started = DateTime.Now, Url = baseUrl, Suite = suite.Name, SuiteVersion = suite.Version, Temperature = suite.Temperature,
            Model = opt.Model ?? "", Seed = suite.Seed, Machine = EvalTarget.MachineOf(baseUrl),
        };
        var sw = Stopwatch.StartNew();
        foreach (var task in suite.Tasks)
        {
            if (opt.Categories is { Count: > 0 } cats && !cats.Contains(task.Category, StringComparer.OrdinalIgnoreCase)) continue;
            if (ct.IsCancellationRequested) { run.Cancelled = true; break; }
            opt.Progress?.Invoke(task, null);
            EvalTaskResult r;
            try { r = await RunTaskAsync(http, baseUrl, suite, task, opt, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { run.Cancelled = true; break; }
            catch (Exception ex) { r = Base(task); r.Error = true; r.Note = "request failed: " + EvalGrader.Short(ex.Message, 100); }
            run.Tasks.Add(r);
            opt.Progress?.Invoke(task, r);
        }
        run.DurationSec = sw.Elapsed.TotalSeconds;
        return run;
    }

    private static EvalTaskResult Base(EvalTask t) => new() { Id = t.Id, Category = t.Category, Title = t.Title, Points = t.Points };

    public const string AgentSystem =
        "You are a coding agent working in a folder on the user's machine. Use the tools to look around, read and change files " +
        "and run commands (cmd by default, or powershell). Paths are relative to the working folder. Check your work by running it. " +
        "When you are done, answer with one short sentence.";

    // Agent-Aufgabe: Wegwerf-Ordner mit den Dateien der Aufgabe, der Mini-Harness mit Werkzeugen (ohne Rückfrage – die
    // Zustimmung ist die Erlaubnis für Modell-Code), danach prüfen die Tests den Ordner. Der Ordner wird danach gelöscht.
    private static async Task<EvalTaskResult> RunAgentTaskAsync(HttpClient http, string baseUrl, EvalSuite suite, EvalTask task, EvalOptions opt, EvalTaskResult r, CancellationToken ct)
    {
        if (opt.Python == null)
        {
            r.Skipped = true; r.Note = "skipped: agent tasks run commands – allow running model-written code (and Python must be found)";
            return r;
        }
        var ws = Path.Combine(Path.GetTempPath(), "stykker-agent-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(ws);
        try
        {
            foreach (var (rel, content) in task.Files)
            {
                var full = Path.GetFullPath(Path.Combine(ws, rel));
                if (!full.StartsWith(ws, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
            }
            var session = new PromptSession();
            session.Options.System = task.System ?? AgentSystem;
            session.Options.Temperature = suite.Temperature;
            session.Options.MaxTokens = task.MaxTokens;
            session.Options.Model = opt.Model ?? "";
            session.Options.ApiKey = opt.ApiKey?.Reveal();
            session.Options.Tools = new PromptTools(ws);
            session.Options.AutoApprove = true;
            var sw = Stopwatch.StartNew();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(opt.RequestTimeout);
            var res = await session.SendAsync(http, baseUrl, task.Prompt, ct: cts.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            r.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
            r.Rounds = session.Turns.Count(t => t.Role == "assistant");
            r.GenTokens = res.CompletionTokens;
            r.PromptTokens = res.PromptTokens;
            r.GenTps = res.Tps;
            r.FinishReason = res.Finish;
            var steps = string.Join("; ", session.ToolLog.Select(o => o.Call.Summary.Length > 80 ? o.Call.Summary[..80] + "…" : o.Call.Summary));
            r.Answer = EvalGrader.Short((steps.Length > 0 ? "[" + steps + "] " : "") + res.Text, 2000);
            // Kontext des Servers zu klein für die Werkzeugrunden: eine Grenze des Servers, kein Fehler des Modells
            // (wie bei den Langkontext-Aufgaben: nicht bewertet)
            if (!res.Ok && (res.Error ?? "").Contains("context", StringComparison.OrdinalIgnoreCase) && (res.Error ?? "").Contains("exceed", StringComparison.OrdinalIgnoreCase))
            {
                r.Skipped = true; r.Note = "skipped: the server's context is too small for this agent task (" + EvalGrader.Short(res.Error ?? "", 80) + ")";
                return r;
            }
            if (!res.Ok && session.ToolLog.Count == 0 && res.Text.Length == 0)
            {
                // Server- oder Verbindungsfehler: kein Fehler des Modells
                r.Error = true; r.Note = EvalGrader.Short(res.Error ?? "", 120);
                return r;
            }
            // Python läuft isoliert (-I): der Ordner kommt erst über sys.path in den Suchpfad (import der Dateien der Aufgabe)
            var code = "import os, sys\nos.chdir(" + JsonSerializer.Serialize(ws) + ")\nsys.path.insert(0, os.getcwd())\n" + (task.Check.Tests ?? "");
            var (exit, output) = await opt.Python.RunAsync(code, TimeSpan.FromSeconds(Math.Max(5, task.Check.TimeoutSec)), ct).ConfigureAwait(false);
            r.Score = exit == 0 ? 1 : 0;
            r.Note = exit == 0 ? $"{session.ToolLog.Count} tool calls" : EvalGrader.Short(LastLine(output) + (res.Ok ? "" : " · " + res.Error), 160);
            return r;
        }
        finally
        {
            // STYKKER_EVAL_KEEP=1: Ordner stehen lassen, um nachzusehen, was das Modell getan hat
            if (Environment.GetEnvironmentVariable("STYKKER_EVAL_KEEP") == "1") r.Note += " · kept " + ws;
            else try { Directory.Delete(ws, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string LastLine(string s) => s.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";

    // Prompt mit Fülltext (langer Kontext): {needle} an Position NeedleAt, danach die eigentliche Frage
    public static string BuildPrompt(EvalTask t, int seed)
    {
        if (t.FillerTokens <= 0) return t.Prompt;
        var text = BenchmarkRunner.Filler((int)(t.FillerTokens * 4.2), seed);
        // Fakten von hinten nach vorn einsetzen, damit die Positionen der vorderen gültig bleiben
        var facts = new List<(string Text, double At)>();
        if (!string.IsNullOrEmpty(t.Needle)) facts.Add((t.Needle, t.NeedleAt));
        for (int i = 0; i < t.Needles.Count; i++) facts.Add((t.Needles[i], i < t.NeedlesAt.Count ? t.NeedlesAt[i] : (i + 1.0) / (t.Needles.Count + 1)));
        foreach (var (fact, pos) in facts.OrderByDescending(f => f.At))
        {
            int at = (int)(text.Length * Math.Clamp(pos, 0, 1));
            at = text.LastIndexOf('\n', Math.Max(0, Math.Min(text.Length - 1, at - 1))) + 1;
            text = text[..at] + fact + "\n" + text[at..];
        }
        return text + "\n\n" + t.Prompt;
    }

    private static async Task<EvalTaskResult> RunTaskAsync(HttpClient http, string baseUrl, EvalSuite suite, EvalTask task, EvalOptions opt, CancellationToken ct)
    {
        var r = Base(task);
        if (task.Check.Type.Equals("agent", StringComparison.OrdinalIgnoreCase)) return await RunAgentTaskAsync(http, baseUrl, suite, task, opt, r, ct).ConfigureAwait(false);
        bool isCheck = task.Check.Type.Equals("pycheck", StringComparison.OrdinalIgnoreCase);
        if ((task.Check.Type.Equals("python", StringComparison.OrdinalIgnoreCase) && opt.Python == null) || (isCheck && (opt.PythonChecks ?? opt.Python) == null))
        {
            r.Skipped = true; r.Note = isCheck ? "skipped: no Python found for the answer checks" : "skipped: running model code was not allowed (or no Python found)";
            return r;
        }
        // Kontext: Fülltext + Frage + Reserve für die (kurze) Antwort samt Denkteil – nicht das volle max_tokens
        int need = task.FillerTokens + Math.Min(task.MaxTokens, 1024) + 512;
        if (opt.ContextLimit > 0 && task.FillerTokens > 0 && need > opt.ContextLimit)
        {
            r.Skipped = true; r.Note = $"skipped: needs ~{need:N0} tokens, server has {opt.ContextLimit:N0} per slot";
            return r;
        }

        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(task.System)) messages.Add(new JsonObject { ["role"] = "system", ["content"] = task.System });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = BuildPrompt(task, suite.Seed) });
        var sw = Stopwatch.StartNew();
        var allCalls = new List<EvalToolCall>();
        string content = "", finish = "";
        double tpsSum = 0; int tpsN = 0, lastGen = 0;
        // Eine Runde je Anfrage; ruft das Modell Werkzeuge auf und die Aufgabe hat feste Ergebnisse (Tool-Kette), geht es weiter
        for (int round = 1; ; round++)
        {
            var body = new JsonObject
            {
                ["model"] = opt.Model ?? "local", ["messages"] = JsonNode.Parse(messages.ToJsonString()), ["max_tokens"] = task.MaxTokens, ["stream"] = false,
                ["temperature"] = suite.Temperature, ["seed"] = suite.Seed,
            };
            if (task.Tools is { ValueKind: JsonValueKind.Array } tools) body["tools"] = JsonNode.Parse(tools.GetRawText());

            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/v1/chat/completions")
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            if (opt.ApiKey != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + opt.ApiKey.Reveal());
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(opt.RequestTimeout);
            using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            r.Seconds = sw.Elapsed.TotalSeconds;
            if (!resp.IsSuccessStatusCode) { r.Error = true; r.Note = $"HTTP {(int)resp.StatusCode}: {EvalGrader.Short(json, 100)}"; return r; }

            var (c, calls, promptTok, genTok, genTps, promptTps, fin) = ParseReply(json);
            content = c; finish = fin; r.Rounds = round; lastGen = genTok;
            r.PromptTokens += promptTok; r.GenTokens += genTok;
            if (round == 1) r.PromptTps = promptTps;
            if (genTps > 0) { tpsSum += genTps; tpsN++; }
            allCalls.AddRange(calls);
            if (calls.Count == 0 || task.ToolResults.Count == 0 || round >= Math.Max(1, task.MaxRounds)) break;
            // Antwort des Modells (mit Werkzeugaufrufen) und die festen Ergebnisse anhängen, dann die nächste Runde
            var tc = new JsonArray();
            for (int i = 0; i < calls.Count; i++)
            {
                var id = calls[i].Id.Length > 0 ? calls[i].Id : $"call_{round}_{i}";
                calls[i] = calls[i] with { Id = id };
                tc.Add(new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = calls[i].Name, ["arguments"] = calls[i].Arguments } });
            }
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = c, ["tool_calls"] = tc });
            foreach (var call in calls)
                messages.Add(new JsonObject
                {
                    ["role"] = "tool", ["tool_call_id"] = call.Id, ["name"] = call.Name,
                    ["content"] = task.ToolResults.TryGetValue(call.Name, out var res) ? res : "{\"error\": \"unknown tool\"}",
                });
        }
        var toolCalls = allCalls;
        r.FinishReason = finish;
        r.GenTps = tpsN > 0 ? tpsSum / tpsN : r.Seconds > 0 ? r.GenTokens / r.Seconds : 0;
        var visible = EvalGrader.StripThinking(content);
        r.Answer = visible.Length > 4000 ? visible[..4000] + "…" : visible;
        if (toolCalls.Count > 0) r.Answer = (r.Answer.Length > 0 ? r.Answer + "\n" : "") + string.Join("\n", toolCalls.Select(c => $"→ {c.Name}({c.Arguments})"));
        if (visible.Length == 0 && toolCalls.Count == 0 && (finish == "length" || (lastGen > 0 && lastGen >= task.MaxTokens)))
        {
            r.Note = $"no answer: hit max_tokens ({task.MaxTokens}) while thinking";
            return r;
        }
        (r.Score, r.Note) = await EvalGrader.GradeAsync(task, content, toolCalls, opt.Python, ct, opt.PythonChecks ?? opt.Python).ConfigureAwait(false);
        return r;
    }

    internal static (string Content, List<EvalToolCall> Tools, int PromptTok, int GenTok, double GenTps, double PromptTps, string Finish) ParseReply(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string content = "", finish = "";
        var tools = new List<EvalToolCall>();
        if (root.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0 &&
            ch[0].TryGetProperty("message", out var msg))
        {
            if (ch[0].TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) finish = fr.GetString() ?? "";
            if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String) content = c.GetString() ?? "";
            if (msg.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                foreach (var call in tc.EnumerateArray())
                    if (call.TryGetProperty("function", out var f))
                        tools.Add(new EvalToolCall(f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                            f.TryGetProperty("arguments", out var a) ? (a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : a.GetRawText()) : "",
                            call.TryGetProperty("id", out var cid) && cid.ValueKind == JsonValueKind.String ? cid.GetString() ?? "" : ""));
        }
        int pt = 0, gt = 0; double tps = 0, ptps = 0;
        if (root.TryGetProperty("usage", out var u))
        {
            if (u.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pi)) pt = pi;
            if (u.TryGetProperty("completion_tokens", out var g) && g.TryGetInt32(out var gi)) gt = gi;
        }
        if (root.TryGetProperty("timings", out var t))
        {
            if (t.TryGetProperty("predicted_per_second", out var ps) && ps.ValueKind == JsonValueKind.Number) tps = ps.GetDouble();
            if (t.TryGetProperty("prompt_per_second", out var pp) && pp.ValueKind == JsonValueKind.Number) ptps = pp.GetDouble();
        }
        return (content, tools, pt, gt, tps, ptps, finish);
    }
}

// Suiten laden (eingebaut + eigene im Datenordner), Läufe speichern und laden
public static class EvalSuites
{
    public const string BuiltInName = "basic";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static int CategoryOrder(string c) => c switch { "coding" => 0, "reasoning" => 1, "format" => 2, "tools" => 3, "context" => 4, "creative" => 5, "agent" => 6, _ => 9 };

    public static readonly string[] BuiltInNames = { "basic", "hard", "creative", "agent" };

    public static EvalSuite BuiltIn(string name = BuiltInName)
    {
        using var s = typeof(EvalSuites).Assembly.GetManifestResourceStream($"StykkerLlm.Core.Eval.suite-{name}.json")
            ?? throw new InvalidOperationException($"built-in suite '{name}' missing");
        return JsonSerializer.Deserialize<EvalSuite>(s, Json) ?? throw new InvalidDataException("built-in suite unreadable");
    }

    public static EvalSuite Load(string file) =>
        JsonSerializer.Deserialize<EvalSuite>(File.ReadAllText(file), Json) is { } s
            ? (s.Name.Length > 0 ? s : Rename(s, Path.GetFileNameWithoutExtension(file))) : throw new InvalidDataException(file);

    private static EvalSuite Rename(EvalSuite s, string name) { s.Name = name; return s; }

    // Eingebaute zuerst, dann alle lesbaren *.json im Ordner (unlesbare werden übergangen)
    public static List<EvalSuite> All(string? dir)
    {
        var list = BuiltInNames.Select(n => BuiltIn(n)).ToList();
        if (dir != null && Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "*.json").Order())
                try { list.Add(Load(f)); } catch { }
        return list;
    }

    public static bool IsBuiltIn(EvalSuite s) => BuiltInNames.Contains(s.Name, StringComparer.OrdinalIgnoreCase);

    // Eigene Suite speichern (Version +1: alte Ergebnisse bleiben getrennt); Name = Dateiname im Ordner eval\
    public static void SaveSuite(string dir, EvalSuite s)
    {
        if (IsBuiltIn(s)) throw new InvalidOperationException("built-in suites are read-only");
        Directory.CreateDirectory(dir);
        s.Version++;
        var opt = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(Path.Combine(dir, s.Name + ".json"), JsonSerializer.Serialize(s, opt), new UTF8Encoding(false));
    }

    // Gültiger Suite-Name aus einer Eingabe (Kleinbuchstaben, Ziffern, Bindestriche); null = leer oder schon vergeben
    public static string? NewSuiteName(string input, IEnumerable<EvalSuite> existing)
    {
        var name = string.Concat(input.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')).Trim('-');
        return name.Length == 0 || BuiltInNames.Contains(name) || existing.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? null : name;
    }

    public static string Save(string dir, EvalRun run)
    {
        Directory.CreateDirectory(dir);
        var safe = string.Concat((run.Title + "-" + run.Suite).Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '.' or '_' ? ch : '_'));
        var file = Path.Combine(dir, $"{run.Started:yyyyMMdd-HHmmss}-{safe}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(run, Json), new UTF8Encoding(false));
        return file;
    }

    // Ältere Läufe ergänzen, ohne die Dateien zu ändern: vor dem 2026-10-03 fehlte der Rechner (dieselbe Regel wie
    // bei neuen Läufen: Host aus der URL, bei Loopback dieser Rechner) – sonst stünde dasselbe Modell doppelt in der
    // Rangliste. Ein Modellname, der ein Dateipfad ist (CLI gegen eine URL), wird zum Dateinamen ohne Endung.
    public static EvalRun Normalize(EvalRun r)
    {
        if (string.IsNullOrWhiteSpace(r.Machine) && !string.IsNullOrWhiteSpace(r.Url)) r.Machine = EvalTarget.MachineOf(r.Url);
        if (r.Model.Contains('\\') || r.Model.Contains('/'))
            r.Model = Path.GetFileNameWithoutExtension(r.Model.Replace('\\', '/').Split('/').Last());
        return r;
    }

    public static List<(string File, EvalRun Run)> LoadRuns(string dir)
    {
        var list = new List<(string, EvalRun)>();
        if (!Directory.Exists(dir)) return list;
        // Unterordner nodes/<Name>/: Läufe gekoppelter Nodes, die der Hub eingesammelt hat (docs/nodes.md, N3)
        foreach (var f in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
            try { if (JsonSerializer.Deserialize<EvalRun>(File.ReadAllText(f), Json) is { } r) list.Add((f, Normalize(r))); } catch { }
        return list.OrderByDescending(x => x.Item2.Started).ToList();
    }
}

// Übersicht „alle Modelle": Läufe derselben Suite (Name + Version) je Modell **und Rechner** zusammengefasst
public sealed record EvalModelSummary(string Model, string Machine, string ModelFile, int Runs, double Total, double TotalSd,
    IReadOnlyDictionary<string, double> Categories, double GenTps, double PromptTps, double Seconds, DateTime Last, string Gpu);

// Eine Zeile der Rangliste (über eine Suite oder als Mittel über alle Suiten)
public sealed record EvalRankRow(string Model, string Machine, string ModelFile, double Total, double Spread, int Runs, IReadOnlyDictionary<string, double> Categories,
    double GenTps, double Seconds);

public static class EvalOverview
{
    // Rangliste für alle Oberflächen: suite = null → Mittel der Suiten-Noten je Modell (jeweils neueste Suite-Version)
    public static List<EvalRankRow> Rank(IReadOnlyList<EvalRun> runs, string? suite)
    {
        var names = runs.Select(r => r.Suite).Distinct().Where(s => suite == null || s == suite).ToList();
        var per = names.SelectMany(s => Summarize(runs, s, runs.Where(r => r.Suite == s).Max(r => r.SuiteVersion))).ToList();
        return per.GroupBy(x => (x.ModelFile.Length > 0 ? x.ModelFile : x.Model).ToLowerInvariant() + "|" + x.Machine.ToLowerInvariant()).Select(g =>
        {
            var cats = g.SelectMany(x => x.Categories).GroupBy(c => c.Key).ToDictionary(c => c.Key, c => c.Average(v => v.Value));
            return new EvalRankRow(g.First().Model, g.First().Machine, g.First().ModelFile, g.Average(x => x.Total), suite != null ? g.First().TotalSd : 0,
                g.Sum(x => x.Runs), cats, g.Average(x => x.GenTps), g.Sum(x => x.Seconds));
        }).OrderByDescending(r => r.Total).ToList();
    }

    // Neuester Lauf je Modell einer Suite (für die Aufgaben-Matrix)
    public static List<EvalRun> LatestPerModel(IReadOnlyList<EvalRun> runs, string suite)
    {
        var mine = runs.Where(r => r.Suite == suite && !r.Cancelled).ToList();
        if (mine.Count == 0) return new();
        int ver = mine.Max(r => r.SuiteVersion);
        return mine.Where(r => r.SuiteVersion == ver).GroupBy(Key).Select(g => g.OrderByDescending(r => r.Started).First()).OrderBy(r => r.Title).ToList();
    }

    // Schlüssel: Rechner + Modelldatei (stabil über Neustarts), sonst Modellname. Der Rechner gehört dazu,
// damit dasselbe Modell auf zwei Rechnern zwei Zeilen ergibt statt zu einer zu verschmelzen.
    public static string Key(EvalRun r) =>
        (string.IsNullOrEmpty(r.Machine) ? "" : r.Machine.ToLowerInvariant() + "|") +
        (r.ModelFile.Length > 0 ? r.ModelFile.ToLowerInvariant() : r.Title.ToLowerInvariant());

    public static List<EvalModelSummary> Summarize(IEnumerable<EvalRun> runs, string suite, int version)
    {
        var list = new List<EvalModelSummary>();
        foreach (var g in runs.Where(r => r.Suite == suite && r.SuiteVersion == version && !r.Cancelled && r.Tasks.Count > 0).GroupBy(Key))
        {
            var rs = g.OrderBy(r => r.Started).ToList();
            var totals = rs.Select(r => r.Total).ToList();
            double mean = totals.Average();
            double sd = totals.Count > 1 ? Math.Sqrt(totals.Sum(t => (t - mean) * (t - mean)) / (totals.Count - 1)) : 0;
            var cats = rs.SelectMany(r => r.Categories).GroupBy(c => c.Category).ToDictionary(c => c.Key, c => c.Average(x => x.Percent));
            var done = rs.SelectMany(r => r.Tasks).Where(t => !t.Skipped && !t.Error && t.GenTokens > 0).ToList();
            double Median(IEnumerable<double> xs) { var a = xs.Where(x => x > 0).OrderBy(x => x).ToList(); return a.Count == 0 ? 0 : a[a.Count / 2]; }
            var last = rs[^1];
            list.Add(new EvalModelSummary(last.Title, last.Machine, last.ModelFile, rs.Count, mean, sd, cats, Median(done.Select(t => t.GenTps)),
                Median(done.Select(t => t.PromptTps)), rs.Average(r => r.DurationSec), last.Started, last.Gpu));
        }
        return list.OrderByDescending(s => s.Total).ThenByDescending(s => s.GenTps).ToList();
    }
}

// Vergleich mehrerer Läufe als Markdown (Kategorien × Modelle, dann Aufgaben × Modelle)
public static class EvalExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Markdown(IReadOnlyList<EvalRun> runs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Model test suite").AppendLine();
        sb.AppendLine("| | " + string.Join(" | ", runs.Select(r => r.Title)) + " |");
        sb.AppendLine("|---|" + string.Concat(runs.Select(_ => "---|")));
        sb.AppendLine("| **machine** | " + string.Join(" | ", runs.Select(r => r.Machine.Length > 0 ? r.Machine : "–")) + " |");
        sb.AppendLine("| **Total** | " + string.Join(" | ", runs.Select(r => $"**{r.Total.ToString("0", Inv)}**")) + " |");
        var cats = runs.SelectMany(r => r.Categories.Select(c => c.Category)).Distinct().OrderBy(EvalSuites.CategoryOrder).ThenBy(c => c).ToList();
        foreach (var c in cats)
            sb.AppendLine($"| {c} | " + string.Join(" | ", runs.Select(r => r.Categories.FirstOrDefault(x => x.Category == c) is { Count: > 0 } x
                ? $"{x.Percent.ToString("0", Inv)} ({x.Passed}/{x.Count})" : "–")) + " |");
        sb.AppendLine("| time | " + string.Join(" | ", runs.Select(r => $"{r.DurationSec.ToString("0", Inv)} s")) + " |");
        sb.AppendLine().AppendLine("## Tasks").AppendLine();
        sb.AppendLine("| Task | " + string.Join(" | ", runs.Select(r => r.Title)) + " |");
        sb.AppendLine("|---|" + string.Concat(runs.Select(_ => "---|")));
        var ids = runs.SelectMany(r => r.Tasks.Select(t => (t.Id, t.Title, t.Category))).Distinct().ToList();
        foreach (var (id, title, cat) in ids)
            sb.AppendLine($"| {cat}: {title} | " + string.Join(" | ", runs.Select(r => r.Tasks.FirstOrDefault(t => t.Id == id) is { } t
                ? (t.Skipped ? "skipped" : t.Error ? "error" : t.Passed ? "✅" : t.Score > 0 ? $"½ {Esc(t.Note)}" : $"❌ {Esc(t.Note)}") : "–")) + " |");
        return sb.ToString();
    }

    private static string Esc(string s) => EvalGrader.Short(s, 50).Replace("|", "\\|");
}
