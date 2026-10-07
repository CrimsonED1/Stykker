using System.Runtime.InteropServices;
using System.Text;

namespace StykkerLlm.Cli;

// Ausgabe ins Terminal: Farben nur, wenn es ein Terminal ist und NO_COLOR/--color none es nicht verbieten.
// Einzelbefehle nutzen nur die 16 Grundfarben (überall lesbar); TrueColor bleibt dem Dashboard (C2) vorbehalten.
public static class Out
{
    public static bool Color { get; private set; }
    public static bool Unicode { get; private set; } = true;

    public static void Init()
    {
        bool tty = !Console.IsOutputRedirected;
        Color = tty && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")) && Environment.GetEnvironmentVariable("TERM") != "dumb";
        if (OperatingSystem.IsWindows())
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            if (Color && !EnableVirtualTerminal()) Color = false;   // altes conhost ohne VT: lieber ohne Farbe als mit Steuerzeichen
        }
    }

    public static void SetColor(bool on) => Color = on;
    public static void SetUnicode(bool on) => Unicode = on;

    public static string Paint(string text, int sgr) => Color ? $"\u001b[{sgr}m{text}\u001b[0m" : text;
    public static string Dim(string t) => Paint(t, 2);
    public static string Bold(string t) => Paint(t, 1);
    public static string Green(string t) => Paint(t, 32);
    public static string Yellow(string t) => Paint(t, 33);
    public static string Red(string t) => Paint(t, 31);
    public static string Cyan(string t) => Paint(t, 36);

    public static string Dot(bool on) => Unicode ? (on ? "●" : "○") : (on ? "*" : "o");

    public static void Error(string message) => Console.Error.WriteLine(Paint("error: ", 31) + message);
    public static void Warn(string message) => Console.Error.WriteLine(Paint("warning: ", 33) + message);

    // Sichtbare Länge ohne Farbcodes
    public static int VisibleLength(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\u001b') { while (i < s.Length && s[i] != 'm') i++; continue; }
            n++;
        }
        return n;
    }

    public static string Pad(string s, int width) => s + new string(' ', Math.Max(0, width - VisibleLength(s)));

    public static string Cut(string s, int width) => s.Length <= width ? s : width <= 1 ? s[..width] : s[..(width - 1)] + (Unicode ? "…" : "~");

    // Einfache Tabelle: Spaltenbreite = längster Eintrag; die letzte Spalte wird nicht aufgefüllt
    public static void Table(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows, TextWriter? w = null)
    {
        w ??= Console.Out;
        var all = rows.ToList();
        var width = header.Select(VisibleLength).ToArray();
        foreach (var r in all)
            for (int i = 0; i < r.Count && i < width.Length; i++) width[i] = Math.Max(width[i], VisibleLength(r[i]));
        string Line(IReadOnlyList<string> cells) =>
            string.Join("  ", cells.Select((c, i) => i == cells.Count - 1 ? c : Pad(c, width[i]))).TrimEnd();
        w.WriteLine(Dim(Line(header)));
        foreach (var r in all) w.WriteLine(Line(r));
    }

    public static string Gb(double? gb) => gb is double g ? $"{g:0.0} GB" : "–";

    // ── Windows: VT-Verarbeitung für ANSI-Farben im klassischen Konsolenfenster einschalten ──
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private static bool EnableVirtualTerminal()
    {
        try
        {
            var h = GetStdHandle(StdOutputHandle);
            if (!GetConsoleMode(h, out uint mode)) return false;
            return (mode & EnableVirtualTerminalProcessing) != 0 || SetConsoleMode(h, mode | EnableVirtualTerminalProcessing);
        }
        catch { return false; }
    }
}
