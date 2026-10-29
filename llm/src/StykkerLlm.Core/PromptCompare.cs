using System.Globalization;
using System.Text;

namespace StykkerLlm.Core;

// Zwei Modelle mit derselben Frage (Seite /prompt, „Compare with a second model“): wer fängt schneller an (Zeit bis zum
// ersten Token), wer schreibt schneller (t/s). Unter 5 % Unterschied gilt als gleich – so schnell schwankt eine Messung.
// Faster: 0 = erstes Modell, 1 = zweites, -1 = gleich oder unbekannt.
public sealed record CompareSummary(int FasterStart, int FasterOutput, string Text);

public static class PromptCompare
{
    public const double Tie = 0.05;

    public static CompareSummary? Of(PromptResult? a, PromptResult? b, string nameA, string nameB)
    {
        if (a is not { Ok: true } || b is not { Ok: true }) return null;
        int start = Better(a.TtftMs, b.TtftMs, lowerWins: true);
        int output = Better(a.Tps, b.Tps, lowerWins: false);
        var inv = CultureInfo.InvariantCulture;
        string Who(int i) => i == 0 ? nameA : nameB;
        var parts = new List<string>
        {
            start < 0 ? Strings.CmpStartTie(a.TtftMs, b.TtftMs) : Strings.CmpStart(Who(start), Math.Min(a.TtftMs, b.TtftMs), Math.Max(a.TtftMs, b.TtftMs)),
            output < 0 ? Strings.CmpOutputTie(a.Tps, b.Tps) : Strings.CmpOutput(Who(output), Math.Max(a.Tps, b.Tps), Math.Min(a.Tps, b.Tps)),
        };
        return new CompareSummary(start, output, string.Join(" · ", parts));
    }

    // Welcher Wert ist besser? Beide unbekannt (0) oder weniger als Tie auseinander: keiner
    public static int Better(double a, double b, bool lowerWins)
    {
        if (a <= 0 || b <= 0) return -1;
        if (Math.Abs(a - b) / Math.Max(a, b) < Tie) return -1;
        return (a < b) == lowerWins ? 0 : 1;
    }

    // Beide Gespräche als Markdown zum Kopieren, mit dem Namen des Modells darüber
    public static string Markdown(string nameA, IEnumerable<ChatTurn> a, string nameB, IEnumerable<ChatTurn> b)
    {
        var sb = new StringBuilder();
        sb.Append("## ").AppendLine(nameA).AppendLine().Append(ChatMarkdown.Conversation(a)).AppendLine();
        sb.Append("## ").AppendLine(nameB).AppendLine().Append(ChatMarkdown.Conversation(b));
        return sb.ToString();
    }
}
