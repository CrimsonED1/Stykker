using System.Globalization;
using System.Net;
using System.Text.Json;

namespace StykkerLlm.Core;

// ───────────── vLLM ─────────────
// vLLM ist ein OpenAI-kompatibler Server und läuft nur unter Linux (nativ oder in WSL/Docker).
// Nach seiner Dokumentation: GET /version -> {"version":"0.11.0"}, GET /health (leerer Text),
// GET /v1/models -> {"object":"list","data":[{"id":"Qwen/Qwen2.5-0.5B",…}]} und
// GET /metrics im Prometheus-Textformat mit den Zählern vllm:avg_generation_throughput_toks_per_s,
// vllm:num_requests_running, vllm:num_requests_waiting und vllm:gpu_cache_usage_perc.
//
// Wichtig für die Erkennung: vLLM wird als „python -m vllm…“ gestartet. ServerDiscovery hat Python-Prozesse
// bisher grundsätzlich übersprungen (Ollamas Runner, Strata) – ohne die Ausnahme unten bliebe jeder
// vLLM-Server unsichtbar. Die Ausnahme ist bewusst eng: nur wenn die Kommandozeile „vllm“ nennt.
public static class VllmApi
{
    /// <summary>Werte aus /metrics: Erzeugungs-Tokens/s, laufende und wartende Anfragen, GPU-Cache-Belegung in Prozent.</summary>
    public readonly record struct Metrics(double? Tps, int? Running, int? Waiting, double? GpuCachePct);

    public static string? ParseVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String) return null;
            var s = v.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
        catch { return null; }
    }

    // Die Modellnamen aus /v1/models. null = keine gültige Antwort, leere Liste = nichts geladen.
    public static List<string>? ParseModels(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var list = new List<string>();
            foreach (var m in arr.EnumerateArray())
                if (m.ValueKind == JsonValueKind.Object && OllamaApi.Str(m, "id") is { Length: > 0 } id && !list.Contains(id)) list.Add(id);
            return list;
        }
        catch { return null; }
    }

    // Absichtlich tolerant: Kommentarzeilen, unbekannte Zähler und fehlende Werte sind in Ordnung.
    // Die Zähler tragen bei vLLM ein Label ({model_name="…"}); mehrere Zeilen desselben Zählers werden
    // addiert, weil ein Server mehrere Modelle anbieten kann.
    public static Metrics ParseMetrics(string text)
    {
        double? tps = null, gpu = null;
        int? running = null, waiting = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (TryValue(line, "vllm:avg_generation_throughput_toks_per_s", out var t)) tps = (tps ?? 0) + t;
            else if (TryValue(line, "vllm:num_requests_running", out var r)) running = (running ?? 0) + (int)r;
            else if (TryValue(line, "vllm:num_requests_waiting", out var w)) waiting = (waiting ?? 0) + (int)w;
            else if (TryValue(line, "vllm:gpu_cache_usage_perc", out var g)) gpu = (gpu ?? 0) + g;
        }
        return new Metrics(tps is > 0 ? tps : null, running, waiting, gpu is > 0 ? gpu : null);
    }

    private static bool TryValue(string line, string name, out double value)
    {
        value = 0;
        if (!line.StartsWith(name, StringComparison.Ordinal)) return false;
        var rest = line[name.Length..].TrimStart();
        if (rest.Length == 0) return false;
        if (rest[0] == '{')                       // Label abschneiden: vllm:…{model_name="…"} 1.0
        {
            var close = rest.IndexOf('}');
            if (close < 0) return false;
            rest = rest[(close + 1)..].TrimStart();
        }
        var token = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token != null && double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Passt die Kommandozeile zu vLLM? (Kern der Ausnahme in der Erkennung.)</summary>
    public static bool MentionsVllm(string? commandLine)
    {
        var toks = CmdLine.Split(commandLine);
        return toks.Skip(1).Any(t => t.Contains("vllm", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class VllmProbe : IBackendProbe
{
    public BackendKind Kind => BackendKind.Vllm;

    // Ollama ist keine Zielgruppe (es hat kein /version mit diesem Aufbau); sonst prüft der Monitor jede Adresse.
    public bool AppliesTo(ProbeTarget t) => !t.ExeName.StartsWith("ollama", StringComparison.OrdinalIgnoreCase);

    public async Task<ProbeOutcome> ProbeAsync(HttpClient http, string baseUrl, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(baseUrl.TrimEnd('/') + "/version", ct);
            if (!resp.IsSuccessStatusCode) return new ProbeOutcome(ProbeVerdict.No);
            var v = VllmApi.ParseVersion(await resp.Content.ReadAsStringAsync(ct));
            return v != null ? new ProbeOutcome(ProbeVerdict.Yes, BackendKind.Vllm, null, v) : new ProbeOutcome(ProbeVerdict.No);
        }
        catch (OperationCanceledException) { return new ProbeOutcome(ProbeVerdict.Retry); }
        catch { return new ProbeOutcome(ProbeVerdict.No); }
    }
}