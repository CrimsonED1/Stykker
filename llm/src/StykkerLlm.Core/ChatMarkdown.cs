using System.Text;

namespace StykkerLlm.Core;

// Antworten eines Modells als Markdown lesen (Seite /prompt): Absätze, Überschriften, Listen, Zitate, Tabellen,
// Code-Blöcke mit Sprache, dazu im Text `Code`, **fett**, *kursiv* und [Links](https://…). Ergebnis sind nur Blöcke und
// Textstücke – kein HTML. Die Seite baut daraus Elemente, damit nichts aus der Antwort als Markup in die Seite gelangt.
// Ein nicht geschlossener Code-Block (die Antwort läuft noch) reicht bis zum Ende: so springt beim Streamen nichts um.
public enum MdKind { Paragraph, Heading, Code, List, Quote, Table, Rule }

public enum MdSpanKind { Text, Code, Bold, Italic, Link }

public sealed record MdSpan(MdSpanKind Kind, string Text, string? Href = null);

public sealed record MdItem(IReadOnlyList<MdSpan> Spans, int Depth);

public sealed record MdBlock(MdKind Kind)
{
    public string Text { get; init; } = "";                                   // Code: der Inhalt
    public string Language { get; init; } = "";                               // Code: Sprache hinter ```
    public bool Open { get; init; }                                           // Code: noch nicht geschlossen
    public int Level { get; init; }                                           // Heading: 1–6
    public bool Ordered { get; init; }                                        // List
    public int Start { get; init; } = 1;                                      // List: erste Nummer
    public IReadOnlyList<MdSpan> Spans { get; init; } = Array.Empty<MdSpan>();          // Paragraph, Heading, Quote
    public IReadOnlyList<MdItem> Items { get; init; } = Array.Empty<MdItem>();          // List
    public IReadOnlyList<IReadOnlyList<MdSpan>> Header { get; init; } = Array.Empty<IReadOnlyList<MdSpan>>();   // Table
    public IReadOnlyList<IReadOnlyList<IReadOnlyList<MdSpan>>> Rows { get; init; } = Array.Empty<IReadOnlyList<IReadOnlyList<MdSpan>>>();
}

