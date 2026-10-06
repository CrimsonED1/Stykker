using System.Text;

namespace StykkerLlm.Cli.Tui;

// Ausgabebereich: alles, was Befehle auf Console.Out/Console.Error schreiben, landet hier (Zeilen mit Farbcodes).
// Thread-sicher: Befehle laufen im Hintergrund, gezeichnet wird in der Hauptschleife.
public sealed class LogPane
{
    public const int MaxLines = 3000;
    private readonly List<string> _lines = new();
    private readonly StringBuilder _partial = new();
    private readonly object _lock = new();
    public int Version { get; private set; }   // ändert sich bei jeder neuen Zeile (neu zeichnen)

    public void Append(string text)
    {
        lock (_lock)
        {
            foreach (char c in text)
            {
                if (c == '\r') continue;
                if (c == '\n') { Commit(); continue; }
                _partial.Append(c);
            }
            Version++;
        }
    }

    public void Line(string line = "") => Append(line + "\n");

    private void Commit()
    {
        _lines.Add(_partial.ToString().Replace("\t", "    "));
        _partial.Clear();
        if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
    }

    public void Clear() { lock (_lock) { _lines.Clear(); _partial.Clear(); Version++; } }

    // Umgebrochen auf width; die angefangene Zeile zählt mit
    public List<string> Render(int width)
    {
        lock (_lock)
        {
            var result = new List<string>();
            foreach (var l in _lines) result.AddRange(Ansi.Wrap(l, width));
            if (_partial.Length > 0) result.AddRange(Ansi.Wrap(_partial.ToString(), width));
            return result;
        }
    }

    public string PlainText() { lock (_lock) return string.Join("\n", _lines.Select(Ansi.Strip)); }

    public TextWriter Writer() => new LogWriter(this);

    private sealed class LogWriter(LogPane pane) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => pane.Append(value.ToString());
        public override void Write(string? value) { if (value != null) pane.Append(value); }
        public override void WriteLine(string? value) => pane.Append((value ?? "") + "\n");
    }
}
