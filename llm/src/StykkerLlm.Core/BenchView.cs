using System.Globalization;

namespace StykkerLlm.Core;

// Anzeige-Logik der Benchmark-Seite: was eine Ergebniszeile zeigt, welche Kontextgrößen zum Ziel passen und die Punkte der
// Diagramme „Lesen/Schreiben je Kontextgröße“. Die Seite rechnet nichts selbst (AGENTS.md: Logik in Core).
public static class BenchView
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Unter so vielen Prompt-Token sagt „Lesen t/s“ wenig: die feste Anlaufzeit überwiegt (Short chat hat ~30 Token)
    public const int MinReadTokens = 256;

    // Vorauswahl der Kontextgrößen: alle, die passen, aber höchstens bis 32k (größere dauern auf kleinen GPUs Minuten)
    public const int DefaultMaxSize = 32768;

    // Höchstens so viele Ergebnisse im Vergleich: eine Farbe je Ergebnis aus einer festen Reihe, nie zyklisch
    public const int MaxCompare = 4;

    public static string SizeLabel(int size) => size >= 1024 ? (size / 1024).ToString(Inv) + "k" : size.ToString(Inv);

    // Passt eine Kontextgröße in einen Slot? Unbekannte Slot-Größe: alles erlaubt (der Lauf überspringt, was nicht passt)
    public static bool Fits(int size, int? ctxPerSlot, int genTokens) => ctxPerSlot is not > 0 || BenchOptions.FitsSlot(size, genTokens, ctxPerSlot.Value);

    public static List<int> DefaultSizes(int? ctxPerSlot, int genTokens) => ctxPerSlot is > 0
        ? BenchOptions.Ladder.Where(s => s <= DefaultMaxSize && Fits(s, ctxPerSlot, genTokens)).ToList()
        : new List<int> { 1024, 4096 };

    // Eine Zeile der Ergebnistabelle. Read/Write sind fertige Texte („–“ ohne Wert); ReadWeak dämpft „Lesen“ bei kurzen Prompts.
    public sealed record Row(string Read, string Write, bool ReadWeak, string ReadTip, string WriteTip, string Result, string ResultTip, bool Comparable);

    public static Row RowOf(BenchStep s)
    {
        string read = s.PromptTps > 0 ? s.PromptTps.ToString("N0", Inv) : "–";
        string write = s.GenTps > 0 ? s.GenTps.ToString("0.0", Inv) : "–";
        string fail = s.Note.Length > 0 ? s.Note : Strings.BenchStepFailed;
        switch (s.Kind)
        {
            case "tool":
                // Ein Werkzeugaufruf ist bestanden oder nicht; Geschwindigkeiten von ~30 Token sagen nichts
                return new Row("–", "–", false, "", "", s.Ok ? Strings.BenchToolOk : fail, s.Note, false);
            case "parallel":
                // Die Kennzahl ist der Durchsatz aller Anfragen zusammen, je Anfrage steht im Tooltip
                return new Row(read, s.AggregateTps > 0 ? "Σ " + s.AggregateTps.ToString("0.0", Inv) : "–", false, "",
                    Strings.BenchParallelTip(s.Parallel, s.AggregateTps, s.GenTps), s.Ok ? Strings.BenchLastOk : fail, s.Note, true);
            default:
                bool weak = s.PromptTokens is > 0 and < MinReadTokens;
                return new Row(read, write, weak, weak ? Strings.BenchReadWeakTip : "", "", s.Ok ? Strings.BenchLastOk : fail, s.Ok ? "" : s.Note, true);
        }
    }

    // ── Diagramm: Lesen oder Schreiben (t/s) über der Kontextgröße, eine Linie je Ergebnis ──

    public sealed record ChartDot(double X, double Y, string Tip);
    public sealed record ChartLine(string Name, string Points, IReadOnlyList<ChartDot> Dots);
    public sealed record ChartTick(double Pos, string Label);
    public sealed record Chart(IReadOnlyList<ChartLine> Lines, IReadOnlyList<ChartTick> XTicks, IReadOnlyList<ChartTick> YTicks)
    {
        public bool Empty => Lines.All(l => l.Dots.Count == 0);
    }

    // Zeichenfläche des SVG (viewBox), Ränder für die Beschriftung
    public const double W = 320, H = 150, Left = 34, Right = 10, Top = 8, Bottom = 20;

    // x logarithmisch (die Leiter verdoppelt sich: 1k, 2k, 4k …), y linear ab 0 bis zu einem runden Höchstwert
    public static Chart ContextChart(IReadOnlyList<BenchResult> results, bool read)
    {
        var data = results.Select(r => (r.Title, Steps: r.Steps.Where(s => s.Kind == "context" && s.Ok && s.TargetPromptTokens > 0 && (read ? s.PromptTps : s.GenTps) > 0)
            .OrderBy(s => s.TargetPromptTokens).ToList())).ToList();
        var sizes = data.SelectMany(d => d.Steps.Select(s => s.TargetPromptTokens)).Distinct().OrderBy(x => x).ToList();
        if (sizes.Count == 0) return new Chart(data.Select(d => new ChartLine(d.Title, "", Array.Empty<ChartDot>())).ToList(), Array.Empty<ChartTick>(), Array.Empty<ChartTick>());

        double max = NiceMax(data.SelectMany(d => d.Steps.Select(s => read ? s.PromptTps : s.GenTps)).Max());
        double lo = Math.Log2(sizes[0]), hi = Math.Log2(sizes[^1]);
        double X(int size) => hi > lo ? Left + (Math.Log2(size) - lo) / (hi - lo) * (W - Left - Right) : (Left + W - Right) / 2;
        double Y(double v) => Top + (1 - v / max) * (H - Top - Bottom);

        var lines = data.Select(d =>
        {
            var dots = d.Steps.Select(s =>
            {
                double v = read ? s.PromptTps : s.GenTps;
                return new ChartDot(Math.Round(X(s.TargetPromptTokens), 1), Math.Round(Y(v), 1), $"{d.Title} · {SizeLabel(s.TargetPromptTokens)}: {v.ToString(read ? "N0" : "0.0", Inv)} t/s");
            }).ToList();
            return new ChartLine(d.Title, string.Join(" ", dots.Select(p => p.X.ToString("0.#", Inv) + "," + p.Y.ToString("0.#", Inv))), dots);
        }).ToList();
        var xt = sizes.Select(s => new ChartTick(Math.Round(X(s), 1), SizeLabel(s))).ToList();
        var yt = new[] { 0, max / 2, max }.Select(v => new ChartTick(Math.Round(Y(v), 1), v.ToString("0", Inv))).ToList();
        return new Chart(lines, xt, yt);
    }

    // Runder Höchstwert für die y-Achse: 1, 2, 2.5 oder 5 mal einer Zehnerpotenz, mindestens 10 % über dem größten Wert
    public static double NiceMax(double v)
    {
        if (v <= 0) return 1;
        double target = v * 1.1, p = Math.Pow(10, Math.Floor(Math.Log10(target)));
        foreach (var f in new[] { 1, 2, 2.5, 5, 10 })
            if (f * p >= target) return f * p;
        return 10 * p;
    }
}
