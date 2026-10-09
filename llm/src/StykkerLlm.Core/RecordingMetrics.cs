using System.Globalization;

namespace StykkerLlm.Core;

public sealed record MetricSeries(string Name, double[] X, double[] Y);

public sealed record MetricInfo(string Id, string Label, string Unit);

// Verlaufsdaten einer Aufnahme für die Diagramme und die Gegenüberstellung zweier Aufnahmen (UI-frei, damit testbar)
public static class RecordingMetrics
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly MetricInfo[] All =
    {
        new("tps", "Tokens/s", "t/s"),
        new("ttft", "Time to first token", "s"),
        new("ctx", "Context used", "tokens"),
        new("gpu_vram", "GPU VRAM used", "GB"),
        new("gpu_util", "GPU load", "%"),
        new("gpu_power", "GPU power", "W"),
        new("gpu_temp", "GPU temperature", "°C"),
        new("gpu_clock", "GPU clock", "MHz"),
        new("srv_vram", "Server VRAM", "GB"),
        new("srv_cpu", "Server CPU", "%"),
        new("sys_cpu", "System CPU", "%"),
        new("sys_ram", "System RAM", "GB"),
    };

    public static MetricInfo Info(string id) => All.FirstOrDefault(m => m.Id == id) ?? All[0];

    // Eine Reihe je Server (Tokens/s, Kontext, Server-Werte) oder eine Reihe (GPU, System)
    public static List<MetricSeries> Series(RecordingData d, string metric)
    {
        var result = new List<MetricSeries>();
        var xs = d.Samples.Select(s => s.T).ToArray();
        MetricSeries One(string name, Func<RecSample, double?> f)
        {
            var x = new List<double>(); var y = new List<double>();
            foreach (var s in d.Samples) { var v = f(s); if (v != null) { x.Add(s.T); y.Add(v.Value); } }
            return new MetricSeries(name, x.ToArray(), y.ToArray());
        }
        string NameOf(string key) => d.Meta.ServerKeys.Zip(d.Meta.ServerNames, (k, n) => (k, n)).FirstOrDefault(t => t.k == key).n ?? key;
        var keys = d.Meta.ServerKeys.Length > 0 ? d.Meta.ServerKeys : d.Samples.SelectMany(s => s.Servers.Select(v => v.Key)).Distinct().ToArray();
        switch (metric)
        {
            case "tps":
                foreach (var k in keys)
                {
                    var sampled = One(NameOf(k), s => s.Servers.FirstOrDefault(v => v.Key == k)?.Tps);
                    var steps = RequestSteps(d, NameOf(k), k);
                    // gemessen, wenn es Ausschläge gab; sonst (nur kurze Anfragen) die Stufen aus den Anfragen
                    result.Add(sampled.Y.Any(v => v > 0) || steps.X.Length == 0 ? sampled : steps);
                }
                break;
            case "ttft":
                foreach (var k in keys)
                {
                    var evs = d.Requests.Where(r => r.ServerKey == k && r.TtftSec >= 0).OrderBy(r => r.Start).ToList();
                    if (evs.Count > 0)
                        result.Add(new MetricSeries(NameOf(k), evs.Select(r => (r.Start - d.Meta.Started).TotalSeconds).ToArray(), evs.Select(r => r.TtftSec).ToArray()));
                }
                break;
            case "ctx":
                foreach (var k in keys) result.Add(One(NameOf(k), s => s.Servers.FirstOrDefault(v => v.Key == k) is { } v ? v.CtxUsed : null));
                break;
            case "srv_vram":
                foreach (var k in keys) result.Add(One(NameOf(k), s => s.Servers.FirstOrDefault(v => v.Key == k)?.VramGb));
                break;
            case "srv_cpu":
                foreach (var k in keys) result.Add(One(NameOf(k), s => s.Servers.FirstOrDefault(v => v.Key == k)?.CpuPct));
                break;
            case "gpu_vram": result.Add(One("GPU", s => s.Gpu?.MemUsedGb)); break;
            case "gpu_util": result.Add(One("GPU", s => s.Gpu?.Util)); break;
            case "gpu_power": result.Add(One("GPU", s => s.Gpu?.PowerW)); break;
            case "gpu_temp": result.Add(One("GPU", s => s.Gpu?.TempC)); break;
            case "gpu_clock": result.Add(One("GPU", s => s.Gpu?.GfxMhz)); break;
            case "sys_cpu": result.Add(One("System", s => s.Sys?.CpuPct)); break;
            case "sys_ram": result.Add(One("System", s => s.Sys?.RamUsedGb)); break;
        }
        return result.Where(r => r.X.Length > 0).ToList();
    }

    // Kurze Anfragen fallen zwischen zwei Messpunkte: die Geschwindigkeit je Anfrage als Stufe über die Erzeugungszeit zeichnen
    public static MetricSeries RequestSteps(RecordingData d, string name, string? key = null)
    {
        var x = new List<double>(); var y = new List<double>();
        foreach (var r in d.Requests.Where(r => (key == null || r.ServerKey == key) && r.GenTps > 0).OrderBy(r => r.Start))
        {
            double t0 = (r.Start - d.Meta.Started).TotalSeconds + r.PromptSec;
            double t1 = (r.End - d.Meta.Started).TotalSeconds;
            if (t1 <= t0) t1 = t0 + 0.2;
            x.Add(Math.Max(0, t0 - 0.01)); y.Add(0);
            x.Add(Math.Max(0, t0)); y.Add(r.GenTps);
            x.Add(t1); y.Add(r.GenTps);
            x.Add(t1 + 0.01); y.Add(0);
        }
        return new MetricSeries(name, x.ToArray(), y.ToArray());
    }

    // Eine Reihe für die Gegenüberstellung: gemessene Gesamt-Tokens/s, wenn es welche gibt, sonst die Stufen der Anfragen
    public static MetricSeries TpsCombined(RecordingData d, string name)
    {
        var x = d.Samples.Select(s => s.T).ToArray();
        var y = d.Samples.Select(s => s.TotalTps).ToArray();
        return y.Any(v => v > 0) ? new MetricSeries(name, x, y) : RequestSteps(d, name);
    }

    // Auf höchstens n Punkte ausdünnen (Maximum je Gruppe, damit Spitzen sichtbar bleiben)
    public static MetricSeries Downsample(MetricSeries s, int n)
    {
        if (n < 2 || s.X.Length <= n) return s;
        var x = new double[n]; var y = new double[n];
        double step = (double)s.X.Length / n;
        for (int i = 0; i < n; i++)
        {
            int a = (int)(i * step), b = Math.Min(s.X.Length, Math.Max(a + 1, (int)((i + 1) * step)));
            double max = double.MinValue; int mi = a;
            for (int j = a; j < b; j++) if (s.Y[j] > max) { max = s.Y[j]; mi = j; }
            x[i] = s.X[mi]; y[i] = s.Y[mi];
        }
        return new MetricSeries(s.Name, x, y);
    }

    // Eine Zeile Klartext zu einer Anfrage (Tooltip und Detailzeile): Wartezeit, Prompt-Token (gecacht), Denk-/Antwort-Token und -Zeit, t/s, Werkzeuge, finish_reason
    public static string Describe(RequestEvent r)
    {
        string N(double v, string f = "0.0") => v.ToString(f, Inv);
        var parts = new List<string>
        {
            $"#{r.Task} at {r.Start.ToString("HH:mm:ss", Inv)}",
            $"{N(r.DurationSec)} s",
            r.Status,
        };
        if (r.TtftSec >= 0) parts.Add($"first token after {N(r.TtftSec)} s");
        if (r.PromptTokens >= 0)
            parts.Add($"prompt {r.TotalPromptTokens.ToString("N0", Inv)} tok" + (r.CachedTokens > 0 ? $" ({r.CachedTokens.ToString("N0", Inv)} cached)" : "") + $", read in {N(r.PromptSec)} s" + (r.PromptTps > 0 ? $" ({N(r.PromptTps, "0")} t/s)" : ""));
        if (r.ReasoningTokens >= 0 && (r.FromProxy || r.ReasoningTokens > 0))
        {
            parts.Add($"thinking {r.ReasoningTokens.ToString("N0", Inv)} tok / {N(r.ThinkSec)} s");
            parts.Add($"answer {Math.Max(0, r.ContentTokens).ToString("N0", Inv)} tok / {N(r.AnswerSec)} s");
        }
        else if (r.GenTokens >= 0) parts.Add($"generation {r.GenTokens.ToString("N0", Inv)} tok / {N(r.AnswerSec)} s");
        if (r.GenTps > 0) parts.Add($"{N(r.GenTps)} t/s");
        if (r.Tools.Length > 0) parts.Add("tools: " + string.Join(", ", r.Tools));
        if (!string.IsNullOrEmpty(r.FinishReason)) parts.Add("finish: " + r.FinishReason);
        if (!string.IsNullOrEmpty(r.Client)) parts.Add("client: " + r.Client);
        return string.Join("  ·  ", parts);
    }

    public sealed record CompareRow(string Label, string A, string B, string Delta, int Better);   // Better: -1 A, 0 gleich/unklar, 1 B

    // Kennzahlen zweier Aufnahmen nebeneinander (Better: bei Tokens/s mehr = besser, bei VRAM/Energie weniger = besser)
    public static List<CompareRow> Compare(RecordingSummary a, RecordingSummary b)
    {
        var rows = new List<CompareRow>();
        string N(double v, string f = "0.0") => v.ToString(f, Inv);
        void Num(string label, double va, double vb, string f, string unit, bool higherBetter, bool judge = true)
        {
            string d = Math.Abs(va) < 1e-9 ? (Math.Abs(vb) < 1e-9 ? "" : "new") : ((vb - va) / va * 100).ToString("+0.#;-0.#;0", Inv) + " %";
            int better = !judge || Math.Abs(va - vb) < 1e-9 ? 0 : (vb > va) == higherBetter ? 1 : -1;
            rows.Add(new CompareRow(label, N(va, f) + unit, N(vb, f) + unit, d, better));
        }
        rows.Add(new CompareRow("Model / target", a.Model, b.Model, "", 0));
        rows.Add(new CompareRow("Started", a.Started.ToString("yyyy-MM-dd HH:mm", Inv), b.Started.ToString("yyyy-MM-dd HH:mm", Inv), "", 0));
        Num("Duration", a.DurationSec / 60, b.DurationSec / 60, "0.0", " min", true, false);
        Num("Requests", a.Requests, b.Requests, "0", "", true, false);
        Num("Average t/s", a.AvgTps, b.AvgTps, "0.0", "", true);
        Num("Peak t/s", a.PeakTps, b.PeakTps, "0.0", "", true);
        if (a.TtftSamples > 0 || b.TtftSamples > 0)
        {
            Num("First token p50", a.TtftP50, b.TtftP50, "0.00", " s", false, false);
            Num("First token p95", a.TtftP95, b.TtftP95, "0.00", " s", false, false);
        }
        Num("Tokens in", a.TokensIn, b.TokensIn, "0", "", true, false);
        Num("Tokens out", a.TokensOut, b.TokensOut, "0", "", true, false);
        Num("Cache hits", a.CachePct, b.CachePct, "0", " %", true);
        Num("Model busy", a.BusyPct, b.BusyPct, "0", " %", true, false);
        Num("Max VRAM", a.MaxVramGb, b.MaxVramGb, "0.0", " GB", false);
        Num("Energy", a.EnergyWh, b.EnergyWh, "0.00", " Wh", false, false);
        double ea = a.TokensOut > 0 ? a.EnergyWh * 1000 / a.TokensOut : 0, eb = b.TokensOut > 0 ? b.EnergyWh * 1000 / b.TokensOut : 0;
        if (ea > 0 || eb > 0) Num("Energy per 1k tokens out", ea, eb, "0.00", " Wh", false);
        Num("Time reading prompts", a.PromptSec, b.PromptSec, "0.0", " s", false, false);
        Num("Time thinking", a.ThinkSec, b.ThinkSec, "0.0", " s", false, false);
        Num("Time answering", a.AnswerSec, b.AnswerSec, "0.0", " s", false, false);
        Num("Truncated / cancelled", a.Truncated + a.Cancelled, b.Truncated + b.Cancelled, "0", "", false);
        return rows;
    }
}

