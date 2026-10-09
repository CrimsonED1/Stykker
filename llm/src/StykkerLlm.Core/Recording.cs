using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StykkerLlm.Core;

// ───────────── Datentypen einer Aufnahme (Sitzung) ─────────────

public sealed record RecMeta(string Id, DateTime Started, string Target, string[] ServerKeys, string[] ServerNames, string Model, string Gpu, bool Proxy);

public sealed record SlotSampleRec(int Id, bool Busy, double Tps, int Ctx, int Generated);

public sealed record ServerSampleRec(string Key, double Tps, double? VramGb, double? RamGb, double? CpuPct, SlotSampleRec[] Slots, string[] Clients, int CtxUsed, int CtxMax);

public sealed record GpuSampleRec(double Util, double MemUsedGb, double MemTotalGb, double PowerW, double TempC, double GfxMhz, double MemMhz);

public sealed record SysSampleRec(double CpuPct, double RamUsedGb);

public sealed record RecSample(double T, GpuSampleRec? Gpu, SysSampleRec? Sys, ServerSampleRec[] Servers)
{
    public double TotalTps => Servers.Sum(s => s.Tps);
}

// Eine Anfrage in der Zeitleiste. Phasen: Prompt lesen, Denken, Antwort (ohne Proxy zählt Denken+Antwort als "Antwort"/Generation).
// Unbekannte Zahlen sind -1. Es werden nur Zahlen und Namen gespeichert, nie Prompt-Inhalte.
public sealed class RequestEvent
{
    public string ServerKey { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Model { get; set; } = "";
    public int Task { get; set; }
    public int Slot { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public double PromptSec { get; set; }
    public double ThinkSec { get; set; }
    public double AnswerSec { get; set; }
    public int PromptTokens { get; set; } = -1;     // vom Server ausgewertete Prompt-Token
    public int CachedTokens { get; set; } = -1;     // aus dem Cache übernommen
    public int GenTokens { get; set; } = -1;
    public int ReasoningTokens { get; set; } = -1;
    public int ContentTokens { get; set; } = -1;
    public double PromptTps { get; set; }
    public double GenTps { get; set; }
    public double TtftSec { get; set; } = -1;       // Zeit bis zum ersten Token (nur mit Proxy)
    public string Status { get; set; } = "done";    // done | truncated | cancelled
    public string[] Tools { get; set; } = Array.Empty<string>();
    public string? FinishReason { get; set; }
    public string Client { get; set; } = "";
    public bool FromProxy { get; set; }
    public bool LogSeen { get; set; }

    public double DurationSec => Math.Max(0, (End - Start).TotalSeconds);
    public int TotalPromptTokens => PromptTokens < 0 ? -1 : PromptTokens + Math.Max(0, CachedTokens);

    // Aus dem Server-Log (immer verfügbar): Dauer, Prompt- und Erzeugungszeit aus den Zeitangaben des Servers
    public static RequestEvent FromLog(FinishedRequest f, string serverKey, string model)
    {
        var end = f.Seen ?? DateTime.Now;
        double promptSec = f.PromptTps > 0 && f.PromptTokens > 0 ? f.PromptTokens / f.PromptTps : 0;
        double genSec = f.GenTps > 0 && f.GenTokens > 0 ? f.GenTokens / f.GenTps : 0;
        double total = f.Seconds > 0 ? f.Seconds : promptSec + genSec;
        if (promptSec + genSec > total && total > 0) { var k = total / (promptSec + genSec); promptSec *= k; genSec *= k; }
        return new RequestEvent
        {
            ServerKey = serverKey, ServerName = f.Server, Model = model, Task = f.Task, Slot = f.Slot,
            Start = end - TimeSpan.FromSeconds(total), End = end, PromptSec = promptSec, AnswerSec = Math.Max(0, total - promptSec),
            PromptTokens = f.PromptTokens, CachedTokens = f.PromptTotal > f.PromptTokens ? f.PromptTotal - f.PromptTokens : (f.PromptTotal > 0 ? 0 : -1),
            GenTokens = f.GenTokens, PromptTps = f.PromptTps, GenTps = f.GenTps,
            Status = f.Status switch { ReqStatus.Truncated => "truncated", ReqStatus.Cancelled => "cancelled", _ => "done" },
            Client = f.Client, LogSeen = true,
        };
    }

    // Log-Ereignis und Proxy-Beobachtung derselben Anfrage zusammenführen: der Proxy liefert Start/Ende aus Sicht des Clients,
    // Zeit bis zum ersten Token, Denk-/Antwort-Token, Werkzeugaufrufe und finish_reason; das Log die Prompt-Zahlen.
    public static RequestEvent Merge(RequestEvent? log, ProxyRecord p, string serverKey, string serverName, string model)
    {
        var e = log ?? new RequestEvent { ServerKey = serverKey, ServerName = serverName, Model = model };
        e.ServerKey = serverKey; e.ServerName = serverName; if (e.Model.Length == 0) e.Model = model;
        e.Start = p.Start; e.End = p.End; e.FromProxy = true;
        e.Tools = p.Tools; e.FinishReason = p.FinishReason;
        if (!string.IsNullOrEmpty(p.UserAgent)) e.Client = p.UserAgent;
        if (p.ReasoningTokens >= 0) e.ReasoningTokens = p.ReasoningTokens;
        if (p.ContentTokens >= 0) e.ContentTokens = p.ContentTokens;
        if (e.GenTokens < 0 && p.ContentTokens >= 0) e.GenTokens = p.ContentTokens + Math.Max(0, p.ReasoningTokens);
        if (p.ClientAborted) e.Status = "cancelled";
        else if (p.FinishReason == "length") e.Status = "truncated";
        double total = (p.End - p.Start).TotalSeconds;
        if (p.FirstTokenAt != null)
        {
            e.TtftSec = Math.Max(0, (p.FirstTokenAt.Value - p.Start).TotalSeconds);
            e.PromptSec = e.TtftSec;
            if (p.ReasoningTokens > 0)
            {
                var contentAt = p.FirstContentAt ?? p.End;
                e.ThinkSec = Math.Max(0, (contentAt - p.FirstTokenAt.Value).TotalSeconds);
                e.AnswerSec = Math.Max(0, (p.End - contentAt).TotalSeconds);
            }
            else { e.ThinkSec = 0; e.AnswerSec = Math.Max(0, (p.End - p.FirstTokenAt.Value).TotalSeconds); }
        }
        else if (log == null) { e.PromptSec = 0; e.AnswerSec = total; }
        else
        {
            e.PromptSec = Math.Min(e.PromptSec, total); e.AnswerSec = Math.Max(0, total - e.PromptSec);
            // ohne Stream gibt es keine Zeitpunkte: die Erzeugungszeit nach dem Anteil der Denk-Token aufteilen
            if (p.ReasoningTokens > 0 && p.ContentTokens >= 0)
            {
                double share = (double)p.ReasoningTokens / (p.ReasoningTokens + p.ContentTokens);
                e.ThinkSec = e.AnswerSec * share; e.AnswerSec -= e.ThinkSec;
            }
        }
        if (e.GenTps <= 0 && e.GenTokens > 0 && e.AnswerSec + e.ThinkSec > 0) e.GenTps = e.GenTokens / (e.AnswerSec + e.ThinkSec);
        return e;
    }
}

// Beobachtung des Proxys zu einer Anfrage (nur Zahlen und Namen)
public sealed record ProxyRecord(string ServerKey, string Path, DateTime Start, DateTime End, DateTime? FirstTokenAt, DateTime? FirstContentAt,
    int ReasoningTokens, int ContentTokens, string[] Tools, string? FinishReason, string UserAgent, bool Stream, int HttpStatus, bool ClientAborted);

public sealed class RecordingData
{
    public RecMeta Meta { get; set; } = new("", DateTime.Now, "", Array.Empty<string>(), Array.Empty<string>(), "", "", false);
    public DateTime? Ended { get; set; }
    public List<RecSample> Samples { get; } = new();
    public List<RequestEvent> Requests { get; } = new();
    public double DurationSec => Samples.Count > 0 ? Samples[^1].T : (Ended != null ? (Ended.Value - Meta.Started).TotalSeconds : 0);
}

public sealed class RecordingSummary
{
    public string Id { get; set; } = "";
    public string File { get; set; } = "";
    public DateTime Started { get; set; }
    public double DurationSec { get; set; }
    public string Target { get; set; } = "";
    public string Model { get; set; } = "";
    public string Gpu { get; set; } = "";
    public bool Proxy { get; set; }
    public int Requests { get; set; }
    public double BusyPct { get; set; }
    public long TokensIn { get; set; }
    public long TokensOut { get; set; }
    public double CachePct { get; set; }
    public double AvgTps { get; set; }
    public double PeakTps { get; set; }
    public double TtftP50 { get; set; }            // Zeit bis zum ersten Token (nur mit Proxy)
    public double TtftP95 { get; set; }
    public double TtftP99 { get; set; }
    public int TtftSamples { get; set; }           // Anzahl Anfragen mit TTFT-Messung
    public double MaxVramGb { get; set; }
    public double EnergyWh { get; set; }
    public double PromptSec { get; set; }
    public double ThinkSec { get; set; }
    public double AnswerSec { get; set; }
    public double WaitSec { get; set; }
    public int Truncated { get; set; }
    public int Cancelled { get; set; }
    public int ToolCalls { get; set; }
}

// ───────────── Lesen, Zusammenfassen, Exportieren ─────────────

public static class RecordingStore
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Längste gelesene Zeile einer Aufnahme (Zeichen). Eine längere Zeile wird übersprungen (sonst könnte eine manipulierte Datei mit einer
    // riesigen Zeile den Speicher füllen); echte Zeilen sind einige hundert Byte lang.
    internal const int MaxLineChars = 1 << 20;

    // Wie TextReader.ReadLine, aber ohne mehr als maxChars zu puffern: eine zu lange Zeile wird bis zum Zeilenende gelesen, verworfen und
    // als leere Zeile geliefert (Load überspringt sie). null = Ende der Datei.
    internal static string? ReadLineLimited(TextReader r, int maxChars)
    {
        var sb = new StringBuilder();
        bool tooLong = false, any = false;
        int c;
        while ((c = r.Read()) >= 0)
        {
            any = true;
            if (c == '\n') break;
            if (c == 13 && r.Peek() == 10) continue;
            if (tooLong) continue;
            if (sb.Length >= maxChars) { tooLong = true; sb.Clear(); continue; }
            sb.Append((char)c);
        }
        return !any ? null : tooLong ? "" : sb.ToString();
    }

    public static RecordingData? Load(string path)
    {
        try
        {
            var data = new RecordingData();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = ReadLineLimited(sr, MaxLineChars)) != null)
            {
                if (line.Length < 2) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    switch (Str(r, "t"))
                    {
                        case "meta": data.Meta = ReadMeta(r); break;
                        case "s": data.Samples.Add(ReadSample(r)); break;
                        case "r": data.Requests.Add(ReadRequest(r)); break;
                        case "end": data.Ended = DateTime.Parse(Str(r, "ended") ?? "", Inv, DateTimeStyles.RoundtripKind); break;
                    }
                }
                catch { /* eine beschädigte Zeile (z. B. abgebrochenes Schreiben) überspringen */ }
            }
            return data.Meta.Id.Length == 0 ? null : data;
        }
        catch { return null; }
    }

    public static string? Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double Dbl(JsonElement e, string n, double def = 0) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;
    private static int Int(JsonElement e, string n, int def = 0) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : def;
    private static double? DblN(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static string[] StrArr(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>();
    private static DateTime Time(JsonElement e, string n) => DateTime.TryParse(Str(e, n), Inv, DateTimeStyles.RoundtripKind, out var d) ? d : default;

    private static RecMeta ReadMeta(JsonElement r) =>
        new(Str(r, "id") ?? "", Time(r, "started"), Str(r, "target") ?? "", StrArr(r, "keys"), StrArr(r, "names"), Str(r, "model") ?? "", Str(r, "gpu") ?? "",
            r.TryGetProperty("proxy", out var p) && p.ValueKind == JsonValueKind.True);

    private static RecSample ReadSample(JsonElement r)
    {
        GpuSampleRec? gpu = null;
        if (r.TryGetProperty("gpu", out var g) && g.ValueKind == JsonValueKind.Object)
            gpu = new GpuSampleRec(Dbl(g, "u"), Dbl(g, "vu"), Dbl(g, "vt"), Dbl(g, "pw"), Dbl(g, "tc"), Dbl(g, "gc"), Dbl(g, "mc"));
        SysSampleRec? sys = null;
        if (r.TryGetProperty("sys", out var s) && s.ValueKind == JsonValueKind.Object) sys = new SysSampleRec(Dbl(s, "cpu"), Dbl(s, "ram"));
        var servers = new List<ServerSampleRec>();
        if (r.TryGetProperty("sv", out var sv) && sv.ValueKind == JsonValueKind.Array)
            foreach (var x in sv.EnumerateArray())
            {
                var slots = new List<SlotSampleRec>();
                if (x.TryGetProperty("sl", out var sl) && sl.ValueKind == JsonValueKind.Array)
                    foreach (var q in sl.EnumerateArray()) slots.Add(new SlotSampleRec(Int(q, "i"), Int(q, "b") != 0, Dbl(q, "tps"), Int(q, "ctx"), Int(q, "gen")));
                servers.Add(new ServerSampleRec(Str(x, "k") ?? "", Dbl(x, "tps"), DblN(x, "vram"), DblN(x, "ram"), DblN(x, "cpu"), slots.ToArray(), StrArr(x, "cl"), Int(x, "cu"), Int(x, "cm")));
            }
        return new RecSample(Dbl(r, "ts"), gpu, sys, servers.ToArray());
    }

    private static RequestEvent ReadRequest(JsonElement r) => new()
    {
        ServerKey = Str(r, "k") ?? "", ServerName = Str(r, "name") ?? "", Model = Str(r, "model") ?? "", Task = Int(r, "task"), Slot = Int(r, "slot"),
        Start = Time(r, "start"), End = Time(r, "end"), PromptSec = Dbl(r, "psec"), ThinkSec = Dbl(r, "tsec"), AnswerSec = Dbl(r, "asec"),
        PromptTokens = Int(r, "ptok", -1), CachedTokens = Int(r, "ctok", -1), GenTokens = Int(r, "gtok", -1), ReasoningTokens = Int(r, "rtok", -1), ContentTokens = Int(r, "otok", -1),
        PromptTps = Dbl(r, "ptps"), GenTps = Dbl(r, "gtps"), TtftSec = Dbl(r, "ttft", -1), Status = Str(r, "status") ?? "done", Tools = StrArr(r, "tools"),
        FinishReason = Str(r, "finish"), Client = Str(r, "client") ?? "", FromProxy = r.TryGetProperty("px", out var px) && px.ValueKind == JsonValueKind.True,
        LogSeen = r.TryGetProperty("lg", out var lg) && lg.ValueKind == JsonValueKind.True,
    };

    // Zusammenfassung: Dauer, Anfragen, Anteil "Modell beschäftigt", Token rein/raus, Cache-Treffer, Energie, Zeitaufteilung
    public static RecordingSummary Summarize(RecordingData d, string file = "")
    {
        var s = new RecordingSummary
        {
            Id = d.Meta.Id, File = file, Started = d.Meta.Started, DurationSec = d.DurationSec, Target = d.Meta.Target, Model = d.Meta.Model,
            Gpu = d.Meta.Gpu, Proxy = d.Meta.Proxy, Requests = d.Requests.Count,
        };
        // Energie: Leistung mal Abstand der Messpunkte
        for (int i = 1; i < d.Samples.Count; i++)
        {
            var a = d.Samples[i - 1]; var b = d.Samples[i];
            double dt = Math.Clamp(b.T - a.T, 0, 10);
            if (b.Gpu != null) s.EnergyWh += b.Gpu.PowerW * dt / 3600.0;
        }
        double tpsSum = 0; int tpsN = 0;
        foreach (var sm in d.Samples)
        {
            double t = sm.TotalTps;
            if (t > 0) { tpsSum += t; tpsN++; s.PeakTps = Math.Max(s.PeakTps, t); }
            foreach (var sv in sm.Servers) if (sv.VramGb is double v) s.MaxVramGb = Math.Max(s.MaxVramGb, v);
            if (sm.Gpu != null && s.MaxVramGb == 0) { }
        }
        s.AvgTps = tpsN > 0 ? tpsSum / tpsN : 0;
        // Kurze Anfragen fallen zwischen zwei Messpunkte: die Zahlen der Anfragen (Token durch Erzeugungszeit) sind genauer
        var gens = d.Requests.Where(r => r.GenTokens > 0 && r.GenTps > 0).ToList();
        if (gens.Count > 0)
        {
            s.AvgTps = gens.Sum(r => r.GenTokens) / gens.Sum(r => r.GenTokens / r.GenTps);
            s.PeakTps = Math.Max(s.PeakTps, gens.Max(r => r.GenTps));
        }
        if (s.MaxVramGb == 0) s.MaxVramGb = d.Samples.Where(x => x.Gpu != null).Select(x => x.Gpu!.MemUsedGb).DefaultIfEmpty(0).Max();

        long cached = 0, evald = 0;
        foreach (var r in d.Requests)
        {
            if (r.PromptTokens >= 0) { evald += r.PromptTokens; if (r.CachedTokens > 0) cached += r.CachedTokens; }
            if (r.GenTokens > 0) s.TokensOut += r.GenTokens;
            s.PromptSec += r.PromptSec; s.ThinkSec += r.ThinkSec; s.AnswerSec += r.AnswerSec;
            if (r.Status == "truncated") s.Truncated++;
            if (r.Status == "cancelled") s.Cancelled++;
            s.ToolCalls += r.Tools.Length;
        }
        s.TokensIn = evald + cached;
        s.CachePct = s.TokensIn > 0 ? 100.0 * cached / s.TokensIn : 0;
        // Zeit bis zum ersten Token (nur mit Proxy gemessen): Perzentile über alle Anfragen
        var ttfts = d.Requests.Where(r => r.TtftSec >= 0).Select(r => r.TtftSec).OrderBy(v => v).ToList();
        s.TtftSamples = ttfts.Count;
        s.TtftP50 = Percentile(ttfts, 0.50); s.TtftP95 = Percentile(ttfts, 0.95); s.TtftP99 = Percentile(ttfts, 0.99);
        // "Modell beschäftigt": Vereinigung der Anfrage-Intervalle (parallele Anfragen zählen einmal)
        double busy = 0;
        DateTime? curStart = null, curEnd = null;
        foreach (var r in d.Requests.OrderBy(x => x.Start))
        {
            if (curEnd == null || r.Start > curEnd) { if (curStart != null) busy += (curEnd!.Value - curStart.Value).TotalSeconds; curStart = r.Start; curEnd = r.End; }
            else if (r.End > curEnd) curEnd = r.End;
        }
        if (curStart != null) busy += (curEnd!.Value - curStart.Value).TotalSeconds;
        double dur = Math.Max(0.001, d.DurationSec);
        busy = Math.Min(busy, dur);
        s.BusyPct = 100.0 * busy / dur;
        s.WaitSec = Math.Max(0, dur - busy);
        return s;
    }

    // Nächstgrößeres Element (n-te Perzentile) aus einer aufsteigend sortierten Liste
    private static double Percentile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        int idx = (int)Math.Ceiling(q * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    public static string SummaryPath(string jsonl) => System.IO.Path.ChangeExtension(jsonl, ".summary.json");
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static void SaveSummary(string jsonl, RecordingSummary s)
    {
        try { AtomicFile.WriteAllText(SummaryPath(jsonl), JsonSerializer.Serialize(s, Opt)); } catch { }
    }

    // Alle Aufnahmen eines Ordners (neueste zuerst); fehlende Zusammenfassungen werden aus der Datei berechnet
    public static List<RecordingSummary> List(string dir)
    {
        var result = new List<RecordingSummary>();
        try
        {
            if (!Directory.Exists(dir)) return result;
            foreach (var f in Directory.GetFiles(dir, "*.jsonl"))
            {
                RecordingSummary? s = null;
                var sp = SummaryPath(f);
                try { if (File.Exists(sp)) s = JsonSerializer.Deserialize<RecordingSummary>(File.ReadAllText(sp)); } catch { }
                if (s == null)
                {
                    var d = Load(f);
                    if (d == null) continue;
                    s = Summarize(d, f);
                    if (d.Ended != null) SaveSummary(f, s);
                }
                s.File = f;
                result.Add(s);
            }
        }
        catch { }
        return result.OrderByDescending(s => s.Started).ToList();
    }

    // Wie List, aber nicht auf dem UI-Thread (liest alle Zusammenfassungen, bei fehlenden auch die Aufnahme selbst)
    public static Task<List<RecordingSummary>> ListAsync(string dir) => Task.Run(() => List(dir));

    // ── Grenzen und Reparatur ──

    // Löscht die ältesten Aufnahmen (Datei + Zusammenfassung), bis Anzahl und Gesamtgröße unter den Grenzen liegen; keep bleibt
    public static int Prune(string dir, int maxCount, long maxBytes, IEnumerable<string>? keep = null)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            var keepSet = (keep ?? Array.Empty<string>()).Select(k => { try { return System.IO.Path.GetFullPath(k); } catch { return k; } }).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var items = new DirectoryInfo(dir).GetFiles("*.jsonl").OrderBy(f => f.LastWriteTimeUtc).ToList()
                .Select(f => (File: f, Size: f.Length + (System.IO.File.Exists(SummaryPath(f.FullName)) ? new FileInfo(SummaryPath(f.FullName)).Length : 0))).ToList();
            long total = items.Sum(i => i.Size);
            int count = items.Count;
            foreach (var (file, size) in items)
            {
                if (count <= maxCount && total <= maxBytes) break;
                if (keepSet.Contains(file.FullName)) continue;
                try
                {
                    file.Delete();
                    try { System.IO.File.Delete(SummaryPath(file.FullName)); } catch { }
                    total -= size; count--; removed++;
                }
                catch { }
            }
        }
        catch { }
        return removed;
    }

    // Eine Aufnahme ohne Ende-Zeile (Absturz, Stromausfall) einmalig abschließen: Ende-Zeile anhängen (Zeitpunkt des letzten Schreibens)
    // und die Zusammenfassung schreiben. active = Dateien laufender Aufnahmen, die nicht angefasst werden. Gibt die Zahl reparierter Dateien zurück.
    public static int RepairCrashed(string dir, IEnumerable<string>? active = null, TimeSpan? minAge = null)
    {
        int repaired = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            var skip = (active ?? Array.Empty<string>()).Select(k => { try { return System.IO.Path.GetFullPath(k); } catch { return k; } }).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(dir, "*.jsonl"))
            {
                if (skip.Contains(System.IO.Path.GetFullPath(f)) || DateTime.Now - System.IO.File.GetLastWriteTime(f) < (minAge ?? TimeSpan.FromSeconds(60)) || HasEndLine(f)) continue;
                try
                {
                    var ended = System.IO.File.GetLastWriteTime(f);
                    bool newline = EndsWithNewline(f);
                    using (var fs = new FileStream(f, FileMode.Append, FileAccess.Write, FileShare.Read))
                    using (var w = new StreamWriter(fs, new UTF8Encoding(false)) { NewLine = "\n" })
                    {
                        if (!newline) w.Write('\n');   // abgebrochene letzte Zeile abschließen (Load überspringt sie)
                        w.WriteLine("{\"t\":\"end\",\"ended\":\"" + ended.ToString("o", Inv) + "\",\"repaired\":true}");
                    }
                    var d = Load(f);
                    if (d != null) SaveSummary(f, Summarize(d, f));
                    repaired++;
                }
                catch { }
            }
        }
        catch { }
        return repaired;
    }

    private static bool HasEndLine(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int n = (int)Math.Min(fs.Length, 2048);
            fs.Seek(-n, SeekOrigin.End);
            var buf = new byte[n];
            int got = 0;
            while (got < n) { int r = fs.Read(buf, got, n - got); if (r <= 0) break; got += r; }
            var lines = Encoding.UTF8.GetString(buf, 0, got).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 && lines[^1].Contains("\"t\":\"end\"");
        }
        catch { return true; }   // nicht lesbar: nicht anfassen
    }

    private static bool EndsWithNewline(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length == 0) return true;
        fs.Seek(-1, SeekOrigin.End);
        return fs.ReadByte() == '\n';
    }

    // CSV: eine Zeile je Messpunkt und Server
    public static string ToCsv(RecordingData d)
    {
        var sb = new StringBuilder();
        sb.Append("Time,ElapsedSec,Server,TokensPerSec,SlotsBusy,ContextUsed,ContextMax,ServerVramGb,ServerRamGb,ServerCpuPct,Clients,GpuUtilPct,GpuVramUsedGb,GpuPowerW,GpuTempC,GpuClockMHz,MemClockMHz,SystemCpuPct,SystemRamUsedGb\r\n");
        var names = d.Meta.ServerKeys.Zip(d.Meta.ServerNames, (k, n) => (k, n)).ToDictionary(x => x.k, x => x.n);
        string F(double v, string f = "0.0") => v.ToString(f, Inv);
        string FN(double? v) => v == null ? "" : F(v.Value, "0.00");
        string Q(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        foreach (var s in d.Samples)
        {
            var time = d.Meta.Started.AddSeconds(s.T).ToString("yyyy-MM-dd HH:mm:ss", Inv);
            var servers = s.Servers.Length > 0 ? s.Servers : new[] { new ServerSampleRec("", 0, null, null, null, Array.Empty<SlotSampleRec>(), Array.Empty<string>(), 0, 0) };
            foreach (var sv in servers)
            {
                sb.Append(time).Append(',').Append(F(s.T)).Append(',').Append(Q(names.GetValueOrDefault(sv.Key) ?? sv.Key)).Append(',').Append(F(sv.Tps)).Append(',')
                  .Append(sv.Slots.Count(x => x.Busy)).Append(',').Append(sv.CtxUsed).Append(',').Append(sv.CtxMax).Append(',')
                  .Append(FN(sv.VramGb)).Append(',').Append(FN(sv.RamGb)).Append(',').Append(FN(sv.CpuPct)).Append(',').Append(Q(string.Join("+", sv.Clients))).Append(',')
                  .Append(s.Gpu != null ? F(s.Gpu.Util) : "").Append(',').Append(s.Gpu != null ? F(s.Gpu.MemUsedGb, "0.00") : "").Append(',')
                  .Append(s.Gpu != null ? F(s.Gpu.PowerW) : "").Append(',').Append(s.Gpu != null ? F(s.Gpu.TempC, "0") : "").Append(',')
                  .Append(s.Gpu != null ? F(s.Gpu.GfxMhz, "0") : "").Append(',').Append(s.Gpu != null ? F(s.Gpu.MemMhz, "0") : "").Append(',')
                  .Append(s.Sys != null ? F(s.Sys.CpuPct) : "").Append(',').Append(s.Sys != null ? F(s.Sys.RamUsedGb, "0.00") : "").Append("\r\n");
            }
        }
        return sb.ToString();
    }

    // CSV der Anfragen (Zeitleiste)
    public static string RequestsToCsv(RecordingData d)
    {
        var sb = new StringBuilder();
        sb.Append("Start,End,DurationSec,Server,Task,PromptSec,ThinkSec,AnswerSec,PromptTokens,CachedTokens,GenTokens,ReasoningTokens,ContentTokens,PromptTokensPerSec,GenTokensPerSec,TtftSec,Status,FinishReason,Tools,Client\r\n");
        string Q(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        foreach (var r in d.Requests.OrderBy(x => x.Start))
            sb.Append(r.Start.ToString("yyyy-MM-dd HH:mm:ss.fff", Inv)).Append(',').Append(r.End.ToString("yyyy-MM-dd HH:mm:ss.fff", Inv)).Append(',')
              .Append(r.DurationSec.ToString("0.00", Inv)).Append(',').Append(Q(r.ServerName)).Append(',').Append(r.Task).Append(',')
              .Append(r.PromptSec.ToString("0.00", Inv)).Append(',').Append(r.ThinkSec.ToString("0.00", Inv)).Append(',').Append(r.AnswerSec.ToString("0.00", Inv)).Append(',')
              .Append(r.PromptTokens).Append(',').Append(r.CachedTokens).Append(',').Append(r.GenTokens).Append(',').Append(r.ReasoningTokens).Append(',').Append(r.ContentTokens).Append(',')
              .Append(r.PromptTps.ToString("0.0", Inv)).Append(',').Append(r.GenTps.ToString("0.0", Inv)).Append(',').Append(r.TtftSec.ToString("0.00", Inv)).Append(',')
              .Append(r.Status).Append(',').Append(r.FinishReason ?? "").Append(',').Append(Q(string.Join("+", r.Tools))).Append(',').Append(Q(r.Client)).Append("\r\n");
        return sb.ToString();
    }
}

// ───────────── Schreiben: eine laufende Aufnahme ─────────────

public sealed class RecordingSession : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly StreamWriter _w;
    private readonly object _lock = new();
    private readonly Func<DateTime> _now;
    private readonly List<(RequestEvent Ev, DateTime At)> _pendingLog = new();
    private readonly List<(ProxyRecord Px, DateTime At, string Name, string Model)> _pendingProxy = new();
    private DateTime _lastFlush = DateTime.MinValue;
    private bool _closed;
    private long _bytes;

    public string Id { get; }
    public string File { get; }
    public string Target { get; }
    public DateTime Started { get; }
    public bool IsGlobal => Target == "all";
    public int Requests { get; private set; }
    // Bisher geschriebene Zeichen (ungefähr die Dateigröße)
    public long Bytes { get { lock (_lock) return _bytes; } }
    public double LastTps { get; private set; }
    public double ElapsedSec => (_now() - Started).TotalSeconds;
    // Zusammenführen von Log- und Proxy-Beobachtung: so lange warten wir auf den Partner
    public TimeSpan MergeWindow { get; set; } = TimeSpan.FromSeconds(4);
    public TimeSpan MergeTolerance { get; set; } = TimeSpan.FromSeconds(3);

    public RecordingSession(string dir, string target, IReadOnlyList<ServerWatcher> servers, string gpuName, bool proxy, Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.Now);
        Started = _now();
        Target = target;
        Directory.CreateDirectory(dir);
        string model = target == "all" ? "all" : servers.Select(s => s.Name).FirstOrDefault() ?? "server";
        Id = Started.ToString("yyyyMMdd-HHmmss", Inv) + "_" + ServerLauncher.SafeName(model);
        File = System.IO.Path.Combine(dir, Id + ".jsonl");
        int n = 1;
        while (System.IO.File.Exists(File)) File = System.IO.Path.Combine(dir, Id + "_" + (++n) + ".jsonl");
        var fs = new FileStream(File, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _w = new StreamWriter(fs, new UTF8Encoding(false)) { NewLine = "\n" };
        WriteLine(w =>
        {
            w.WriteString("t", "meta"); w.WriteNumber("v", 1); w.WriteString("id", Id); w.WriteString("started", Started.ToString("o", Inv));
            w.WriteString("target", target); w.WriteString("model", model); w.WriteString("gpu", gpuName); w.WriteBoolean("proxy", proxy);
            w.WriteStartArray("keys"); foreach (var s in servers) w.WriteStringValue(s.Key); w.WriteEndArray();
            w.WriteStartArray("names"); foreach (var s in servers) w.WriteStringValue(s.Name); w.WriteEndArray();
        });
        _w.Flush();
    }

    private void WriteLine(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var jw = new Utf8JsonWriter(ms)) { jw.WriteStartObject(); body(jw); jw.WriteEndObject(); }
        lock (_lock) { if (_closed) return; var text = Encoding.UTF8.GetString(ms.ToArray()); _w.WriteLine(text); _bytes += text.Length + 1; }
    }

    // Ein Messpunkt (einmal pro Sekunde aus dem UI-Takt)
    public void Sample(IReadOnlyList<ServerWatcher> servers, GpuSample? gpu, SystemSample? sys)
    {
        var t = (_now() - Started).TotalSeconds;
        var list = IsGlobal ? servers : servers.Where(s => s.Key == Target).ToList();
        LastTps = list.Sum(s => s.Current);
        WriteLine(w =>
        {
            w.WriteString("t", "s"); w.WriteNumber("ts", Math.Round(t, 2));
            if (gpu != null)
            {
                w.WriteStartObject("gpu");
                w.WriteNumber("u", Math.Round(gpu.Util, 1)); w.WriteNumber("vu", Math.Round(gpu.MemUsedGb, 3)); w.WriteNumber("vt", Math.Round(gpu.MemTotalGb, 2));
                w.WriteNumber("pw", Math.Round(gpu.PowerW, 1)); w.WriteNumber("tc", gpu.TempC); w.WriteNumber("gc", gpu.GfxMhz); w.WriteNumber("mc", gpu.MemMhz);
                w.WriteEndObject();
            }
            if (sys != null) { w.WriteStartObject("sys"); w.WriteNumber("cpu", Math.Round(sys.CpuPercent, 1)); w.WriteNumber("ram", Math.Round(sys.RamUsedGb, 2)); w.WriteEndObject(); }
            w.WriteStartArray("sv");
            foreach (var s in list)
            {
                w.WriteStartObject();
                w.WriteString("k", s.Key); w.WriteNumber("tps", Math.Round(s.Current, 2));
                if (s.VramGb is double v) w.WriteNumber("vram", Math.Round(v, 3));
                if (s.RamGb is double r) w.WriteNumber("ram", Math.Round(r, 3));
                if (s.CpuPercent is double c) w.WriteNumber("cpu", Math.Round(c, 1));
                w.WriteNumber("cu", s.Slots.Sum(x => x.CtxUsed)); w.WriteNumber("cm", s.Unified && s.Slots.Count > 0 ? s.Slots[0].CtxMax : s.Slots.Sum(x => x.CtxMax));
                w.WriteStartArray("sl");
                foreach (var sl in s.Slots)
                {
                    w.WriteStartObject(); w.WriteNumber("i", sl.Id); w.WriteNumber("b", sl.Busy ? 1 : 0); w.WriteNumber("tps", Math.Round(sl.Tps, 2));
                    w.WriteNumber("ctx", sl.CtxUsed); w.WriteNumber("gen", sl.Generated); w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("cl"); foreach (var cl in s.Clients) w.WriteStringValue(cl); w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
        FlushPending(_now());
        if ((_now() - _lastFlush).TotalSeconds >= 5) { _lastFlush = _now(); lock (_lock) if (!_closed) _w.Flush(); }
    }

    private bool Matches(string key) => IsGlobal || key == Target;

    // Anfrage aus dem Server-Log
    public void OnLogRequest(FinishedRequest f, string serverKey, string model)
    {
        if (!Matches(serverKey) || f.Seen == null) return;
        var ev = RequestEvent.FromLog(f, serverKey, model);
        lock (_lock)
        {
            // Proxy-Beobachtung desselben Endes schon da?
            int i = _pendingProxy.FindIndex(p => p.Px.ServerKey == serverKey && Math.Abs((p.Px.End - ev.End).TotalSeconds) <= MergeTolerance.TotalSeconds);
            if (i >= 0)
            {
                var p = _pendingProxy[i]; _pendingProxy.RemoveAt(i);
                EmitLocked(RequestEvent.Merge(ev, p.Px, serverKey, p.Name, p.Model));
                return;
            }
            _pendingLog.Add((ev, _now()));
        }
    }

    // Beobachtung des Proxys
    public void OnProxyRecord(ProxyRecord px, string serverName, string model)
    {
        if (!Matches(px.ServerKey)) return;
        lock (_lock)
        {
            int i = _pendingLog.FindIndex(l => l.Ev.ServerKey == px.ServerKey && Math.Abs((l.Ev.End - px.End).TotalSeconds) <= MergeTolerance.TotalSeconds);
            if (i >= 0)
            {
                var l = _pendingLog[i]; _pendingLog.RemoveAt(i);
                EmitLocked(RequestEvent.Merge(l.Ev, px, px.ServerKey, serverName, model));
                return;
            }
            _pendingProxy.Add((px, _now(), serverName, model));
        }
    }

    // Was nach Ablauf des Zeitfensters keinen Partner gefunden hat, wird allein ausgegeben
    public void FlushPending(DateTime now, bool all = false)
    {
        lock (_lock)
        {
            foreach (var l in _pendingLog.Where(x => all || now - x.At >= MergeWindow).ToList()) { _pendingLog.Remove(l); EmitLocked(l.Ev); }
            foreach (var p in _pendingProxy.Where(x => all || now - x.At >= MergeWindow).ToList())
            {
                _pendingProxy.Remove(p);
                EmitLocked(RequestEvent.Merge(null, p.Px, p.Px.ServerKey, p.Name, p.Model));
            }
        }
    }

    private void EmitLocked(RequestEvent e)
    {
        Requests++;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("t", "r"); w.WriteString("k", e.ServerKey); w.WriteString("name", e.ServerName); w.WriteString("model", e.Model);
            w.WriteNumber("task", e.Task); w.WriteNumber("slot", e.Slot);
            w.WriteString("start", e.Start.ToString("o", Inv)); w.WriteString("end", e.End.ToString("o", Inv));
            w.WriteNumber("psec", Math.Round(e.PromptSec, 3)); w.WriteNumber("tsec", Math.Round(e.ThinkSec, 3)); w.WriteNumber("asec", Math.Round(e.AnswerSec, 3));
            w.WriteNumber("ptok", e.PromptTokens); w.WriteNumber("ctok", e.CachedTokens); w.WriteNumber("gtok", e.GenTokens);
            w.WriteNumber("rtok", e.ReasoningTokens); w.WriteNumber("otok", e.ContentTokens);
            w.WriteNumber("ptps", Math.Round(e.PromptTps, 1)); w.WriteNumber("gtps", Math.Round(e.GenTps, 1)); w.WriteNumber("ttft", Math.Round(e.TtftSec, 3));
            w.WriteString("status", e.Status);
            if (e.FinishReason != null) w.WriteString("finish", e.FinishReason);
            w.WriteStartArray("tools"); foreach (var t in e.Tools) w.WriteStringValue(t); w.WriteEndArray();
            w.WriteString("client", e.Client);
            if (e.FromProxy) w.WriteBoolean("px", true);
            if (e.LogSeen) w.WriteBoolean("lg", true);
            w.WriteEndObject();
        }
        if (!_closed) { var text = Encoding.UTF8.GetString(ms.ToArray()); _w.WriteLine(text); _w.Flush(); _bytes += text.Length + 1; }
    }

    // Aufnahme beenden: offene Anfragen ausgeben, Ende-Zeile und Zusammenfassung schreiben
    public RecordingSummary? Stop()
    {
        FlushPending(_now(), all: true);
        WriteLine(w => { w.WriteString("t", "end"); w.WriteString("ended", _now().ToString("o", Inv)); });
        lock (_lock) { if (!_closed) { _w.Flush(); _w.Dispose(); _closed = true; } }
        var d = RecordingStore.Load(File);
        if (d == null) return null;
        var s = RecordingStore.Summarize(d, File);
        RecordingStore.SaveSummary(File, s);
        return s;
    }

    public void Dispose() { if (!_closed) Stop(); }
}

// Verwaltet laufende Aufnahmen. Der UI-Takt ruft Tick auf; Log- und Proxy-Ereignisse werden an alle Sitzungen verteilt.
public sealed class RecordingManager : IDisposable
{
    private readonly string _dir;
    private readonly List<RecordingSession> _sessions = new();
    public IReadOnlyList<RecordingSession> Sessions { get { lock (_sessions) return _sessions.ToList(); } }
    public bool Active { get { lock (_sessions) return _sessions.Count > 0; } }
    public event Action<RecordingSummary>? Finished;

    public RecordingManager(string dir) => _dir = dir;

    public bool IsRecording(string key) { lock (_sessions) return _sessions.Any(s => s.IsGlobal || s.Target == key); }
    public bool IsRecordingGlobal { get { lock (_sessions) return _sessions.Any(s => s.IsGlobal); } }
    public RecordingSession? SessionFor(string key) { lock (_sessions) return _sessions.FirstOrDefault(s => s.Target == key) ?? _sessions.FirstOrDefault(s => s.IsGlobal); }

    // target = Schlüssel des Servers oder "all"
    public RecordingSession Start(string target, IReadOnlyList<ServerWatcher> servers, string gpuName, bool proxy)
    {
        var scope = target == "all" ? servers : servers.Where(s => s.Key == target).ToList();
        var s = new RecordingSession(_dir, target, scope, gpuName, proxy);
        lock (_sessions) _sessions.Add(s);
        return s;
    }

    public RecordingSummary? Stop(RecordingSession s)
    {
        lock (_sessions) _sessions.Remove(s);
        var sum = s.Stop();
        if (sum != null) Finished?.Invoke(sum);
        return sum;
    }

    public void StopAll() { foreach (var s in Sessions) Stop(s); }

    // Größe, ab der eine einzelne Aufnahme beendet werden soll (Tick meldet sie dann)
    public long MaxSessionBytes { get; set; } = 100L * 1024 * 1024;

    // Ein Messpunkt für alle laufenden Aufnahmen; gibt die zurück, die ihre Größengrenze erreicht haben (der Aufrufer beendet sie)
    public List<RecordingSession> Tick(IReadOnlyList<ServerWatcher> servers, GpuSample? gpu, SystemSample? sys)
    {
        var over = new List<RecordingSession>();
        foreach (var s in Sessions)
        {
            try { s.Sample(servers, gpu, sys); } catch { }
            if (s.Bytes > MaxSessionBytes) over.Add(s);
        }
        return over;
    }

    // Aufnahme beenden, ohne den Aufrufer zu blockieren (Lesen, Zusammenfassen und Schreiben der Dateien laufen im Hintergrund)
    public Task<RecordingSummary?> StopAsync(RecordingSession s) => Task.Run(() => Stop(s));

    public int MaxRecordings { get; set; } = 200;
    public long MaxRecordingsBytes { get; set; } = 500L * 1024 * 1024;

    public void ApplyLimits(StorageLimits l)
    {
        MaxSessionBytes = l.MaxRecordingBytes; MaxRecordings = l.MaxRecordings; MaxRecordingsBytes = l.MaxRecordingsBytes;
    }

    // Älteste Aufnahmen löschen, wenn Anzahl oder Gesamtgröße die Grenzen überschreiten (laufende Aufnahmen bleiben)
    public Task<int> PruneAsync() => Task.Run(() => RecordingStore.Prune(_dir, MaxRecordings, MaxRecordingsBytes, Sessions.Select(s => s.File)));

    // Beim Start einmalig: Aufnahmen ohne Ende-Zeile (abgestürzt) abschließen
    public Task<int> RepairAsync(TimeSpan? minAge = null) => Task.Run(() => RecordingStore.RepairCrashed(_dir, Sessions.Select(s => s.File), minAge));

    public void OnLogRequest(FinishedRequest f, string serverKey, string model)
    {
        foreach (var s in Sessions) s.OnLogRequest(f, serverKey, model);
    }

    public void OnProxyRecord(ProxyRecord px, string serverName, string model)
    {
        foreach (var s in Sessions) s.OnProxyRecord(px, serverName, model);
    }

    public void Dispose() => StopAll();
}
