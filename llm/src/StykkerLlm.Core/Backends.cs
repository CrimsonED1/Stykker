using System.Globalization;
using System.Net;
using System.Text.Json;

namespace StykkerLlm.Core;

public enum BackendKind { LlamaCpp, Ollama, LmStudio, Vllm }

public enum ProbeVerdict { No, Yes, Retry }

// Was die Probe über den Prozess weiß (Programmname, Elternprozess, Kommandozeile), um unpassende Backends auszuschließen
public sealed record ProbeTarget(string ExeName, string ParentName, string? CommandLine, string? ImagePath);

public sealed record ProbeOutcome(ProbeVerdict Verdict, BackendKind Kind = BackendKind.LlamaCpp, LlamaProps? Props = null, string? Version = null);

// Erkennt ein Backend an einer HTTP-Adresse (nur lesende GET-Anfragen)
public interface IBackendProbe
{
    BackendKind Kind { get; }
    bool AppliesTo(ProbeTarget target);
    Task<ProbeOutcome> ProbeAsync(HttpClient http, string baseUrl, CancellationToken ct);
}

// Ein im Backend geladenes Modell (Ollama /api/ps, LM Studio /api/v0/models mit state "loaded")
public sealed record LoadedModel(string Name, long SizeBytes, long VramBytes, DateTime? ExpiresAt, int? ContextLength, string Detail, string State);

// ───────────── Ollama ─────────────
// Nach der offiziellen API-Dokumentation (docs/api.md): GET /api/version -> {"version":"0.5.1"},
// GET /api/ps -> {"models":[{"name","model","size","digest","details":{"format","family","parameter_size","quantization_level"},
// "expires_at":"2024-06-04T14:38:31.83753-07:00","size_vram":5137025024,"context_length":4096}]}. Entladen: POST /api/generate
// {"model":"...","keep_alive":0}.
public static class OllamaApi
{
    public static string? ParseVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch { return null; }
    }

    // null = keine gültige Antwort; leere Liste = läuft, nichts geladen
    public static List<LoadedModel>? ParsePs(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("models", out var arr)) return null;
            var list = new List<LoadedModel>();
            if (arr.ValueKind == JsonValueKind.Null) return list;
            if (arr.ValueKind != JsonValueKind.Array) return null;
            foreach (var m in arr.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                string name = Str(m, "name") ?? Str(m, "model") ?? "?";
                long size = Long(m, "size"), vram = Long(m, "size_vram");
                DateTime? exp = null;
                if (Str(m, "expires_at") is { } e && DateTimeOffset.TryParse(e, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) && dto.Year > 2000)
                    exp = dto.LocalDateTime;
                string detail = "";
                if (m.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object)
                    detail = string.Join(" ", new[] { Str(d, "parameter_size"), Str(d, "quantization_level"), Str(d, "family") }.Where(s => !string.IsNullOrEmpty(s)));
                int? ctx = m.TryGetProperty("context_length", out var cl) && cl.ValueKind == JsonValueKind.Number && cl.TryGetInt32(out var ci) ? ci : null;
                list.Add(new LoadedModel(name, size, vram, exp, ctx, detail, "loaded"));
            }
            return list;
        }
        catch { return null; }
    }

    public static string UnloadBody(string model) => JsonSerializer.Serialize(new Dictionary<string, object> { ["model"] = model, ["keep_alive"] = 0 });

    internal static string? Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static long Long(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
}

public sealed class OllamaProbe : IBackendProbe
{
    public BackendKind Kind => BackendKind.Ollama;
    public bool AppliesTo(ProbeTarget t) => true;

    public async Task<ProbeOutcome> ProbeAsync(HttpClient http, string baseUrl, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(baseUrl.TrimEnd('/') + "/api/version", ct);
            if (!resp.IsSuccessStatusCode) return new ProbeOutcome(ProbeVerdict.No);
            var v = OllamaApi.ParseVersion(await resp.Content.ReadAsStringAsync(ct));
            return v != null ? new ProbeOutcome(ProbeVerdict.Yes, BackendKind.Ollama, null, v) : new ProbeOutcome(ProbeVerdict.No);
        }
        catch (OperationCanceledException) { return new ProbeOutcome(ProbeVerdict.Retry); }
        catch { return new ProbeOutcome(ProbeVerdict.No); }
    }
}

