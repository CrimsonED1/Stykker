using System.Globalization;

namespace StykkerLlm.Core;

// Wie schnell ein Server anfängt zu antworten (Idee B1 aus docs/design/review-features-final.de.md): bei Agents mit langen
// Prompts bremst meist das Einlesen, nicht die Generierung. Median der letzten Anfragen:
// - PromptTps: Lesegeschwindigkeit laut Log (llama: „prompt eval“)
// - TtftSec: Zeit bis zum ersten Token, gemessen vom Proxy (Anfang bis erstes Token, mit Warteschlange). Ohne Proxy-Messung
//   die reine Lesedauer aus dem Log als Schätzung (TtftMeasured = false) – dann fehlt die Wartezeit.
public sealed record LatencyStats(double? PromptTps, double? TtftSec, bool TtftMeasured, int Samples);

public static class ServerLatency
{
    public const int Window = 20;

    public static LatencyStats Of(IEnumerable<FinishedRequest> finished, IEnumerable<ProxyRecord> proxy, string serverKey)
    {
        var done = finished.Where(f => f.ServerKey.Length == 0 || f.ServerKey == serverKey)
            .OrderByDescending(f => f.Seen ?? DateTime.MinValue).Take(Window).ToList();
        double? pp = Median(done.Where(f => f.PromptTps > 0 && f.PromptTokens > 0).Select(f => f.PromptTps));

        var measured = proxy.Where(p => p.ServerKey == serverKey && p.HttpStatus < 400 && Ttft(p) is not null)
            .OrderByDescending(p => p.End).Take(Window).Select(p => Ttft(p)!.Value).ToList();
        if (measured.Count > 0) return new LatencyStats(pp, Median(measured), true, measured.Count);

        var read = done.Where(f => f.PromptSec > 0).Select(f => f.PromptSec).ToList();
        return new LatencyStats(pp, Median(read), false, read.Count);
    }

    // Zeit bis zum ersten Token einer Anfrage durch den Proxy (Denk- oder Antwort-Token), null wenn unbekannt
    public static double? Ttft(ProxyRecord p)
    {
        var first = p.FirstTokenAt ?? p.FirstContentAt;
        return first is DateTime ft && ft >= p.Start ? (ft - p.Start).TotalSeconds : null;
    }

    public static double? Median(IEnumerable<double> values)
    {
        var v = values.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
        if (v.Count == 0) return null;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    // „0.42“ s bzw. „12“ s: unter 10 s mit zwei, darüber ohne Nachkommastellen (die Einheit steht daneben)
    public static string Seconds(double s) =>
        s.ToString(s < 10 ? "0.00" : "0", CultureInfo.InvariantCulture);
}
