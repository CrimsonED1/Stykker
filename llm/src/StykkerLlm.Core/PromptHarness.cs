using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StykkerLlm.Core;

// Mini-Harness: ein geladenes Modell direkt anprompten (Knopf „Prompt“ an der Serverkarte in Fenster und Web,
// /prompt in der TUI). OpenAI-kompatibel (/v1/chat/completions mit Stream) – das sprechen llama.cpp, Ollama,
// LM Studio und vLLM. Antwort und „Denken“ (reasoning_content) kommen getrennt, dazu die Messwerte, die man beim
// Ausprobieren sehen will: Zeit bis zum ersten Token, Tokens, t/s und warum die Antwort endete.

// Eine Nachricht im Gespräch. Mit Werkzeugen: die Antwort des Modells trägt ToolCalls, das Ergebnis kommt als
// Rolle "tool" mit der Id des Aufrufs zurück.
public sealed record ChatTurn(string Role, string Content, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null);

public sealed class PromptOptions
{
    public string System { get; set; } = "";
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 1024;
    public string Model { get; set; } = "";
    public string? ApiKey { get; set; }        // nur für Server mit --api-key; wird nirgends gespeichert
    // Werkzeuge (read, edit, cmd/PowerShell) im Arbeitsordner; null = ohne Werkzeuge
    public PromptTools? Tools { get; set; }
    // Schreiben, Ändern und Befehle ohne Rückfrage (sonst fragt die Oberfläche je Aufruf)
    public bool AutoApprove { get; set; }
    public const int MaxToolSteps = 20;
}

public sealed record PromptResult(string Text, string Reasoning, int PromptTokens, int CompletionTokens, double TtftMs, double TotalMs,
    double Tps, string Finish, string? Error)
{
    public bool Ok => Error == null;
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = Array.Empty<ToolCall>();
}

public sealed class PromptSession
{
    public List<ChatTurn> Turns { get; } = new();
    public PromptOptions Options { get; } = new();
    public PromptResult? Last { get; private set; }
    public bool Busy { get; private set; }
    // Was die Werkzeuge in diesem Gespräch getan haben (Aufruf, Ergebnis, abgelehnt?)
    public List<ToolOutcome> ToolLog { get; } = new();
    // Der Aufruf, der gerade auf Freigabe wartet (für die Anzeige)
    public ToolCall? Waiting { get; private set; }

    // Text und Denken der laufenden Antwort (für die Anzeige während des Streams)
    public string LiveText { get; private set; } = "";
    public string LiveReasoning { get; private set; } = "";

    public void Clear() { Turns.Clear(); ToolLog.Clear(); Last = null; LiveText = LiveReasoning = ""; }

    // Eine Nachricht schicken; der Verlauf wächst um Frage und Antwort (für Nachfragen). onDelta kommt aus dem
    // Hintergrund – die Oberfläche schaltet selbst um.
    // Mit Werkzeugen läuft eine Schleife: das Modell ruft Werkzeuge auf, die Ergebnisse gehen zurück, bis es antwortet
    // (höchstens MaxToolSteps Runden). approve fragt bei Schreiben, Ändern und Befehlen nach (null = ablehnen).
    public async Task<PromptResult> SendAsync(HttpClient http, string baseUrl, string text, Action? onDelta = null, CancellationToken ct = default,
        Func<ToolCall, Task<bool>>? approve = null)
    {
        if (Busy) return new PromptResult("", "", 0, 0, 0, 0, 0, "", Strings.PromptBusy);
        Busy = true;
        int start = Turns.Count;
        Turns.Add(new ChatTurn("user", text));
        try
        {
            PromptResult result;
            int step = 0;
            while (true)
            {
                LiveText = LiveReasoning = "";
                result = await PromptHarness.RunAsync(http, baseUrl, Turns, Options, (t, r) =>
                {
                    LiveText += t;
                    LiveReasoning += r;
                    onDelta?.Invoke();
                }, ct).ConfigureAwait(false);
                if (Options.Tools == null || result.ToolCalls.Count == 0 || !result.Ok || ct.IsCancellationRequested) break;
                Turns.Add(new ChatTurn("assistant", result.Text, result.ToolCalls));
                LiveText = LiveReasoning = "";   // steht jetzt im Verlauf – sonst zeigen die Oberflächen es doppelt
                foreach (var call in result.ToolCalls)
                {
                    bool allowed = !call.NeedsApproval || Options.AutoApprove;
                    if (!allowed && approve != null)
                    {
                        Waiting = call;
                        onDelta?.Invoke();
                        try { allowed = await approve(call).ConfigureAwait(false); }
                        finally { Waiting = null; }
                    }
                    var outcome = allowed
                        ? await Options.Tools.RunAsync(call, ct).ConfigureAwait(false)
                        : new ToolOutcome(call, Strings.ToolDenied, false, true);
                    ToolLog.Add(outcome);
                    Turns.Add(new ChatTurn("tool", outcome.Result, ToolCallId: call.Id));
                    onDelta?.Invoke();
                }
                if (++step >= PromptOptions.MaxToolSteps)
                {
                    result = result with { Error = Strings.ToolTooManySteps(PromptOptions.MaxToolSteps) };
                    break;
                }
            }
            if (result.Ok || result.Text.Length > 0) Turns.Add(new ChatTurn("assistant", result.Text));
            else if (Turns.Count == start + 1) Turns.RemoveAt(start);   // nichts gekommen: die Frage nicht doppelt im Verlauf
            Last = result;
            return result;
        }
        finally { Busy = false; }
    }
}