// ───────────── LM Studio ─────────────
// Nach der offiziellen REST-API-Dokumentation (v0): GET /api/v0/models -> {"object":"list","data":[{"id","object":"model",
// "type":"llm|vlm|embeddings","publisher","arch","compatibility_type":"gguf|mlx","quantization","state":"loaded|not-loaded",
// "max_context_length","loaded_context_length"}]}. Geladen = state "loaded".
public static class LmStudioApi
{
    public sealed record ModelEntry(string Id, string Type, string Arch, string Quantization, string Format, bool Loaded, int? MaxContext, int? LoadedContext);

    // null = keine gültige Antwort (kein LM Studio)
    public static List<ModelEntry>? ParseModels(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var list = new List<ModelEntry>();
            foreach (var m in arr.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                int? Int(string n) => m.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
                list.Add(new ModelEntry(OllamaApi.Str(m, "id") ?? "?", OllamaApi.Str(m, "type") ?? "", OllamaApi.Str(m, "arch") ?? "",
                    OllamaApi.Str(m, "quantization") ?? "", OllamaApi.Str(m, "compatibility_type") ?? "",
                    OllamaApi.Str(m, "state") == "loaded", Int("max_context_length"), Int("loaded_context_length")));
            }
            return list;
        }
        catch { return null; }
    }

    // Die Antwort ist ein LM-Studio-Modellverzeichnis: leer oder mit den typischen Feldern
    public static bool LooksLikeLmStudio(List<ModelEntry> list, string json)
    {
        if (list.Count == 0) return json.Contains("\"object\"") && json.Contains("\"list\"");
        return json.Contains("\"state\"") || json.Contains("\"compatibility_type\"") || json.Contains("\"max_context_length\"");
    }

    public static List<LoadedModel> Loaded(List<ModelEntry> all) =>
        all.Where(m => m.Loaded).Select(m => new LoadedModel(m.Id, 0, 0, null, m.LoadedContext ?? m.MaxContext,
            string.Join(" ", new[] { m.Arch, m.Quantization, m.Format }.Where(s => s.Length > 0)), "loaded")).ToList();
}

public sealed class LmStudioProbe : IBackendProbe
{
    public BackendKind Kind => BackendKind.LmStudio;
    public bool AppliesTo(ProbeTarget t) => !t.ExeName.StartsWith("ollama", StringComparison.OrdinalIgnoreCase);

    public async Task<ProbeOutcome> ProbeAsync(HttpClient http, string baseUrl, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(baseUrl.TrimEnd('/') + "/api/v0/models", ct);
            if (!resp.IsSuccessStatusCode) return new ProbeOutcome(ProbeVerdict.No);
            var json = await resp.Content.ReadAsStringAsync(ct);
            var list = LmStudioApi.ParseModels(json);
            return list != null && LmStudioApi.LooksLikeLmStudio(list, json) ? new ProbeOutcome(ProbeVerdict.Yes, BackendKind.LmStudio) : new ProbeOutcome(ProbeVerdict.No);
        }
        catch (OperationCanceledException) { return new ProbeOutcome(ProbeVerdict.Retry); }
        catch { return new ProbeOutcome(ProbeVerdict.No); }
    }
}

// ───────────── llama.cpp ─────────────
// GET /props mit build_info, model_path, total_slots oder default_generation_settings
public sealed class LlamaCppProbe : IBackendProbe
{
    public BackendKind Kind => BackendKind.LlamaCpp;
    // Ollama (samt Runner) ist kein llama-server, auch wenn es llama.cpp benutzt
    public bool AppliesTo(ProbeTarget t) => !t.ExeName.StartsWith("ollama", StringComparison.OrdinalIgnoreCase);

    public async Task<ProbeOutcome> ProbeAsync(HttpClient http, string baseUrl, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(baseUrl.TrimEnd('/') + "/props", ct);
            if (resp.StatusCode == HttpStatusCode.ServiceUnavailable) return new ProbeOutcome(ProbeVerdict.Retry);   // lädt vielleicht noch
            if (!resp.IsSuccessStatusCode) return new ProbeOutcome(ProbeVerdict.No);
            var props = LlamaProps.Parse(await resp.Content.ReadAsStringAsync(ct));
            return props != null ? new ProbeOutcome(ProbeVerdict.Yes, BackendKind.LlamaCpp, props) : new ProbeOutcome(ProbeVerdict.No);
        }
        catch (OperationCanceledException) { return new ProbeOutcome(ProbeVerdict.Retry); }
        catch { return new ProbeOutcome(ProbeVerdict.No); }   // Verbindung verweigert, kein HTTP ...
    }
}

public static class BackendProbes
{
    public static IReadOnlyList<IBackendProbe> Default() => new IBackendProbe[] { new LlamaCppProbe(), new OllamaProbe(), new LmStudioProbe(), new VllmProbe() };

