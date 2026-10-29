namespace StykkerLlm.Core;

// Zeilen der Liste „Letzte Anfragen“ (docs/plan-ui-redesign.md, U7). Die Zahlen kommen aus dem Log des Servers
// (FinishedRequest); lief die Anfrage durch den Stykker-Proxy, ergänzt dessen Beobachtung (ProxyRecord) Werkzeugaufrufe,
// Denk-Tokens, den Client und das Ende (abgebrochen, Fehler). Nur Zahlen und Namen – nie Inhalte.
public sealed record RequestRow(
    string Server, string ServerKey, string? Host, string Model, DateTime? Seen, double Seconds,
    double PromptTps, double GenTps, int GenTokens, string Client, string ClientKind,
    int? Tools, int? ThinkTokens, string Result, int PromptRead = 0, int PromptReused = -1, bool PromptReusedEstimated = false, string? Route = null,
    string UserAgent = "", DateTime? Start = null, int? HttpStatus = null, double? Ttft = null);

public static class RequestRows
{
    // Modelle, die in der Liste vorkommen (ohne leere Namen), sortiert – für den Filter „Model: All / …“
    public static IReadOnlyList<string> Models(IEnumerable<RequestRow> rows) =>
        rows.Select(r => r.Model).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();

    // Nur die Zeilen dieses Modells. Leer oder ein Modell, das in der Liste nicht (mehr) vorkommt, zeigt alle Zeilen.
    public static IReadOnlyList<RequestRow> ForModel(IReadOnlyList<RequestRow> rows, string? model)
    {
        if (string.IsNullOrEmpty(model) || !rows.Any(r => r.Model == model)) return rows;
        return rows.Where(r => r.Model == model).ToList();
    }

    // Beginn aus Ende und Dauer, wenn der Proxy den Beginn nicht kennt (Zeile aus dem Log)
    private static DateTime? StartOf(DateTime? seen, double seconds) =>
        seen is DateTime end && seconds > 0 ? end.AddSeconds(-seconds) : null;

    // Wie weit Ende laut Log und Ende laut Proxy auseinander liegen dürfen, um als dieselbe Anfrage zu gelten
    public static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(4);

    // Dazu die Anfragen an Cloud-Ziele: sie laufen nur durch den Proxy, nicht durch die Slots der lokalen Server.
    // nameOf liefert den Anzeigenamen eines Ziels (sonst zählt der Modellname aus der Kennung).
    public static IReadOnlyList<RequestRow> Build(IEnumerable<(FinishedRequest F, string? Host)> finished, IReadOnlyList<ProxyRecord> proxy, int take = 8,
        Func<string, string>? nameOf = null)
    {
        var all = finished.ToList();
        var used = new HashSet<ProxyRecord>();
        var rows = new List<RequestRow>();
        foreach (var (f, host) in all.OrderByDescending(x => x.F.Seen ?? DateTime.MinValue).Take(take))
        {
            ProxyRecord? px = null;
            if (f.Seen is DateTime seen && host == null)
                px = proxy.Where(p => !used.Contains(p) && p.ServerKey == f.ServerKey && (p.End - seen).Duration() <= MatchWindow)
                          .OrderBy(p => (p.End - seen).Duration()).FirstOrDefault();
            if (px != null) used.Add(px);
            var client = px != null ? ClientFromAgent(px.UserAgent) : f.Client;
            // Der Weg (auto/image) steht nur an der Zeile, wenn die Zuordnung eindeutig ist
            string? route = px != null && Unambiguous(px, f, all, proxy) ? px.Route : null;
            rows.Add(new RequestRow(f.Server, f.ServerKey, host, f.Model, f.Seen, f.Seconds, f.PromptTps, f.GenTps, f.GenTokens,
                client, ClientKind(client), px?.Tools.Length, px is { ReasoningTokens: > 0 } ? px.ReasoningTokens : px != null ? 0 : null,
                Result(f, px), f.PromptTokens, f.PromptReused, f.PromptReusedEstimated, route,
                px?.UserAgent ?? "", px?.Start ?? StartOf(f.Seen, f.Seconds), px is { HttpStatus: > 0 } ? px.HttpStatus : null,
                px != null ? ServerLatency.Ttft(px) : null));
        }
        var cloud = proxy.Where(p => ProxyTarget.IsCloudKey(p.ServerKey)).OrderByDescending(p => p.End).Take(take)
            .Select(p => CloudRow(p, nameOf));
        return rows.Concat(cloud).OrderByDescending(r => r.Seen ?? DateTime.MinValue).Take(take).ToList();
    }