public static class PromptHarness
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Ollama, LM Studio und llama.cpp: alle unter /v1. Ein Modell muss Ollama genannt bekommen, die anderen nehmen,
    // was geladen ist.
    public static string ModelFor(RemoteServer s) => ModelFor(s.Models, s.Model);

    public static string ModelFor(ServerWatcher s) => ModelFor(s.Models, s.Model);

    // llama.cpp meldet als Modell den Dateipfad – im Feld reicht der Name (llama-server nimmt ohnehin, was geladen ist)
    private static string ModelFor(IEnumerable<LoadedModel> loaded, string model) =>
        loaded.FirstOrDefault()?.Name is { Length: > 0 } l ? l
        : model is { Length: > 0 } m && m != "–" ? (m.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(m) : m) : "";

    public static string Body(IReadOnlyList<ChatTurn> turns, PromptOptions opt)
    {
        var msgs = new JsonArray();
        if (opt.System.Trim().Length > 0) msgs.Add(new JsonObject { ["role"] = "system", ["content"] = opt.System });
        foreach (var t in turns)
        {
            var m = new JsonObject { ["role"] = t.Role, ["content"] = t.Content };
            if (t.ToolCalls is { Count: > 0 } calls)
                m["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
                }).ToArray());
            if (t.ToolCallId != null) m["tool_call_id"] = t.ToolCallId;
            msgs.Add(m);
        }
        var body = new JsonObject
        {
            ["messages"] = msgs,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["temperature"] = Math.Round(Math.Clamp(opt.Temperature, 0, 2), 2),
            ["max_tokens"] = Math.Clamp(opt.MaxTokens, 1, 32768),
        };
        if (opt.Model.Length > 0) body["model"] = opt.Model;
        if (opt.Tools != null) body["tools"] = PromptTools.Definitions();
        return body.ToJsonString();
    }

    public static async Task<PromptResult> RunAsync(HttpClient http, string baseUrl, IReadOnlyList<ChatTurn> turns, PromptOptions opt,
        Action<string, string>? onDelta = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        double ttft = 0;
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        int promptTokens = 0, completionTokens = 0, chunks = 0;
        double serverTps = 0;
        string finish = "";
        // Werkzeugaufrufe kommen im Stream stückweise (je Index: Id, Name, Argumente in Teilen)
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/v1/chat/completions")
            { Content = new StringContent(Body(turns, opt), Encoding.UTF8, "application/json") };
            if (!string.IsNullOrEmpty(opt.ApiKey)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + opt.ApiKey);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return Fail(resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ? Strings.PromptNeedsKey : Strings.PromptHttpError((int)resp.StatusCode, ErrorText(err)));
            }
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            bool sse = resp.Content.Headers.ContentType?.MediaType == "text/event-stream";
            if (!sse)
            {
                // Manche Server ignorieren "stream": dann kommt die ganze Antwort auf einmal
                Chunk(await reader.ReadToEndAsync(ct).ConfigureAwait(false), whole: true);
            }
            else
            {
                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var data = line[5..].Trim();
                    if (data == "[DONE]") break;
                    Chunk(data, whole: false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { finish = "stopped"; }
        catch (HttpRequestException ex) { return Fail(Strings.PromptNotReachable(ex.Message)); }
        catch (IOException ex) { return Fail(Strings.PromptNotReachable(ex.Message)); }

        sw.Stop();
        double total = sw.Elapsed.TotalMilliseconds;
        if (completionTokens == 0) completionTokens = chunks;   // ohne usage: ein Stück ≈ ein Token
        double genMs = Math.Max(1, total - ttft);
        double tps = serverTps > 0 ? serverTps : completionTokens * 1000.0 / genMs;
        return new PromptResult(text.ToString(), reasoning.ToString(), promptTokens, completionTokens, Math.Round(ttft), Math.Round(total), Math.Round(tps, 1), finish, null)
        {
            ToolCalls = calls.Values.Where(c => c.Name.Length > 0)
                .Select((c, i) => new ToolCall(c.Id.Length > 0 ? c.Id : "call_" + i, c.Name, c.Args.ToString())).ToList(),
        };

        PromptResult Fail(string message) =>
            new(text.ToString(), reasoning.ToString(), promptTokens, completionTokens, Math.Round(ttft), Math.Round(sw.Elapsed.TotalMilliseconds), 0, "error", message);

        void Chunk(string json, bool whole)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var c = choices[0];
                    var part = whole ? (c.TryGetProperty("message", out var m) ? m : default) : (c.TryGetProperty("delta", out var d) ? d : default);
                    string t = "", rs = "";
                    if (part.ValueKind == JsonValueKind.Object)
                    {
                        t = part.TryGetProperty("content", out var ce) && ce.ValueKind == JsonValueKind.String ? ce.GetString() ?? "" : "";
                        rs = part.TryGetProperty("reasoning_content", out var re) && re.ValueKind == JsonValueKind.String ? re.GetString() ?? ""
                            : part.TryGetProperty("reasoning", out var re2) && re2.ValueKind == JsonValueKind.String ? re2.GetString() ?? "" : "";
                    }
                    if (t.Length + rs.Length > 0)
                    {
                        if (ttft == 0) ttft = sw.Elapsed.TotalMilliseconds;
                        chunks++;
                        text.Append(t);
                        reasoning.Append(rs);
                        onDelta?.Invoke(t, rs);
                    }
                    if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                    {
                        int pos = 0;
                        foreach (var tc in tcs.EnumerateArray())
                        {
                            int idx = tc.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var ixv) ? ixv : pos;
                            pos++;
                            if (!calls.TryGetValue(idx, out var cur)) cur = ("", "", new StringBuilder());
                            if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) cur.Id = id.GetString() ?? cur.Id;
                            if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                            {
                                if (fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String) cur.Name += nm.GetString();
                                if (fn.TryGetProperty("arguments", out var ar))
                                    cur.Args.Append(ar.ValueKind == JsonValueKind.String ? ar.GetString() : ar.GetRawText());   // Ollama: Objekt statt Text
                            }
                            calls[idx] = cur;
                        }
                        if (ttft == 0) ttft = sw.Elapsed.TotalMilliseconds;
                    }
                    if (c.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String) finish = f.GetString() ?? "";
                }
                if (r.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    if (u.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var pti)) promptTokens = pti;
                    if (u.TryGetProperty("completion_tokens", out var ctk) && ctk.TryGetInt32(out var cti)) completionTokens = cti;
                }
                // llama.cpp schickt seine eigene Messung mit (genauer als die Uhr hier, ohne Netz und Prompt)
                if (r.TryGetProperty("timings", out var tm) && tm.ValueKind == JsonValueKind.Object
                    && tm.TryGetProperty("predicted_per_second", out var pps) && pps.TryGetDouble(out var ppsd)) serverTps = ppsd;
            }
            catch (JsonException) { /* halbe Zeile oder Kommentar: überspringen */ }
        }
    }

    // {"error":{"message":"…"}} oder {"error":"…"} → nur der Satz
    private static string ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
                return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : e.GetRawText();
        }
        catch (JsonException) { }
        return body.Length > 200 ? body[..200] : body;
    }

    // Eine Zeile mit den Messwerten: „TTFT 312 ms · 128 tok · 54.2 t/s · 2.7 s · stop“
    public static string Stats(PromptResult r) =>
        string.Join(" · ", new[]
        {
            $"TTFT {r.TtftMs.ToString("0", Inv)} ms",
            r.PromptTokens > 0 ? $"{r.PromptTokens} → {r.CompletionTokens} tok" : $"{r.CompletionTokens} tok",
            $"{r.Tps.ToString("0.0", Inv)} t/s",
            $"{(r.TotalMs / 1000).ToString("0.0", Inv)} s",
            r.Finish,
        }.Where(x => x.Length > 0));
}