    // Einmalige Erkennung für eine Adresse (Dialog "Add server by URL"): null = keines der bekannten Backends antwortet
    public static async Task<ProbeOutcome?> DetectAsync(HttpClient http, string baseUrl, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var target = new ProbeTarget("", "", null, null);
        foreach (var p in Default())
        {
            if (!p.AppliesTo(target)) continue;
            var o = await p.ProbeAsync(http, baseUrl, cts.Token);
            if (o.Verdict == ProbeVerdict.Yes) return o;
        }
        return null;
    }

    public static BackendKind? KindFromName(string? name) => name?.ToLowerInvariant() switch
    {
        "llama.cpp" or "llamacpp" or "llama" => BackendKind.LlamaCpp,
        "ollama" => BackendKind.Ollama,
        "lmstudio" or "lm studio" or "lm-studio" => BackendKind.LmStudio,
        "vllm" => BackendKind.Vllm,
        _ => null,
    };

    public static string DisplayName(BackendKind k) => k switch
    {
        BackendKind.Ollama => "Ollama",
        BackendKind.LmStudio => "LM Studio",
        BackendKind.Vllm => "vLLM",
        _ => "llama.cpp"
    };
}

// ───────────── llama-server: Prometheus-Metriken ─────────────
// GET /metrics liefert das Prometheus-Textformat. Für die Oberfläche sind nur die beiden Anforderungs-Zähler
// interessant: requests_processing (in Arbeit) und requests_deferred (warten auf einen freien Slot).
// Der Parser ist absichtlich tolerant (Kommentarzeilen, unbekannte Zähler und fehlende Werte sind in Ordnung).
public static class LlamaMetricsApi
{
    public readonly record struct Requests(int? Processing, int? Deferred);

    public static Requests Parse(string text)
    {
        int? processing = null, deferred = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (TryValue(line, "llamacpp:requests_processing", out var p)) processing = p;
            else if (TryValue(line, "llamacpp:requests_deferred", out var d)) deferred = d;
        }
        return new Requests(processing, deferred);
    }

    private static bool TryValue(string line, string name, out int value)
    {
        value = 0;
        if (!line.StartsWith(name, StringComparison.Ordinal)) return false;
        var rest = line[name.Length..].Trim();
        if (rest.Length == 0 || rest[0] == '{') return false;   // mit Labels (nicht der Fall bei diesen Zählern)
        var token = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token != null && int.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

// Spekulatives Decoding: llama-server zählt die entworfenen und die vom Zielmodell angenommenen
// Entwürfe als Prometheus-Zähler – die Namen stehen in tools/server/server-task.cpp (dieses Repository, Stand 2026-10-05):
//   llamacpp:spec_decode_num_draft_tokens_total        (entworfene Token, kumulativ)
//   llamacpp:spec_decode_num_accepted_tokens_total     (davon angenommen)
//   llamacpp:spec_decode_num_drafts_total              (Prüfschritte, daraus die mittlere Annahmelänge)
// Ohne Entwürfe bleiben alle drei null, dann gibt es nichts anzuzeigen.
public static class LlamaSpecApi
{
    public readonly record struct Spec(int Drafted, int Accepted, int Steps);

    public static Spec Parse(string text)
    {
        int drafted = 0, accepted = 0, steps = 0;
        bool any = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (Try(line, "llamacpp:spec_decode_num_draft_tokens_total", out var d)) { drafted = d; any = true; }
            else if (Try(line, "llamacpp:spec_decode_num_accepted_tokens_total", out var a)) { accepted = a; any = true; }
            else if (Try(line, "llamacpp:spec_decode_num_drafts_total", out var s)) { steps = s; any = true; }
        }
        return any ? new Spec(drafted, accepted, steps) : new Spec(0, 0, 0);
    }

    // Quote der angenommenen Entwürfe (0 bis 1); 0 ohne Entwürfe
    public static double Rate(Spec s) => s.Drafted > 0 ? Math.Clamp((double)s.Accepted / s.Drafted, 0, 1) : 0;

    // Mittlere angenommene Länge je Prüfschritt – llama.cpp nennt sie „mean len“ (1,0 ohne Entwürfe)
    public static double MeanAccepted(Spec s) => s.Steps > 0 ? 1.0 + (double)s.Accepted / s.Steps : 0;

    private static bool Try(string line, string name, out int value)
    {
        value = 0;
        if (!line.StartsWith(name, StringComparison.Ordinal)) return false;
        var rest = line[name.Length..].TrimStart();
        if (rest.Length > 0 && rest[0] == '{') return false;   // gelabelte Reihen (pro Position) zählen nicht mit
        var token = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token != null && int.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
