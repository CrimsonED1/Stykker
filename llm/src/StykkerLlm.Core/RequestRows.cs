namespace StykkerLlm.Core;

// Zeilen der Liste „Letzte Anfragen“ (docs/plan-ui-redesign.md, U7). Die Zahlen kommen aus dem Log des Servers
// (FinishedRequest); lief die Anfrage durch den Stykker-Proxy, ergänzt dessen Beobachtung (ProxyRecord) Werkzeugaufrufe,
// Denk-Tokens, den Client und das Ende (abgebrochen, Fehler). Nur Zahlen und Namen – nie Inhalte.
public sealed record RequestRow(
    string Server, string ServerKey, string? Host, string Model, DateTime? Seen, double Seconds,
    double PromptTps, double GenTps, int GenTokens, string Client, string ClientKind,
    int? Tools, int? ThinkTokens, string Result);

public static class RequestRows
{
    // Wie weit Ende laut Log und Ende laut Proxy auseinander liegen dürfen, um als dieselbe Anfrage zu gelten
    public static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(4);

    public static IReadOnlyList<RequestRow> Build(IEnumerable<(FinishedRequest F, string? Host)> finished, IReadOnlyList<ProxyRecord> proxy, int take = 8)
    {
        var used = new HashSet<ProxyRecord>();
        var rows = new List<RequestRow>();
        foreach (var (f, host) in finished.OrderByDescending(x => x.F.Seen ?? DateTime.MinValue).Take(take))
        {
            ProxyRecord? px = null;
            if (f.Seen is DateTime seen && host == null)
                px = proxy.Where(p => !used.Contains(p) && p.ServerKey == f.ServerKey && (p.End - seen).Duration() <= MatchWindow)
                          .OrderBy(p => (p.End - seen).Duration()).FirstOrDefault();
            if (px != null) used.Add(px);
            var client = px != null ? ClientFromAgent(px.UserAgent) : f.Client;
            rows.Add(new RequestRow(f.Server, f.ServerKey, host, f.Model, f.Seen, f.Seconds, f.PromptTps, f.GenTps, f.GenTokens,
                client, ClientKind(client), px?.Tools.Length, px is { ReasoningTokens: > 0 } ? px.ReasoningTokens : px != null ? 0 : null,
                Result(f, px)));
        }
        return rows;
    }

    // ok | full (Kontext/Längenlimit) | abort (Client hat abgebrochen) | err (Fehler-Status)
    public static string Result(FinishedRequest f, ProxyRecord? px)
    {
        if (px is { HttpStatus: >= 400 }) return "err";
        if (px is { ClientAborted: true } || f.Status == ReqStatus.Cancelled) return "abort";
        if (f.Status == ReqStatus.Truncated || px?.FinishReason == "length") return "full";
        return "ok";
    }

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

    // Farbstufe für tokens/s (1 langsam … 4 schnell)
    public static int SpeedLevel(double tps) => tps < 10 ? 1 : tps < 25 ? 2 : tps < 60 ? 3 : 4;
}