public static class ChatMarkdown
{
    public static IReadOnlyList<MdBlock> Parse(string? text)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrEmpty(text)) return blocks;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int i = 0;
        var para = new List<string>();

        void FlushPara()
        {
            if (para.Count == 0) return;
            blocks.Add(new MdBlock(MdKind.Paragraph) { Spans = Inline(string.Join("\n", para)) });
            para.Clear();
        }

        while (i < lines.Length)
        {
            var line = lines[i];
            var t = line.TrimStart();

            // Code-Block: ``` oder ~~~, Sprache dahinter; bis zum gleichen Zaun oder zum Ende
            if (Fence(t) is { } fence)
            {
                FlushPara();
                var lang = t[fence.Length..].Trim();
                var code = new List<string>();
                i++;
                bool closed = false;
                while (i < lines.Length)
                {
                    if (lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal) && lines[i].Trim().Trim(fence[0]).Length == 0) { closed = true; i++; break; }
                    code.Add(lines[i]);
                    i++;
                }
                blocks.Add(new MdBlock(MdKind.Code) { Text = string.Join("\n", code), Language = lang.Split(' ')[0], Open = !closed });
                continue;
            }

            if (t.Length == 0) { FlushPara(); i++; continue; }

            if (IsRule(t)) { FlushPara(); blocks.Add(new MdBlock(MdKind.Rule)); i++; continue; }

            if (Heading(t) is var (level, htext) && level > 0)
            {
                FlushPara();
                blocks.Add(new MdBlock(MdKind.Heading) { Level = level, Spans = Inline(htext) });
                i++;
                continue;
            }

            if (t.StartsWith('>'))
            {
                FlushPara();
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                {
                    var q = lines[i].TrimStart()[1..];
                    quote.Add(q.StartsWith(' ') ? q[1..] : q);
                    i++;
                }
                blocks.Add(new MdBlock(MdKind.Quote) { Spans = Inline(string.Join("\n", quote)) });
                continue;
            }

            // Tabelle: Kopfzeile mit |, darunter eine Trennzeile aus | - :
            if (t.Contains('|') && i + 1 < lines.Length && IsTableRule(lines[i + 1]))
            {
                FlushPara();
                var header = Cells(t).Select(Inline).ToList();
                var rows = new List<IReadOnlyList<IReadOnlyList<MdSpan>>>();
                i += 2;
                while (i < lines.Length && lines[i].Contains('|') && lines[i].Trim().Length > 0)
                {
                    var cells = Cells(lines[i].Trim()).Select(Inline).ToList();
                    while (cells.Count < header.Count) cells.Add(Array.Empty<MdSpan>());
                    rows.Add(cells.Take(header.Count).ToList());
                    i++;
                }
                blocks.Add(new MdBlock(MdKind.Table) { Header = header, Rows = rows });
                continue;
            }

            if (ListMarker(line) is { } first)
            {
                FlushPara();
                var items = new List<MdItem>();
                int baseIndent = first.Indent;
                var current = new StringBuilder(first.Text);
                int depth = 0;
                i++;
                while (i < lines.Length)
                {
                    var l = lines[i];
                    if (l.Trim().Length == 0)
                    {
                        // eine Leerzeile beendet die Liste nur, wenn danach kein weiterer Punkt kommt
                        if (i + 1 < lines.Length && ListMarker(lines[i + 1]) is { } nx && nx.Ordered == first.Ordered) { i++; continue; }
                        break;
                    }
                    if (ListMarker(l) is { } m)
                    {
                        items.Add(new MdItem(Inline(current.ToString()), depth));
                        current.Clear().Append(m.Text);
                        depth = Math.Clamp((m.Indent - baseIndent) / 2, 0, 3);
                        i++;
                        continue;
                    }
                    if (l.StartsWith(' ') || l.StartsWith('\t')) { current.Append('\n').Append(l.Trim()); i++; continue; }
                    break;
                }
                items.Add(new MdItem(Inline(current.ToString()), depth));
                blocks.Add(new MdBlock(MdKind.List) { Ordered = first.Ordered, Start = first.Number, Items = items });
                continue;
            }

            para.Add(line);
            i++;
        }
        FlushPara();
        return blocks;
    }

    private static string? Fence(string t) =>
        t.StartsWith("```", StringComparison.Ordinal) ? new string('`', t.TakeWhile(c => c == '`').Count())
        : t.StartsWith("~~~", StringComparison.Ordinal) ? new string('~', t.TakeWhile(c => c == '~').Count())
        : null;

    private static bool IsRule(string t)
    {
        var s = t.Replace(" ", "");
        return s.Length >= 3 && (s.All(c => c == '-') || s.All(c => c == '*') || s.All(c => c == '_'));
    }

    private static (int Level, string Text) Heading(string t)
    {
        int n = t.TakeWhile(c => c == '#').Count();
        if (n is < 1 or > 6 || t.Length <= n || t[n] != ' ') return (0, "");
        return (n, t[(n + 1)..].TrimEnd().TrimEnd('#').TrimEnd());
    }

    private static bool IsTableRule(string line)
    {
        var s = line.Trim();
        return s.Contains('-') && s.Contains('|') && s.All(c => c is '|' or '-' or ':' or ' ');
    }

    private static List<string> Cells(string row)
    {
        var r = row.Trim();
        if (r.StartsWith('|')) r = r[1..];
        if (r.EndsWith('|')) r = r[..^1];
        return r.Split('|').Select(c => c.Trim()).ToList();
    }

    private sealed record Marker(int Indent, bool Ordered, int Number, string Text);

    // „- x“, „* x“, „+ x“, „1. x“, „1) x“ (mit Einrückung)
    private static Marker? ListMarker(string line)
    {
        int indent = line.TakeWhile(c => c == ' ').Count();
        var t = line[indent..];
        if (t.Length >= 2 && t[0] is '-' or '*' or '+' && t[1] == ' ' && !IsRule(t)) return new Marker(indent, false, 1, t[2..]);
        int digits = t.TakeWhile(char.IsAsciiDigit).Count();
        if (digits is > 0 and < 10 && t.Length > digits + 1 && t[digits] is '.' or ')' && t[digits + 1] == ' ')
            return new Marker(indent, true, int.Parse(t[..digits], System.Globalization.CultureInfo.InvariantCulture), t[(digits + 2)..]);
        return null;
    }

    // Text einer Zeile: `Code` zuerst (darin gilt nichts anderes), dann **fett**, *kursiv*/_kursiv_ und [Text](http…)
    public static IReadOnlyList<MdSpan> Inline(string text)
    {
        var spans = new List<MdSpan>();
        var plain = new StringBuilder();
        void Flush() { if (plain.Length > 0) { spans.Add(new MdSpan(MdSpanKind.Text, plain.ToString())); plain.Clear(); } }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '`')
            {
                int ticks = 1;
                while (i + ticks < text.Length && text[i + ticks] == '`') ticks++;
                var fence = new string('`', ticks);
                int end = text.IndexOf(fence, i + ticks, StringComparison.Ordinal);
                if (end > i + ticks - 1)
                {
                    Flush();
                    spans.Add(new MdSpan(MdSpanKind.Code, text[(i + ticks)..end].Trim()));
                    i = end + ticks;
                    continue;
                }
            }
            else if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                var mark = new string(c, 2);
                int end = text.IndexOf(mark, i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    spans.Add(new MdSpan(MdSpanKind.Bold, text[(i + 2)..end]));
                    i = end + 2;
                    continue;
                }
            }
            else if ((c == '*' || c == '_') && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && (c == '*' || i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                int end = text.IndexOf(c, i + 1);
                if (end > i + 1 && !char.IsWhiteSpace(text[end - 1]) && (c == '*' || end + 1 >= text.Length || !char.IsLetterOrDigit(text[end + 1])))
                {
                    Flush();
                    spans.Add(new MdSpan(MdSpanKind.Italic, text[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
            }
            else if (c == '[')
            {
                int close = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                int paren = close > 0 ? text.IndexOf(')', close + 2) : -1;
                if (close > i + 1 && paren > close + 2)
                {
                    var href = text[(close + 2)..paren].Trim();
                    if (href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    {
                        Flush();
                        spans.Add(new MdSpan(MdSpanKind.Link, text[(i + 1)..close], href));
                        i = paren + 1;
                        continue;
                    }
                }
            }
            plain.Append(c);
            i++;
        }
        Flush();
        return spans;
    }

    // Die ganze Unterhaltung als Markdown zum Kopieren: „**You:**“ / „**Model:**“ vor jedem Beitrag, Werkzeuge gekürzt
    public static string Conversation(IEnumerable<ChatTurn> turns)
    {
        var sb = new StringBuilder();
        foreach (var t in turns)
        {
            if (t.Role == "tool") { sb.Append("> tool result: ").AppendLine(Short(t.Content, 300).Replace("\n", " ")).AppendLine(); continue; }
            sb.Append(t.Role == "user" ? "**You:**" : "**Model:**").AppendLine();
            if (t.Content.Length > 0) sb.AppendLine(t.Content.TrimEnd());
            foreach (var call in t.ToolCalls ?? Array.Empty<ToolCall>()) sb.Append("- tool call: `").Append(call.Summary).AppendLine("`");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + " …";
}