    // Die Beobachtung eines Ziels, das nicht lokal läuft (Cloud): Name des Ziels, Host „cloud“, Zeiten und Zahlen, soweit bekannt
    private static RequestRow CloudRow(ProxyRecord p, Func<string, string>? nameOf)
    {
        string model = p.ServerKey[(p.ServerKey.IndexOf('|') + 1)..];
        string named = nameOf?.Invoke(p.ServerKey) ?? "";
        string name = named.Length > 0 && named != p.ServerKey ? named : model;
        // Wie im Protokoll (proxy.csv): ohne Denk-Zähler ist die Antwortlänge unbekannt
        bool known = p.ReasoningTokens >= 0;
        int tokens = known ? p.ReasoningTokens + Math.Max(0, p.ContentTokens) : 0;
        double gen = p.FirstTokenAt is DateTime ft && p.End > ft && tokens > 0 ? tokens / (p.End - ft).TotalSeconds : 0;
        string client = ClientFromAgent(p.UserAgent);
        return new RequestRow(name, p.ServerKey, Strings.ReqCloud, model, p.End, (p.End - p.Start).TotalSeconds, 0, gen, tokens,
            client, ClientKind(client), p.Tools.Length, p.ReasoningTokens >= 0 ? p.ReasoningTokens : null, ResultOf(p), 0, -1, false, p.Route,
            p.UserAgent, p.Start, p.HttpStatus > 0 ? p.HttpStatus : null, ServerLatency.Ttft(p));
    }

    // Die Zuordnung zu einer lokalen Zeile ist eindeutig, wenn im Fenster kein zweiter Proxy-Eintrag und keine zweite
    // lokale Anfrage desselben Servers liegt – sonst könnte ein Weg an der falschen Zeile stehen
    private static bool Unambiguous(ProxyRecord px, FinishedRequest f, List<(FinishedRequest F, string? Host)> all, IReadOnlyList<ProxyRecord> proxy)
    {
        if (f.Seen is not DateTime seen) return false;
        int sameProxy = proxy.Count(p => p.ServerKey == f.ServerKey && (p.End - seen).Duration() <= MatchWindow);
        int sameLog = all.Count(x => x.Host == null && x.F.ServerKey == f.ServerKey && x.F.Seen is DateTime s2 && (px.End - s2).Duration() <= MatchWindow);
        return sameProxy == 1 && sameLog == 1;
    }

    // ok | full (Kontext/Längenlimit) | abort (Client hat abgebrochen) | err (Fehler-Status)
    public static string Result(FinishedRequest f, ProxyRecord? px)
    {
        if (px is { HttpStatus: >= 400 }) return "err";
        if (px is { ClientAborted: true } || f.Status == ReqStatus.Cancelled) return "abort";
        if (f.Status == ReqStatus.Truncated || px?.FinishReason == "length") return "full";
        return "ok";
    }

    // Ergebnis einer Anfrage ohne Log-Zeile (Cloud-Ziel): dieselben Werte, nur aus der Beobachtung
    public static string ResultOf(ProxyRecord px) =>
        px.HttpStatus >= 400 ? "err" : px.ClientAborted ? "abort" : px.FinishReason == "length" ? "full" : "ok";

    // Art des Clients für das Symbol: editor, web, agent oder cli
    public static string ClientKind(string? client)
    {
        var c = (client ?? "").ToLowerInvariant();
        if (c.Length == 0) return "cli";
        if (c.Contains("mozilla") || c.Contains("chrome") || c.Contains("edge") || c.Contains("firefox") || c.Contains("safari") || c.Contains("browser") || c.Contains("web"))
            return "web";
        if (c.Contains("code") && !c.Contains("opencode") && !c.Contains("qwen code") && !c.Contains("claude code") || c.Contains("cursor") || c.Contains("zed") || c.Contains("vim") || c.Contains("jetbrains") || c.Contains("windsurf"))
            return "editor";
        if (c.Contains("opencode") || c.Contains("aider") || c.Contains("qwen") || c.Contains("claude") || c.Contains("cline") || c.Contains("goose") || c.Contains("agent") || c.Contains("codex"))
            return "agent";
        return "cli";
    }

    // Erster Teil des User-Agent („opencode/0.9 …“ → „opencode“), sonst leer
    public static string ClientFromAgent(string? ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return "";
        var first = ua.Trim().Split(' ', 2)[0];
        var slash = first.IndexOf('/');
        return slash > 0 ? first[..slash] : first;
    }

    // Spalte „cache“: "44.9k ↺" bei Wiederverwendung, "—" wenn der ganze Prompt neu gelesen wurde, leer wenn unbekannt
    public static string CacheText(int reused) => reused < 0 ? "" : reused == 0 ? "—" : Short(reused) + " ↺";

    // Geschätzte Werte (Strata-Fortschrittszeilen) bekommen den Hinweis dazu
    public static string CacheTip(int reused, int read, bool estimated = false) =>
        estimated && reused >= 0 ? Strings.ReqCacheTip(reused, read) + " (" + Strings.ReqCacheEstimated + ")" : Strings.ReqCacheTip(reused, read);

    private static string Short(int n) =>
        n >= 1_000_000 ? (n / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "M"
        : n >= 1000 ? (n / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "k"
        : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Farbstufe für tokens/s (1 langsam … 4 schnell)
    public static int SpeedLevel(double tps) => tps < 10 ? 1 : tps < 25 ? 2 : tps < 60 ? 3 : 4;
}
