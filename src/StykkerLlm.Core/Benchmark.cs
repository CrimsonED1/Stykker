using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StykkerLlm.Core;

// ───────────── Ergebnisse ─────────────

public sealed class BenchStep
{
    public string Kind { get; set; } = "";            // chat | context | tool | parallel
    public string Name { get; set; } = "";
    public int TargetPromptTokens { get; set; }
    public int PromptTokens { get; set; }
    public double PromptSec { get; set; }
    public double PromptTps { get; set; }
    public int GenTokens { get; set; }
    public double GenSec { get; set; }
    public double GenTps { get; set; }
    public double TotalSec { get; set; }
    public bool Ok { get; set; }
    public string Note { get; set; } = "";
    public int Parallel { get; set; }                 // Anzahl gleichzeitiger Anfragen (nur "parallel")
    public double AggregateTps { get; set; }          // Summe der Token aller parallelen Anfragen durch die Wanduhrzeit
    public int Runs { get; set; } = 1;
}

public sealed class BenchResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Started { get; set; }
    public double DurationSec { get; set; }
    public string Server { get; set; } = "";
    public string ServerKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string ModelFile { get; set; } = "";
    public string ProfileKey { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string Quant { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string Parameters { get; set; } = "";
    public double ModelSizeGb { get; set; }
    public string Settings { get; set; } = "";        // z. B. "ctx 65536 · ngl 99 · q4_0/q4_0 · fa on"
    public string Gpu { get; set; } = "";
    public string Driver { get; set; } = "";          // GPU-Treiberversion
    public string Cpu { get; set; } = "";             // CPU-Kennung (Hardware-Fingerprint)
    public string Build { get; set; } = "";
    public int ContextPerSlot { get; set; }
    public int Slots { get; set; } = 1;
    public int Seed { get; set; }
    public string? RecordingFile { get; set; }
    public bool Cancelled { get; set; }
    // Rückgang der Erzeugungsgeschwindigkeit gegenüber dem früheren Lauf derselben Konfiguration (bleibt am Ergebnis stehen)
    public BenchRegression? Regression { get; set; }
    public List<BenchStep> Steps { get; set; } = new();

    public string Title => !string.IsNullOrEmpty(ProfileName) ? ProfileName : Server;
}

// Ein auffälliger Rückgang der Erzeugungsgeschwindigkeit gegenüber einem früheren Lauf derselben Konfiguration.
// Wie die übrigen gespeicherten Modelle eine Klasse mit Setzern, damit library.json sie ohne Umwege liest.
public sealed class BenchRegression
{
    public double Previous { get; set; }
    public double Current { get; set; }
    public double DropPct { get; set; }

    public BenchRegression() { }

    public BenchRegression(double previous, double current, double dropPct)
    {
        Previous = previous;
        Current = current;
        DropPct = dropPct;
    }
}

public sealed class BenchOptions
{
    public static readonly int[] Ladder = { 1024, 2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144 };

    public bool Chat { get; set; } = true;
    public bool Tool { get; set; } = true;
    public bool Parallel { get; set; } = true;
    public int[] ContextSizes { get; set; } = Ladder;
    public int Repeats { get; set; } = 1;
    public int GenTokens { get; set; } = 128;
    public int Seed { get; set; } = 42;
    public int ContextPerSlot { get; set; } = 4096;
    public int Slots { get; set; } = 1;
    public SecretValue? ApiKey { get; set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(15);

    // Welche Kontextgrößen passen in einen Slot (Prompt + Antwort + Reserve)?
    public IEnumerable<int> UsableSizes() => ContextSizes.Where(s => s + GenTokens + 192 <= ContextPerSlot).OrderBy(s => s);
}

// ───────────── Durchführung ─────────────

// Feste Testreihe gegen einen OpenAI-kompatiblen llama-server: kurzer Chat, Prompt-Verarbeitung und Erzeugung bei wachsendem Kontext,
// Werkzeugaufruf, optional parallele Anfragen. Feste Prompts, cache_prompt:false, Seed und Temperatur 0: wiederholbar.
// Es werden nur Zahlen ausgewertet; die Antworttexte werden nicht gespeichert.
public static class BenchmarkRunner
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] Words =
    {
        "river", "stone", "market", "engine", "garden", "signal", "window", "forest", "copper", "harbor", "ladder", "meadow", "pencil", "rocket", "valley", "winter",
        "bridge", "candle", "dragon", "fabric", "glacier", "hammer", "island", "jungle", "kettle", "lantern", "mirror", "needle", "orchard", "planet", "quartz", "ribbon",
        "saddle", "tunnel", "violin", "walnut", "yellow", "zephyr", "anchor", "button", "castle", "desert", "elbow", "feather", "guitar", "helmet", "insect", "jacket",
    };

    // CPU-Kennung für den Hardware-Fingerprint (ohne Plattform-Abhängigkeit): Prozessor-Identifikation plus Kernzahl
    public static string CpuName()
    {
        string id = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
        return string.IsNullOrWhiteSpace(id) ? $"{Environment.ProcessorCount}-core CPU" : $"{id} ({Environment.ProcessorCount} cores)";
    }

    // Immer derselbe Text für dieselben (Länge, Seed): einfache Zufallsfolge aus einer festen Wortliste
    public static string Filler(int chars, int seed)
    {
        var sb = new StringBuilder(chars + 64);
        uint state = (uint)seed * 2654435761u + 12345u;
        int line = 1;
        while (sb.Length < chars)
        {
            sb.Append("Note ").Append(line++).Append(": ");
            for (int i = 0; i < 9; i++)
            {
                state = state * 1664525u + 1013904223u;
                sb.Append(Words[(int)((state >> 16) % (uint)Words.Length)]).Append(i < 8 ? ' ' : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString(0, chars);
    }

    private static string Url(string baseUrl, string path) => baseUrl.TrimEnd('/') + path;

    private static HttpRequestMessage Post(string baseUrl, string path, string json, SecretValue? key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Url(baseUrl, path)) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (key != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key.Reveal());
        return req;
    }

    // Wie viele Token hat ein Text? /tokenize des Servers; null, wenn nicht verfügbar
    public static async Task<int?> CountTokensAsync(HttpClient http, string baseUrl, SecretValue? key, string text, CancellationToken ct)
    {
        try
        {
            using var req = Post(baseUrl, "/tokenize", JsonSerializer.Serialize(new { content = text }), key);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("tokens", out var t) && t.ValueKind == JsonValueKind.Array ? t.GetArrayLength() : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // Zeichen je Token für den Füllertext (gemessen am Server, sonst Schätzung)
    public static async Task<double> CalibrateAsync(HttpClient http, string baseUrl, SecretValue? key, int seed, CancellationToken ct)
    {
        var sample = Filler(3000, seed);
        int? n = await CountTokensAsync(http, baseUrl, key, sample, ct);
        return n is > 50 ? sample.Length / (double)n.Value : 3.2;
    }

    private sealed record Reply(bool Ok, string Note, int PromptN, double PromptMs, int PredN, double PredMs, double WallSec, string? Finish, string[] Tools, string ToolArgs);

    // Eine Chat-Anfrage ohne Stream; Zeiten aus "timings" des Servers, sonst aus usage und Wanduhr
    private static async Task<Reply> ChatAsync(HttpClient http, string baseUrl, SecretValue? key, string userText, int maxTokens, int seed, string? toolsJson, TimeSpan timeout, CancellationToken ct)
    {
        var body = new StringBuilder("{\"messages\":[{\"role\":\"user\",\"content\":");
        body.Append(JsonSerializer.Serialize(userText));
        body.Append("}],\"max_tokens\":").Append(maxTokens).Append(",\"temperature\":0,\"seed\":").Append(seed).Append(",\"cache_prompt\":false,\"stream\":false");
        if (toolsJson != null) body.Append(",\"tools\":").Append(toolsJson);
        body.Append('}');
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var req = Post(baseUrl, "/v1/chat/completions", body.ToString(), key);
            using var resp = await http.SendAsync(req, cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            double wall = sw.Elapsed.TotalSeconds;
            if (!resp.IsSuccessStatusCode)
            {
                string msg = text.Length > 160 ? text[..160] : text;
                try { using var e = JsonDocument.Parse(text); if (e.RootElement.TryGetProperty("error", out var er) && er.TryGetProperty("message", out var m)) msg = m.GetString() ?? msg; } catch { }
                return new Reply(false, $"HTTP {(int)resp.StatusCode}: {msg}", 0, 0, 0, 0, wall, null, Array.Empty<string>(), "");
            }
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            int pn = 0, gn = 0; double pms = 0, gms = 0;
            if (r.TryGetProperty("timings", out var t) && t.ValueKind == JsonValueKind.Object)
            {
                pn = I(t, "prompt_n"); gn = I(t, "predicted_n"); pms = D(t, "prompt_ms"); gms = D(t, "predicted_ms");
                int cache = I(t, "cache_n");
                if (cache > 0 && pn == 0) pn = cache;
            }
            if (r.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                if (pn == 0) pn = I(u, "prompt_tokens");
                if (gn == 0) gn = I(u, "completion_tokens");
            }
            string? finish = null; var tools = new List<string>(); string args = "";
            if (r.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0)
            {
                var c0 = ch[0];
                if (c0.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) finish = fr.GetString();
                if (c0.TryGetProperty("message", out var msg2) && msg2.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                    foreach (var tc in tcs.EnumerateArray())
                        if (tc.TryGetProperty("function", out var f))
                        {
                            if (f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) tools.Add(n.GetString() ?? "");
                            if (args.Length == 0 && f.TryGetProperty("arguments", out var a)) args = a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : a.GetRawText();
                        }
            }
            // ohne timings: Prompt- und Erzeugungszeit nicht trennbar, die Gesamtzeit gilt für beides zusammen
            if (pms <= 0 && gms <= 0) { gms = wall * 1000; pms = 0; }
            return new Reply(true, "", pn, pms, gn, gms, wall, finish, tools.ToArray(), args);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new Reply(false, "timeout", 0, 0, 0, 0, sw.Elapsed.TotalSeconds, null, Array.Empty<string>(), ""); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new Reply(false, ex.Message, 0, 0, 0, 0, sw.Elapsed.TotalSeconds, null, Array.Empty<string>(), ""); }
    }

    private static int I(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
    private static double D(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static BenchStep ToStep(string kind, string name, int target, Reply r)
    {
        double pSec = r.PromptMs / 1000, gSec = r.PredMs / 1000;
        return new BenchStep
        {
            Kind = kind, Name = name, TargetPromptTokens = target, PromptTokens = r.PromptN, PromptSec = pSec, PromptTps = pSec > 0 ? r.PromptN / pSec : 0,
            GenTokens = r.PredN, GenSec = gSec, GenTps = gSec > 0 && r.PredN > 1 ? r.PredN / gSec : 0, TotalSec = r.WallSec, Ok = r.Ok, Note = r.Note,
        };
    }

    // Mehrere Läufe: der mittlere (nach Erzeugungsgeschwindigkeit) gilt
    private static BenchStep Median(List<BenchStep> runs)
    {
        var ok = runs.Where(r => r.Ok).OrderBy(r => r.GenTps).ToList();
        if (ok.Count == 0) return runs[^1];
        var m = ok[ok.Count / 2];
        m.Runs = runs.Count;
        return m;
    }

    // Kopfdaten eines Ergebnisses aus dem laufenden Server: Modell, Quantisierung (GGUF-Kopf), Einstellungen, GPU, Build
    public static BenchResult Describe(ServerWatcher w, Library lib, string gpuName, string? cpu = null, string? driver = null)
    {
        var info = w.Info;
        var p = info.Params;
        var prof = lib.FindProfile(info);
        var r = new BenchResult
        {
            Started = DateTime.Now, Server = w.Name, ServerKey = w.Key, Model = w.Model != "–" ? w.Model : w.Name,
            ProfileKey = info.Program != null ? Library.MakeKey(info.Program, info.Args) : "", ProfileName = prof?.Name ?? "",
            Gpu = gpuName, Build = w.Props?.BuildInfo ?? "", ModelSizeGb = w.ModelFileGb ?? 0,
            Cpu = string.IsNullOrEmpty(cpu) ? CpuName() : cpu, Driver = driver ?? "",
        };
        var path = ServerLauncher.ResolveModel(info.ModelPath, info.WorkingDir) ?? w.Props?.ModelPath;
        if (path != null) r.ModelFile = ServerInfo.ModelName(path);
        if (Gguf.TryRead(path) is { } g)
        {
            r.Quant = g.Quantization; r.Architecture = g.Architecture;
            r.Parameters = g.ParameterCount >= 1_000_000_000 ? (g.ParameterCount / 1e9).ToString("0.0", Inv) + " B" : (g.ParameterCount / 1e6).ToString("0", Inv) + " M";
            if (r.ModelSizeGb <= 0 && g.FileSize > 0) r.ModelSizeGb = g.FileSize / 1073741824.0;
        }
        else if (w.Props?.ModelFtype is { Length: > 0 } ft) r.Quant = ft;
        var parts = new List<string>();
        if (p?.Ctx is > 0) parts.Add("ctx " + p.Ctx);
        if (p?.NglRaw != null) parts.Add("ngl " + p.NglRaw);
        if (p?.CacheTypeK != null || p?.CacheTypeV != null) parts.Add($"{p?.CacheTypeK ?? "f16"}/{p?.CacheTypeV ?? "f16"} KV");
        if (p?.FlashAttn != null) parts.Add("fa " + (p.FlashAttn.Value ? "on" : "off"));
        if (p?.Np is > 0) parts.Add("np " + p.Np);
        r.Settings = string.Join(" · ", parts);
        return r;
    }

    // Eine Linie je Ergebnis für das Diagramm: x = Prompt-Länge (Token), y = Prompt- oder Erzeugungsgeschwindigkeit
    public static MetricSeries ContextSeries(BenchResult r, bool prompt)
    {
        var steps = r.Steps.Where(s => s.Kind == "context" && s.Ok && s.PromptTokens > 0).OrderBy(s => s.PromptTokens).ToList();
        return new MetricSeries(r.Title, steps.Select(s => (double)s.PromptTokens).ToArray(), steps.Select(s => prompt ? s.PromptTps : s.GenTps).ToArray());
    }

    // Rückgang der Erzeugungsgeschwindigkeit gegenüber einem früheren Lauf derselben Konfiguration (gleicher Profil-Schlüssel/Server+Modell).
    // thresholdPct: erst ab diesem Rückgang in Prozent wird gemeldet (0 = nie). null = kein aussagekräftiger Vergleich möglich.
    public static BenchRegression? Regression(BenchResult previous, BenchResult current, double thresholdPct = 10)
    {
        if (thresholdPct <= 0) return null;      // der Nutzer hat die Meldung abgeschaltet
        if (previous.Id == current.Id) return null;
        double? prev = Representative(previous), cur = Representative(current);
        if (prev is not > 0 || cur is null) return null;
        double drop = (prev.Value - cur.Value) / prev.Value * 100.0;
        return drop >= thresholdPct ? new BenchRegression(prev.Value, cur.Value, drop) : null;
    }

    // Derselbe Schwellwert aus den Einstellungen (BenchmarkService und der Dialog im Fenster nutzen das so)
    public static BenchRegression? Regression(BenchResult previous, BenchResult current, AppSettings settings) =>
        Regression(previous, current, settings.BenchRegressionPct);

    // Kennzahl für den Vergleich: Chat-Tokens/s, sonst die beste Kontext-Erzeugung
    private static double? Representative(BenchResult r)
    {
        var chat = r.Steps.FirstOrDefault(s => s.Kind == "chat" && s.Ok && s.GenTps > 0);
        if (chat != null) return chat.GenTps;
        var ctx = r.Steps.Where(s => s.Kind == "context" && s.Ok && s.GenTps > 0).Select(s => s.GenTps).ToList();
        return ctx.Count > 0 ? ctx.Max() : null;
    }

    // Bestes früheres Ergebnis derselben Konfiguration (gleicher Profil-Schlüssel, sonst gleicher Server+Modell)
    public static BenchResult? PreviousSimilar(Library lib, BenchResult current)
    {
        return lib.Benchmarks.Where(b => b.Id != current.Id && b.Steps.Any(s => s.Ok))
            .Where(b => (!string.IsNullOrEmpty(current.ProfileKey) && b.ProfileKey == current.ProfileKey)
                        || (string.IsNullOrEmpty(current.ProfileKey) && b.ServerKey == current.ServerKey && b.Model == current.Model))
            .OrderByDescending(b => b.Started).FirstOrDefault();
    }

    private const string ToolsJson = "[{\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"description\":\"Get the current weather for a city\",\"parameters\":{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\",\"description\":\"City name\"}},\"required\":[\"city\"]}}}]";

    // Führt die Testreihe aus. onStep meldet jeden fertigen Schritt (auch für die Anzeige während des Laufs).
    public static async Task<BenchResult> RunAsync(HttpClient http, string baseUrl, BenchOptions opt, BenchResult result,
        Action<string>? progress = null, Action<BenchStep>? onStep = null, CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        result.Seed = opt.Seed; result.ContextPerSlot = opt.ContextPerSlot; result.Slots = opt.Slots;
        void Add(BenchStep s) { result.Steps.Add(s); onStep?.Invoke(s); }
        try
        {
            // 1. kurzer Chat
            if (opt.Chat)
            {
                progress?.Invoke("Short chat …");
                var runs = new List<BenchStep>();
                for (int i = 0; i < opt.Repeats; i++)
                    runs.Add(ToStep("chat", "Short chat", 0, await ChatAsync(http, baseUrl, opt.ApiKey, "Explain in two sentences why the sky is blue.", opt.GenTokens, opt.Seed, null, opt.RequestTimeout, ct)));
                Add(Median(runs));
            }

            // 2. wachsender Kontext
            var sizes = opt.UsableSizes().ToList();
            if (sizes.Count > 0)
            {
                double cpt = await CalibrateAsync(http, baseUrl, opt.ApiKey, opt.Seed, ct);   // Zeichen je Token
                foreach (var size in sizes)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Invoke($"Context {size / 1024}k: reading the prompt, then generating {opt.GenTokens} tokens …");
                    int chars = Math.Max(200, (int)((size - 48) * cpt));   // etwas Platz für Vorlage und Anweisung
                    var text = Filler(chars, opt.Seed) + "\n\nSummarize the notes above in one short sentence.";
                    var runs = new List<BenchStep>();
                    for (int i = 0; i < opt.Repeats; i++)
                    {
                        var step = ToStep("context", $"Context {size / 1024}k", size, await ChatAsync(http, baseUrl, opt.ApiKey, text, opt.GenTokens, opt.Seed, null, opt.RequestTimeout, ct));
                        runs.Add(step);
                        if (!step.Ok) break;
                    }
                    var med = Median(runs);
                    Add(med);
                    if (!med.Ok && med.Note.Contains("context", StringComparison.OrdinalIgnoreCase)) break;   // größere passen erst recht nicht
                }
            }

            // 3. Werkzeugaufruf
            if (opt.Tool)
            {
                progress?.Invoke("Tool call …");
                var r = await ChatAsync(http, baseUrl, opt.ApiKey, "What is the weather in Paris right now? Use the tool.", 512, opt.Seed, ToolsJson, opt.RequestTimeout, ct);
                var step = ToStep("tool", "Tool call", 0, r);
                bool called = r.Tools.Contains("get_weather");
                bool argsOk = false;
                try { using var a = JsonDocument.Parse(r.ToolArgs); argsOk = a.RootElement.TryGetProperty("city", out var c) && (c.GetString() ?? "").Contains("Paris", StringComparison.OrdinalIgnoreCase); } catch { }
                step.Ok = r.Ok && called && argsOk;
                step.Note = !r.Ok ? r.Note : called ? (argsOk ? $"ok: get_weather({r.ToolArgs.Trim()})" : $"called get_weather but arguments are wrong: {r.ToolArgs}") : $"no tool call (finish: {r.Finish ?? "?"})";
                Add(step);
            }

            // 4. parallele Anfragen (nur mit mehreren Slots)
            if (opt.Parallel && opt.Slots > 1)
            {
                int n = Math.Min(opt.Slots, 4);
                progress?.Invoke($"{n} parallel requests …");
                double cpt = await CalibrateAsync(http, baseUrl, opt.ApiKey, opt.Seed, ct);
                var sw = Stopwatch.StartNew();
                var tasks = Enumerable.Range(0, n).Select(i =>
                    ChatAsync(http, baseUrl, opt.ApiKey, Filler((int)(400 * cpt), opt.Seed + i + 1) + "\n\nSummarize the notes above in one short sentence.", opt.GenTokens, opt.Seed + i, null, opt.RequestTimeout, ct)).ToArray();
                var replies = await Task.WhenAll(tasks);
                double wall = sw.Elapsed.TotalSeconds;
                var good = replies.Where(r => r.Ok).ToList();
                var step = new BenchStep
                {
                    Kind = "parallel", Name = $"{n} parallel requests", Parallel = n, Ok = good.Count == n, TotalSec = wall,
                    PromptTokens = good.Count > 0 ? (int)good.Average(r => r.PromptN) : 0,
                    GenTokens = good.Sum(r => r.PredN),
                    GenTps = good.Count > 0 ? good.Average(r => r.PredMs > 0 && r.PredN > 1 ? r.PredN / (r.PredMs / 1000) : 0) : 0,      // je Anfrage
                    PromptTps = good.Count > 0 ? good.Average(r => r.PromptMs > 0 ? r.PromptN / (r.PromptMs / 1000) : 0) : 0,
                    AggregateTps = wall > 0 ? good.Sum(r => r.PredN) / wall : 0,
                    Note = good.Count == n ? $"aggregate {(wall > 0 ? good.Sum(r => r.PredN) / wall : 0).ToString("0.0", Inv)} t/s" : string.Join("; ", replies.Where(r => !r.Ok).Select(r => r.Note).Distinct()),
                };
                Add(step);
            }
        }
        catch (OperationCanceledException) { result.Cancelled = true; }
        result.DurationSec = total.Elapsed.TotalSeconds;
        return result;
    }
}

// ───────────── Export ─────────────

public static class BenchmarkExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string N(double v, string f = "0.0") => v > 0 ? v.ToString(f, Inv) : "–";

    // Markdown für README oder Foren: Einrichtung je Ergebnis, dann je Test eine Zeile mit Prompt-/Erzeugungs-Tokens pro Sekunde
    public static string Markdown(IReadOnlyList<BenchResult> results)
    {
        var sb = new StringBuilder();
        if (results.Count == 0) return "";
        sb.AppendLine("### Setup").AppendLine();
        sb.AppendLine("| | " + string.Join(" | ", results.Select(r => Esc(r.Title))) + " |");
        sb.AppendLine("|---|" + string.Join("|", results.Select(_ => "---")) + "|");
        void Row(string label, Func<BenchResult, string> f) => sb.AppendLine($"| {label} | " + string.Join(" | ", results.Select(r => Esc(f(r)))) + " |");
        Row("Model", r => r.Model);
        Row("Model file", r => r.ModelFile);
        Row("Quantization", r => r.Quant);
        Row("Parameters", r => r.Parameters);
        Row("Model size", r => r.ModelSizeGb > 0 ? r.ModelSizeGb.ToString("0.0", Inv) + " GB" : "");
        Row("Settings", r => r.Settings);
        Row("GPU", r => r.Gpu);
        Row("GPU driver", r => r.Driver);
        Row("CPU", r => r.Cpu);
        Row("Server build", r => r.Build);
        Row("Context per slot", r => r.ContextPerSlot > 0 ? r.ContextPerSlot.ToString(Inv) : "");
        Row("Date", r => r.Started.ToString("yyyy-MM-dd HH:mm", Inv));
        sb.AppendLine().AppendLine("### Results (tokens per second)").AppendLine();
        sb.AppendLine("| Test | " + string.Join(" | ", results.Select(r => Esc(r.Title) + " prompt / generation")) + " |");
        sb.AppendLine("|---|" + string.Join("|", results.Select(_ => "---")) + "|");
        var names = results.SelectMany(r => r.Steps).Select(s => s.Name).Distinct().ToList();
        // Reihenfolge: Chat, Kontext aufsteigend, Werkzeug, parallel
        names = names.OrderBy(n => n.StartsWith("Short") ? 0 : n.StartsWith("Context") ? 1 : n.StartsWith("Tool") ? 3 : 2)
                     .ThenBy(n => n.StartsWith("Context") ? int.Parse(new string(n.Where(char.IsDigit).ToArray()), Inv) : 0).ToList();
        foreach (var name in names)
        {
            var cells = results.Select(r =>
            {
                var s = r.Steps.FirstOrDefault(x => x.Name == name);
                if (s == null) return "";
                if (!s.Ok && s.Kind != "tool") return "failed: " + Esc(s.Note);
                if (s.Kind == "tool") return s.Ok ? "ok" : "failed";
                if (s.Kind == "parallel") return $"{N(s.GenTps)} each, {N(s.AggregateTps)} total";
                return $"{N(s.PromptTps, "0")} / {N(s.GenTps)}" + (s.PromptTokens > 0 && s.Kind == "context" ? $" ({s.PromptTokens} tok)" : "");
            });
            sb.AppendLine($"| {name} | " + string.Join(" | ", cells) + " |");
        }
        sb.AppendLine().AppendLine("_Measured with fixed prompts, cache_prompt off, temperature 0, seed " + results[0].Seed + " (StykkerLLM)._");
        var drops = results.Where(r => r.Regression is { }).ToList();
        if (drops.Count > 0)
        {
            sb.AppendLine().AppendLine($"**{Strings.BenchRegressionTitle}:**").AppendLine();
            foreach (var r in drops) sb.AppendLine($"- {Esc(r.Title)}: {Strings.BenchRegressionAlert(r.Regression!.DropPct, r.Regression.Previous, r.Regression.Current)}");
        }
        return sb.ToString();
    }

    private static string Esc(string s) => s.Replace("|", "\\|").Replace("\n", " ");

    public static string Csv(IReadOnlyList<BenchResult> results)
    {
        var sb = new StringBuilder("Date,Title,Model,Quantization,Settings,Gpu,Driver,Cpu,Test,TargetPromptTokens,PromptTokens,PromptTokensPerSec,GenTokens,GenTokensPerSec,AggregateTokensPerSec,Seconds,Ok,Note\r\n");
        string Q(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"").Replace("\n", " ") + "\"" : s;
        foreach (var r in results)
            foreach (var s in r.Steps)
                sb.Append(r.Started.ToString("yyyy-MM-dd HH:mm:ss", Inv)).Append(',').Append(Q(r.Title)).Append(',').Append(Q(r.Model)).Append(',').Append(Q(r.Quant)).Append(',')
                  .Append(Q(r.Settings)).Append(',').Append(Q(r.Gpu)).Append(',').Append(Q(r.Driver)).Append(',').Append(Q(r.Cpu)).Append(',').Append(Q(s.Name)).Append(',').Append(s.TargetPromptTokens).Append(',').Append(s.PromptTokens).Append(',')
                  .Append(s.PromptTps.ToString("0.0", Inv)).Append(',').Append(s.GenTokens).Append(',').Append(s.GenTps.ToString("0.0", Inv)).Append(',')
                  .Append(s.AggregateTps.ToString("0.0", Inv)).Append(',').Append(s.TotalSec.ToString("0.0", Inv)).Append(',').Append(s.Ok ? "yes" : "no").Append(',').Append(Q(s.Note)).Append("\r\n");
        return sb.ToString();
    }
}
