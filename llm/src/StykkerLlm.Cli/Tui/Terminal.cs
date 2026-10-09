using System.Runtime.InteropServices;
using System.Text;

namespace StykkerLlm.Cli.Tui;

// Wohin die Oberfläche zeichnet und woher die Tasten kommen. ConsoleTerminal ist das echte Terminal,
// VirtualTerminal dient Tests und Bildern (--snapshot): feste Größe, Tasten aus einem Skript, letztes Bild im Speicher.
public interface ITerminal
{
    int Width { get; }
    int Height { get; }
    bool KeyAvailable { get; }
    ConsoleKeyInfo ReadKey();
    // Ein ganzes Bild (eine Zeile je Bildschirmzeile, schon auf Breite gekürzt) und die Cursorposition (null = verstecken)
    void Present(IReadOnlyList<string> lines, (int Row, int Col)? cursor);
    void Enter();
    void Leave();
}

public sealed class ConsoleTerminal : ITerminal
{
    private readonly Stream _out;
    private string[] _last = Array.Empty<string>();
    private int _lastW, _lastH;

    public ConsoleTerminal()
    {
        _out = Console.OpenStandardOutput();   // vor dem Umleiten von Console.Out holen: dorthin schreiben die Befehle ins Protokoll
    }

    public int Width => Math.Max(20, SafeSize(() => Console.WindowWidth, 100));
    public int Height => Math.Max(10, SafeSize(() => Console.WindowHeight, 30));
    public bool KeyAvailable { get { try { return Console.KeyAvailable; } catch { return false; } } }
    public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);

    private static int SafeSize(Func<int> f, int fallback) { try { int v = f(); return v > 0 ? v : fallback; } catch { return fallback; } }

    public void Enter()
    {
        if (OperatingSystem.IsWindows()) { try { Console.OutputEncoding = new UTF8Encoding(false); } catch { } EnableVt(); }
        try { Console.TreatControlCAsInput = true; } catch { }
        Write("\u001b[?1049h\u001b[?25l\u001b[2J");   // eigener Bildschirm (Alt-Screen), Cursor aus
    }

    public void Leave()
    {
        Write("\u001b[0m\u001b[?25h\u001b[?1049l");
        try { Console.TreatControlCAsInput = false; } catch { }
    }

    public void Present(IReadOnlyList<string> lines, (int Row, int Col)? cursor)
    {
        int w = Width, h = Height;
        bool full = w != _lastW || h != _lastH;
        var sb = new StringBuilder();
        sb.Append("\u001b[?25l");
        if (full) { sb.Append("\u001b[2J"); _last = Array.Empty<string>(); _lastW = w; _lastH = h; }
        for (int r = 0; r < lines.Count && r < h; r++)
        {
            if (r < _last.Length && _last[r] == lines[r]) continue;   // nur geänderte Zeilen schreiben: kein Flackern, wenig Last über SSH
            sb.Append("\u001b[").Append(r + 1).Append(";1H").Append(lines[r]).Append("\u001b[0m");
            // Rest der Zeile nur löschen, wenn sie kürzer ist: nach dem Zeichen in der letzten Spalte würde ESC[K genau dieses
            // Zeichen löschen (das Terminal steht dort auf "Umbruch ausstehend") – so fehlte der rechte Rahmen
            if (Ansi.Width(lines[r]) < w) sb.Append("\u001b[K");
        }
        if (cursor is { } c) sb.Append("\u001b[").Append(c.Row + 1).Append(';').Append(c.Col + 1).Append("H\u001b[?25h");
        _last = lines.ToArray();
        Write(sb.ToString());
    }

    private void Write(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        try { _out.Write(bytes, 0, bytes.Length); _out.Flush(); } catch { }
    }

    private const int StdOutputHandle = -11;
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr h, uint mode);

    private static void EnableVt()
    {
        try
        {
            var h = GetStdHandle(StdOutputHandle);
            if (GetConsoleMode(h, out uint m)) SetConsoleMode(h, m | 0x0004 | 0x0008);   // VT-Verarbeitung, kein automatischer Umbruch am Rand
        }
        catch { }
    }
}

// Für Tests und --snapshot. Tasten-Skript: normale Zeichen werden getippt; {enter} {tab} {esc} {up} {down} {left} {right}
// {pgup} {pgdn} {bs} {ctrl-c} {wait 1500} (Millisekunden warten, die Oberfläche läuft weiter).
public sealed class VirtualTerminal : ITerminal
{
    private readonly Queue<object> _script = new();   // ConsoleKeyInfo oder int (Wartezeit)
    private DateTime _waitUntil = DateTime.MinValue;
    public int Width { get; }
    public int Height { get; }
    public string[] Screen { get; private set; } = Array.Empty<string>();
    public (int Row, int Col)? Cursor { get; private set; }
    public bool ScriptDone => _script.Count == 0 && DateTime.Now >= _waitUntil;

    public VirtualTerminal(int width, int height, string script = "")
    {
        Width = width; Height = height;
        Parse(script);
    }

    private void Parse(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '{')
            {
                int j = s.IndexOf('}', i);
                if (j > i)
                {
                    var tok = s[(i + 1)..j].Trim().ToLowerInvariant();
                    i = j;
                    if (tok.StartsWith("wait")) { _script.Enqueue(int.TryParse(tok[4..].Trim(), out int ms) ? ms : 1000); continue; }
                    _script.Enqueue(tok switch
                    {
                        "enter" => K('\r', ConsoleKey.Enter), "tab" => K('\t', ConsoleKey.Tab), "esc" => K('\u001b', ConsoleKey.Escape),
                        "up" => K('\0', ConsoleKey.UpArrow), "down" => K('\0', ConsoleKey.DownArrow), "left" => K('\0', ConsoleKey.LeftArrow),
                        "right" => K('\0', ConsoleKey.RightArrow), "pgup" => K('\0', ConsoleKey.PageUp), "pgdn" => K('\0', ConsoleKey.PageDown),
                        "bs" => K('\b', ConsoleKey.Backspace), "ctrl-c" => new ConsoleKeyInfo('\u0003', ConsoleKey.C, false, false, true),
                        _ => K('?', ConsoleKey.Oem2),
                    });
                    continue;
                }
            }
            _script.Enqueue(K(s[i], char.IsLetter(s[i]) ? (ConsoleKey)char.ToUpperInvariant(s[i]) : ConsoleKey.Oem1));
        }
    }

    private static ConsoleKeyInfo K(char c, ConsoleKey k) => new(c, k, false, false, false);

    public bool KeyAvailable
    {
        get
        {
            if (DateTime.Now < _waitUntil) return false;
            while (_script.Count > 0 && _script.Peek() is int ms) { _script.Dequeue(); _waitUntil = DateTime.Now.AddMilliseconds(ms); return false; }
            return _script.Count > 0;
        }
    }

    public ConsoleKeyInfo ReadKey() => (ConsoleKeyInfo)_script.Dequeue();
    public void Present(IReadOnlyList<string> lines, (int Row, int Col)? cursor) { Screen = lines.ToArray(); Cursor = cursor; }
    public void Enter() { }
    public void Leave() { }
}
