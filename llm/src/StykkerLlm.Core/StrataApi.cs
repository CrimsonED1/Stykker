using System.Text.Json;

namespace StykkerLlm.Core;

// Strata (github.com/Niko1221/Strata) ahmt die llama-server-Endpunkte nach, aber /slots meldet nur is_processing.
// Die Live-Werte stehen in einem eigenen JSON unter /metrics: "live" (laufende Anfrage) und "requests" (abgeschlossene, neueste zuerst).
// Erkennung: /health enthält "service": "strata".
public static class StrataApi
{
    public const string EngineProcess = "strata";   // die eigentliche Engine ist ein Kindprozess des Python-Servers

    public sealed record Live(bool Busy, bool Reading, bool Thinking, int PromptTokens, int PromptRead, int PromptTotal,
        int Generated, int MaxTokens, double Tps);

    public sealed record Done(double Time, int PromptTokens, double PromptMs, int OutputTokens, double DecodeTps, double Seconds, string Finish);

    public sealed record Metrics(Live Live, IReadOnlyList<Done> Requests, int TotalRequests, int MaxContext);

    public static bool IsStrataHealth(string? json)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("service", out var s) &&
                   s.ValueKind == JsonValueKind.String && string.Equals(s.GetString(), "strata", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // null = keine Strata-Antwort (z. B. Prometheus-Text eines llama-server)
    public static Metrics? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("live", out var lv) || lv.ValueKind != JsonValueKind.Object) return null;
            string state = Str(lv, "state") ?? "idle";
            string phase = Str(lv, "phase") ?? "";
            bool busy = state is "reading" or "generating";
            var live = new Live(busy, state == "reading", phase.Contains("think", StringComparison.OrdinalIgnoreCase),
                Int(lv, "prompt_tokens"), Int(lv, "prompt_read"), Int(lv, "prompt_total"), Int(lv, "generated"), Int(lv, "max_tokens"),
                Dbl(lv, "tok_s"));
            var done = new List<Done>();
            if (r.TryGetProperty("requests", out var rq) && rq.ValueKind == JsonValueKind.Array)
                foreach (var e in rq.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Object)
                        done.Add(new Done(Dbl(e, "time"), Int(e, "prompt_tokens"), Dbl(e, "prompt_ms"), Int(e, "output_tokens"),
                            Dbl(e, "decode_tok_s"), Dbl(e, "duration_s"), Str(e, "finish") ?? ""));
            int total = r.TryGetProperty("totals", out var t) ? Int(t, "requests") : done.Count;
            int ctx = r.TryGetProperty("engine", out var en) ? Int(en, "context") : 0;
            return new Metrics(live, done, total, ctx);
        }
        catch { return null; }
    }

    public static ReqStatus Status(string finish) => finish switch
    {
        "length" => ReqStatus.Truncated,
        "stop" or "tool_calls" or "" => ReqStatus.Done,
        _ => ReqStatus.Cancelled,
    };

    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int Int(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;
    private static double Dbl(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
