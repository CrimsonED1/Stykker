using System.Text;

namespace StykkerLlm.Cli.Tui;

// Text mit eingebetteten Farbcodes (SGR) messen, kürzen und umbrechen. Jedes Zeichen außerhalb einer Steuerfolge zählt als
// eine Spalte (Blockzeichen, Rahmen und Latin sind einspaltig; breite CJK-Zeichen kommen in der Oberfläche nicht vor).
public static class Ansi
{
    public const string Esc = "\u001b", Reset = "\u001b[0m";

    // Länge der Steuerfolge ab i (CSI "ESC [ … Endbyte" oder ESC + ein Zeichen); 0 = keine
    private static int SeqLength(string s, int i)
    {
        if (s[i] != '\u001b') return 0;
        if (i + 1 >= s.Length) return 1;
        if (s[i + 1] != '[') return 2;
        int j = i + 2;
        while (j < s.Length && (s[j] < '@' || s[j] > '~')) j++;
        return Math.Min(s.Length, j + 1) - i;
    }

    public static int Width(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length;)
        {
            int k = SeqLength(s, i);
            if (k > 0) { i += k; continue; }
            if (!char.IsLowSurrogate(s[i])) n++;
            i++;
        }
        return n;
    }

    public static string Strip(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length;)
        {
            int k = SeqLength(s, i);
            if (k > 0) { i += k; continue; }
            sb.Append(s[i++]);
        }
        return sb.ToString();
    }

    // Auf width Spalten kürzen (mit "…"); pad: mit Leerzeichen auffüllen. Endet mit Reset, wenn Farben vorkamen.
    public static string Fit(string s, int width, bool pad = false, string ellipsis = "…")
    {
        if (width <= 0) return "";
        int w = Width(s);
        var sb = new StringBuilder(s.Length + 8);
        bool colored = false;
        if (w <= width) { sb.Append(s); colored = s.Contains('\u001b'); }
        else
        {
            int keep = Math.Max(0, width - ellipsis.Length), n = 0;
            for (int i = 0; i < s.Length;)
            {
                int k = SeqLength(s, i);
                if (k > 0) { sb.Append(s, i, k); colored = true; i += k; continue; }
                if (n >= keep) break;
                sb.Append(s[i]);
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length) sb.Append(s[++i]);
                n++; i++;
            }
            sb.Append(ellipsis);
            w = width;
        }
        if (colored) sb.Append(Reset);
        if (pad && w < width) sb.Append(' ', width - w);
        return sb.ToString();
    }

    // Umbruch nach Spalten, bevorzugt an Leerzeichen (sonst hart); die zuletzt gesetzte Farbe gilt in der Folgezeile weiter
    public static List<string> Wrap(string s, int width)
    {
        var lines = new List<string>();
        if (width <= 0) { lines.Add(""); return lines; }
        // in sichtbare Zeichen mit der jeweils davor stehenden Steuerfolge zerlegen
        var cells = new List<(string Pre, string Ch)>();
        var pre = new StringBuilder();
        string trailing = "";
        for (int i = 0; i < s.Length;)
        {
            int k = SeqLength(s, i);
            if (k > 0) { pre.Append(s, i, k); i += k; continue; }
            int len = char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
            cells.Add((pre.ToString(), s.Substring(i, len)));
            pre.Clear();
            i += len;
        }
        trailing = pre.ToString();
        if (cells.Count <= width) { lines.Add(s); return lines; }

        var active = new StringBuilder();
        int pos = 0;
        while (pos < cells.Count)
        {
            int take = Math.Min(width, cells.Count - pos);
            if (pos + take < cells.Count)
            {
                // letztes Leerzeichen in der Zeile suchen (nicht zu weit vorn, sonst lieber hart umbrechen)
                for (int j = take; j > width / 2; j--)
                    if (cells[pos + j - 1].Ch == " ") { take = j; break; }
            }
            var sb = new StringBuilder(active.ToString());
            for (int j = pos; j < pos + take; j++)
            {
                var (p, ch) = cells[j];
                if (p.Length > 0)
                {
                    sb.Append(p);
                    foreach (var seq in SplitSeqs(p))
                        if (seq.EndsWith('m')) { if (seq is "\u001b[0m" or "\u001b[m") active.Clear(); else active.Append(seq); }
                }
                sb.Append(ch);
            }
            pos += take;
            while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;   // Leerzeichen am Umbruch weg (vor dem Reset)
            if (pos >= cells.Count) sb.Append(trailing);
            else if (active.Length > 0) sb.Append(Reset);
            lines.Add(sb.ToString());
        }
        return lines;
    }

    private static IEnumerable<string> SplitSeqs(string p)
    {
        for (int i = 0; i < p.Length;)
        {
            int k = Math.Max(1, SeqLength(p, i));
            yield return p.Substring(i, k);
            i += k;
        }
    }
}
