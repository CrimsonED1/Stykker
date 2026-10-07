using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Cli.Tui;

// Interaktive Oberfläche (wie Claude Code): oben der Live-Status, darunter die Ausgabe der Befehle, unten die Eingabezeile.
// Befehle mit "/" (Vorschläge erscheinen beim Tippen), Rückfragen in derselben Zeile; man verlässt das Programm nicht.
public sealed class TuiApp
{
    private sealed record Cmd(string Name, string Args, string Help, params string[] Aliases);

    private static readonly Cmd[] Cmds =
    {
        new("status", "", Strings.TuiStatus),
        new("saved", "", Strings.TuiSaved, "profiles"),
        new("history", "", Strings.TuiHistory, "hist"),
        new("show", "<id|name>", Strings.TuiShow),
        new("start", "<name|id>", Strings.TuiStart),
        new("stop", "<name|port>", Strings.TuiStop),
        new("unload", "<server> <model>", Strings.TuiUnload),
        new("add", "<url> [name]", Strings.TuiAdd),
        new("remove", "<server>", Strings.TuiRemove),
        new("proxy", "[on|off|lan|serve <server>]", Strings.TuiProxy),
        new("notice", "[dismiss]", Strings.TuiNotice),
        new("recent", "[n]", Strings.TuiRecent),
        new("record", "", Strings.TuiRecord),
        new("recordings", "", Strings.TuiRecordings, "rec"),
        new("bench", "", Strings.TuiBench, "benchmarks"),
        new("gpu", "", Strings.TuiGpu),
        new("freevram", "", Strings.TuiFreeVram),
        new("eval", "[runs|results|models|catalog]", Strings.TuiEval, "tests"),
        new("server", "[status|start|stop|keep on|off]", Strings.TuiServer),
        new("web", "", Strings.TuiWeb),
        new("remote", "[on|off]", Strings.TuiRemote),
        new("qr", "", Strings.TuiQr),
        new("devices", "[id]", Strings.TuiDevices),
        new("role", "<id> viewer|admin", Strings.TuiRole),
        new("approve", "<code> [viewer]", Strings.TuiApprove),
        new("prompt", "<server> <text> | clear | system <text>", Strings.TuiPrompt, "chat"),
        new("nodes", "[search|pair <address> [code]|remove <id>|run <node> <action>]", Strings.TuiNodes),
        new("bugreport", "<what happened>", Strings.TuiBugReport, "bug"),
        new("about", "", Strings.TuiAbout),
        new("theme", "[name]", "switch colors: " + string.Join(", ", TuiTheme.All.Select(t => t.Name))),
        new("clear", "", Strings.TuiClear),
        new("help", "", Strings.TuiHelp, "?"),
        new("quit", "", Strings.TuiQuit, "exit", "q"),
    };

    private readonly ITerminal _term;
    private readonly Session _s;
    private readonly TuiPrompt _prompt;
    private readonly CliArgs _args;
    private readonly LogPane _log = new();
    private readonly Paint _p;

    private readonly StringBuilder _input = new();
    private int _caret;
    private readonly List<string> _history = new();
    private int _histIdx = -1;
    private int _sel;
    private int _scroll;                     // Zeilen vom Ende der Ausgabe nach oben geblättert
    private Task? _tick, _cmd;
    private DateTime _nextTick = DateTime.MinValue, _ctrlCAt = DateTime.MinValue, _flashUntil = DateTime.MinValue;
    private string _flash = "";
    private bool _quit;
    private int _ticksDone;
    private List<string> _header = new();
    private int _headerWidth = -1;
    private (int Version, int Width, List<string> Lines) _logCache = (-1, -1, new());

    public TuiApp(ITerminal term, Session session, TuiPrompt prompt, CliArgs args)
    {
        _term = term; _s = session; _prompt = prompt; _args = args;
        var theme = TuiTheme.Find(args.Theme) ?? TuiTheme.Find(session.Engine.Settings.Theme) ?? TuiTheme.DeepSea;
        _p = new Paint(theme, Paint.Detect(args.Color));
        Out.SetColor(_p.Mode != ColorMode.None);
        Out.SetUnicode(!args.Ascii);
        _s.Engine.Notice += m => _log.Line(_p.Muted("• " + m));
        _s.Engine.Alarm += m => Flash("! " + m);
        // Der Server ist weg: einmal melden (wie im Fenster), dann misst die TUI wieder selbst
        _s.Remote?.Lost += _ => { _log.Line(_p.Warn(Strings.ServerGone)); Flash(Strings.ServerGone); };
    }

    // ── Ablauf ──

    public async Task RunAsync(CancellationToken ct)
    {
        var oldOut = Console.Out; var oldErr = Console.Error;
        var writer = TextWriter.Synchronized(_log.Writer());
        _term.Enter();
        Console.SetOut(writer); Console.SetError(writer);
        try
        {
            Welcome();
            int lastLog = -1, lastW = 0, lastH = 0;
            bool rendered = false;
            while (!_quit && !ct.IsCancellationRequested)
            {
                bool dirty = false;
                while (!_quit && _term.KeyAvailable) { HandleKey(_term.ReadKey()); dirty = true; }

                var now = DateTime.Now;
                // Messen nur, wenn kein Befehl läuft (Start/Stopp greifen auf dieselbe Engine zu).
                // Läuft der Server, fragt TickAsync ihn – die eigene Engine daneben wäre doppelte Arbeit.
                if (_tick == null && _cmd == null && now >= _nextTick) _tick = _s.UsesServer ? _s.TickAsync(1) : _s.Engine.TickAsync();
                if (_tick is { IsCompleted: true })
                {
                    _tick = null; _ticksDone++;
                    _nextTick = DateTime.Now.AddMilliseconds(_args.IntervalMs);
                    _headerWidth = -1;
                    dirty = true;
                }
                if (_cmd is { IsCompleted: true })
                {
                    if (_cmd.Exception?.GetBaseException() is { } ex) _log.Line(_p.Bad("error: " + ex.Message));
                    _cmd = null; dirty = true;
                }
                if (_log.Version != lastLog) { lastLog = _log.Version; dirty = true; }
                if (_term.Width != lastW || _term.Height != lastH) { lastW = _term.Width; lastH = _term.Height; _headerWidth = -1; dirty = true; }
                if (_flash.Length > 0 && now > _flashUntil) { _flash = ""; dirty = true; }

                if (dirty || !rendered) { Render(); rendered = true; }

                // Bild ohne Terminal (--snapshot): fertig, wenn das Skript durch ist und nichts mehr läuft
                // (eine offene Rückfrage bleibt im Bild stehen und wird danach mit "Nein" beantwortet)
                if (_term is VirtualTerminal vt && vt.ScriptDone && (_cmd == null || _prompt.Pending != null) && _ticksDone >= 3 && _tick == null)
                {
                    Render();
                    _prompt.Answer(null);
                    break;
                }
                try { await Task.Delay(30, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            _prompt.Answer(null);   // ein wartender Befehl (Rückfrage) endet mit "Nein"
            try { _cmd?.Wait(2000); } catch { }   // seine letzten Zeilen noch ins Protokoll, nicht ins Terminal
            Console.SetOut(oldOut); Console.SetError(oldErr);
            _term.Leave();
        }
    }

    public string OutputText => _log.PlainText();

    private void Welcome()
    {
        var ver = typeof(TuiApp).Assembly.GetName().Version?.ToString(3) ?? "";
        _log.Line(_p.Accent("✻ ", true) + _p.Ink("Welcome to ", false) + _p.Accent("stykker", true) + _p.Muted($" {ver} – the terminal side of StykkerLLM"));
        _log.Line(_p.Muted("  Type ") + _p.Ink("/") + _p.Muted(" for commands, ") + _p.Ink("/help") + _p.Muted(" for keys. The status above updates every second."));
        if (_s.Simulated) _log.Line(_p.Second("  Simulation: fake servers, nothing real is started or stopped."));
        else if (_s.UsesServer) _log.Line(_p.Accent("  The Stykker server is the core: it measures, /start /stop go to it.") + _p.Muted(" /server shows it, /server stop ends it."));
        else if (_s.ReadOnly) _log.Line(_p.Warn("  Read-only: the app is open with this data folder. Start/stop there, or close it and restart stykker."));
        if (_s.Limited) _log.Line(_p.Warn("  Limited: no process/GPU access on this platform yet – only servers added by URL are shown."));
        if (!_s.Simulated) _log.Line(_p.Muted("  Data folder: " + _s.Engine.Paths.Root));
    }

    private void Flash(string text) { _flash = text; _flashUntil = DateTime.Now.AddSeconds(5); }

    // ── Tasten ──

    private List<Cmd> Suggestions()
    {
        if (_prompt.Pending != null || _input.Length == 0 || _input[0] != '/') return new();
        var t = _input.ToString(1, _input.Length - 1);
        if (t.Contains(' ')) return new();
        return Cmds.Where(c => c.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase) || c.Aliases.Any(x => x.StartsWith(t, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private void SetInput(string text) { _input.Clear().Append(text); _caret = _input.Length; _sel = 0; }

    private void HandleKey(ConsoleKeyInfo k)
    {
        bool ctrl = (k.Modifiers & ConsoleModifiers.Control) != 0;
        if (ctrl && k.Key == ConsoleKey.C || k.KeyChar == '\u0003')
        {
            if (_prompt.Pending != null) { _prompt.Answer(null); return; }
            if (_input.Length > 0) { SetInput(""); return; }
            if ((DateTime.Now - _ctrlCAt).TotalSeconds < 2) { _quit = true; return; }
            _ctrlCAt = DateTime.Now;
            Flash("Press Ctrl+C again to exit");
            return;
        }
        if (ctrl && k.Key == ConsoleKey.D || k.KeyChar == '\u0004') { if (_input.Length == 0 && _prompt.Pending == null) _quit = true; return; }
        if (ctrl && k.Key == ConsoleKey.U || k.KeyChar == '\u0015') { SetInput(""); return; }
        if (ctrl && k.Key == ConsoleKey.L || k.KeyChar == '\u000c') { _log.Clear(); return; }

        var sugg = Suggestions();
        switch (k.Key)
        {
            case ConsoleKey.Enter:
                if (sugg.Count > 0)
                {
                    var c = sugg[Math.Clamp(_sel, 0, sugg.Count - 1)];
                    var typed = _input.ToString(1, _input.Length - 1);
                    bool exact = c.Name.Equals(typed, StringComparison.OrdinalIgnoreCase) || c.Aliases.Contains(typed, StringComparer.OrdinalIgnoreCase);
                    if (!exact && c.Args.StartsWith('<')) { SetInput("/" + c.Name + " "); return; }   // braucht noch ein Ziel
                    SetInput("/" + c.Name);
                }
                Submit();
                return;
            case ConsoleKey.Tab:
                if (sugg.Count > 0) SetInput("/" + sugg[Math.Clamp(_sel, 0, sugg.Count - 1)].Name + (sugg[Math.Clamp(_sel, 0, sugg.Count - 1)].Args.Length > 0 ? " " : ""));
                return;
            case ConsoleKey.Escape:
                if (_prompt.Pending != null) _prompt.Answer(null); else SetInput("");
                return;
            case ConsoleKey.UpArrow:
                if (sugg.Count > 0) { _sel = (_sel - 1 + sugg.Count) % sugg.Count; return; }
                if (_prompt.Pending == null && _history.Count > 0)
                {
                    _histIdx = _histIdx < 0 ? _history.Count - 1 : Math.Max(0, _histIdx - 1);
                    var keepSel = _sel; SetInput(_history[_histIdx]); _sel = keepSel;
                }
                return;
            case ConsoleKey.DownArrow:
                if (sugg.Count > 0) { _sel = (_sel + 1) % sugg.Count; return; }
                if (_histIdx >= 0)
                {
                    _histIdx++;
                    if (_histIdx >= _history.Count) { _histIdx = -1; SetInput(""); } else SetInput(_history[_histIdx]);
                }
                return;
            case ConsoleKey.PageUp: _scroll += Math.Max(1, LogHeight() - 2); return;
            case ConsoleKey.PageDown: _scroll = Math.Max(0, _scroll - Math.Max(1, LogHeight() - 2)); return;
            case ConsoleKey.LeftArrow: _caret = Math.Max(0, _caret - 1); return;
            case ConsoleKey.RightArrow: _caret = Math.Min(_input.Length, _caret + 1); return;
            case ConsoleKey.Home: _caret = 0; return;
            case ConsoleKey.End: _caret = _input.Length; return;
            case ConsoleKey.Backspace: if (_caret > 0) { _input.Remove(--_caret, 1); _sel = 0; } return;
            case ConsoleKey.Delete: if (_caret < _input.Length) _input.Remove(_caret, 1); return;
        }
        if (!char.IsControl(k.KeyChar)) { _input.Insert(_caret++, k.KeyChar); _sel = 0; _histIdx = -1; }
    }

    // ── Befehle ──

    private void Submit()
    {
        var text = _input.ToString();
        if (_prompt.Pending is { } q)
        {
            _log.Line(_p.Muted("  " + (q.Secret ? new string('•', text.Length) : text)));
            SetInput("");
            _prompt.Answer(text);
            return;
        }
        text = text.Trim();
        SetInput("");
        if (text.Length == 0) return;
        if (_history.Count == 0 || _history[^1] != text) _history.Add(text);
        _histIdx = -1;
        _scroll = 0;
        if (_cmd != null) { Flash("busy – wait for the running command"); return; }
        _log.Line();
        _log.Line(_p.Muted("> ") + _p.Ink(text, true));
        var tokens = Tokenize(text.TrimStart('/'));
        if (tokens.Count == 0) return;
        var name = tokens[0].ToLowerInvariant();
        var cmd = Cmds.FirstOrDefault(c => c.Name == name || c.Aliases.Contains(name)) ?? (name is "list" or "ls" ? new Cmd("list", "", "") : null);
        if (cmd == null) { _log.Line(_p.Bad($"unknown command '{name}'") + _p.Muted(" – type / to see the commands")); return; }

        switch (cmd.Name)
        {
            case "quit": _quit = true; return;
            case "clear": _log.Clear(); return;
            case "help": Help(); return;
            case "theme": Theme(tokens.Skip(1).ToList()); return;
        }
        var rest = tokens.Skip(1).ToList();
        var (a, err) = CliArgs.Parse(new[] { cmd.Name }.Concat(rest).ToList());
        if (err != null) { _log.Line(_p.Bad("error: " + err)); return; }
        // Die Optionen der Sitzung gelten auch für die /-Befehle (Datenordner, Serverport)
        if (a.DataDir == null) a.DataDir = _args.DataDir;
        if (_args.Port > 0) a.Port = _args.Port;
        _cmd = Task.Run(() => RunCommand(cmd.Name, a));
    }

    private async Task RunCommand(string name, CliArgs a)
    {
        int code = name switch
        {
            "status" => Status(a),
            "list" => await Commands.ListCore(a, _s).ConfigureAwait(false),
            "saved" => await TuiCommands.SavedAsync(_s, a, CancellationToken.None).ConfigureAwait(false),
            "history" => await TuiCommands.HistoryAsync(_s, a, CancellationToken.None).ConfigureAwait(false),
            "recordings" => await TuiCommands.RecordingsAsync(_s, a, CancellationToken.None).ConfigureAwait(false),
            "bench" => await BenchAsync(a).ConfigureAwait(false),
            "record" => await TuiCommands.RecordAsync(_s, a, CancellationToken.None).ConfigureAwait(false),
            "add" => await TuiCommands.AddAsync(_s, a, CancellationToken.None).ConfigureAwait(false),
            "remove" => await TuiCommands.RemoveServerAsync(_s, a, CancellationToken.None).ConfigureAwait(false),
            "freevram" => await TuiCommands.FreeVramAsync(_s, CancellationToken.None).ConfigureAwait(false),
            "gpu" => TuiCommands.Gpu(_s),
            "show" => Commands.ShowCore(a, _s),
            "start" => await Commands.StartCore(a, _s, CancellationToken.None).ConfigureAwait(false),
            "stop" => await Commands.StopCore(a, _s).ConfigureAwait(false),
            "unload" => await Commands.UnloadCore(a, _s).ConfigureAwait(false),
            "proxy" => await ProxyCommand.ProxyAsync(_s, a, CancellationToken.None).ConfigureAwait(false),
            "notice" => await ProxyCommand.NoticeAsync(_s, a, CancellationToken.None).ConfigureAwait(false),
            "recent" => await ProxyCommand.RecentAsync(_s, a, CancellationToken.None).ConfigureAwait(false),
            "about" => ProxyCommand.About(_s.Engine.Paths),
            "web" => await ServerCommand.WebAsync(a, CancellationToken.None).ConfigureAwait(false),
            "remote" => await ServerCommand.RemoteAsync(a, CancellationToken.None).ConfigureAwait(false),
            "qr" => await ServerCommand.QrAsync(a, CancellationToken.None).ConfigureAwait(false),
            "devices" => await ServerCommand.DevicesAsync(a, CancellationToken.None).ConfigureAwait(false),
            "role" => await ServerCommand.RoleAsync(a, CancellationToken.None).ConfigureAwait(false),
            "approve" => await ServerCommand.ApproveAsync(a, CancellationToken.None).ConfigureAwait(false),
            "prompt" or "chat" => await PromptCommand.RunAsync(_s, a, CancellationToken.None).ConfigureAwait(false),
            "nodes" => await NodeCommand.RunAsync(a, CancellationToken.None, waitForApproval: false).ConfigureAwait(false),
            "eval" => await EvalCommands.RunAsync(_s, a).ConfigureAwait(false),
            "server" => await ServerCommand.ServerAsync(a, CancellationToken.None, _s.Prompt).ConfigureAwait(false),
            "bugreport" => await BugReportCommand.RunAsync(a, CancellationToken.None).ConfigureAwait(false),
            _ => Commands.Usage,
        };
        if (code == Commands.Cancelled) _log.Line(_p.Muted("cancelled"));
        _headerWidth = -1;
    }

    private int Status(CliArgs a)
    {
        Commands.PrintStatus(_s, a);
        return Commands.Ok;
    }

    // "/bench run <server> …" startet einen Lauf, alles andere zeigt die Ergebnisse
    private Task<int> BenchAsync(CliArgs a)
    {
        var words = a.Words.ToList();
        if (words.Count > 0 && words[0].Equals("run", StringComparison.OrdinalIgnoreCase))
            return TuiCommands.BenchRunAsync(_s, words.Count > 1 ? words[1] : "", words.Skip(2).ToList(), CancellationToken.None);
        return Commands.ListCore(List(a, "bench"), _s);
    }

    // Die Listenbefehle erwarten den Bereichsnamen als erstes Wort ("saved", "history", "recordings", "bench")
    internal static CliArgs List(CliArgs a, string what)
    {
        a.Words.Insert(0, what);
        return a;
    }

    private void Help()
    {
        int w = Cmds.Max(c => c.Name.Length + c.Args.Length) + 3;
        foreach (var c in Cmds)
            _log.Line("  " + _p.Accent("/" + c.Name) + " " + _p.Muted(c.Args) + new string(' ', Math.Max(1, w - c.Name.Length - c.Args.Length - 1)) + _p.Ink(c.Help));
        _log.Line();
        _log.Line(_p.Muted("  Keys: Tab completes · ↑↓ pick a suggestion or recall a command · PgUp/PgDn scroll the output"));
        _log.Line(_p.Muted("        Esc clears the line or answers no · Ctrl+L clears the output · Ctrl+C twice (or Ctrl+D) quits"));
        _log.Line(_p.Muted("  Options work too, e.g. ") + _p.Ink("/history --json") + _p.Muted(" or ") + _p.Ink("/status --json"));
    }

    private void Theme(List<string> args)
    {
        if (args.Count == 0)
        {
            foreach (var t in TuiTheme.All)
                _log.Line((t == _p.Theme ? _p.Accent("  ● ") : "    ") + new Paint(t, _p.Mode).Accent(t.Name, true) + _p.Muted("  /theme " + t.Name.Split(' ')[0].ToLowerInvariant()));
            return;
        }
        var th = TuiTheme.Find(string.Join(' ', args));
        if (th == null) { _log.Line(_p.Bad("no theme like that") + _p.Muted(" – /theme lists them")); return; }
        _p.Theme = th;
        _headerWidth = -1;
        if (!_s.ReadOnly && !_s.Simulated) { _s.Engine.Settings.Theme = th.Name; _s.Engine.Settings.Save(); }   // gleiche Einstellung wie die App
        _log.Line(_p.Muted("theme ") + _p.Accent(th.Name, true));
    }

    internal static List<string> Tokenize(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool quote = false;
        foreach (char c in s)
        {
            if (c == '"') { quote = !quote; continue; }
            if (char.IsWhiteSpace(c) && !quote) { if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); } continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    // ── Zeichnen ──

    private const int BoxHeight = 4;   // Rahmen oben, Eingabe, Rahmen unten, Hinweiszeile

    private int LogHeight()
    {
        int h = _term.Height;
        int sugg = Math.Min(8, Suggestions().Count);
        return Math.Max(1, h - _header.Count - sugg - BoxHeight);
    }

    private void Render()
    {
        int w = _term.Width, h = _term.Height;
        if (_headerWidth != w)
        {
            try { _header = Header(w, Math.Max(4, h / 2)); _headerWidth = w; } catch { /* Engine mitten im Takt: altes Bild behalten */ }
        }
        var sugg = Suggestions();
        if (_sel >= sugg.Count) _sel = 0;
        var suggLines = SuggestionLines(sugg, w);
        int logH = Math.Max(1, h - _header.Count - suggLines.Count - BoxHeight);

        if (_logCache.Version != _log.Version || _logCache.Width != w) _logCache = (_log.Version, w, _log.Render(w - 1));
        var all = _logCache.Lines;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, all.Count - logH));
        int end = all.Count - _scroll, start = Math.Max(0, end - logH);

        var frame = new List<string>(h);
        frame.AddRange(_header.Select(l => Ansi.Fit(l, w)));
        var view = all.GetRange(start, end - start);
        for (int i = view.Count; i < logH; i++) frame.Add("");   // Ausgabe unten an der Eingabe ausrichten
        frame.AddRange(view.Select(l => Ansi.Fit(" " + l, w)));
        frame.AddRange(suggLines);
        var (box, caretCol) = InputBox(w);
        frame.AddRange(box);
        while (frame.Count > h) frame.RemoveAt(_header.Count);
        if (_args.Ascii) for (int i = 0; i < frame.Count; i++) frame[i] = ToAscii(frame[i]);
        _term.Present(frame, (h - BoxHeight + 1, caretCol));
    }

    // --ascii: alte Konsolen/Schriften ohne Rahmen- und Sonderzeichen. Je Zeichen genau ein Ersatzzeichen (Spalten bleiben gleich);
    // Steuerfolgen bestehen nur aus ASCII und bleiben unberührt.
    private const string Fancy = "–◆✻─│╭╮╰╯·…›•→←↑↓●○▕▏█▉▊▋▌▍▎▁▂▃▄▅▆▇°";
    private const string Plain = "-**-|++++.~>*><^v*o[]#######_.:-=+*o";

    internal static string ToAscii(string line)
    {
        var sb = new StringBuilder(line.Length);
        foreach (char c in line)
        {
            int i = Fancy.IndexOf(c);
            sb.Append(i >= 0 ? Plain[i] : c > 126 && !char.IsLetter(c) ? '?' : c);
        }
        return sb.ToString();
    }

    private List<string> SuggestionLines(List<Cmd> sugg, int w)
    {
        var list = new List<string>();
        int nameW = Cmds.Max(c => c.Name.Length + c.Args.Length) + 4;
        for (int i = 0; i < sugg.Count && i < 8; i++)
        {
            var c = sugg[i];
            bool sel = i == _sel;
            var left = "/" + c.Name + (c.Args.Length > 0 ? " " + c.Args : "");
            var text = (sel ? " › " : "   ") + left.PadRight(nameW) + c.Help;
            list.Add(sel ? _p.Accent(Ansi.Fit(text, w - 1), true) : _p.Ink(" " + Ansi.Fit(text[1..], w - 2)).Replace(left, _p.Accent(left) + _p.Fg(_p.Theme.Muted)));
        }
        return list;
    }

    private (List<string> Lines, int CaretCol) InputBox(int w)
    {
        var q = _prompt.Pending;
        Func<string, string> frame = q != null ? _p.Warn : _p.Muted;   // Eingabebox gut sichtbar, bei Rückfrage in Warnfarbe
        string label = q != null ? q.Label + " › " : "> ";
        string shown = q is { Secret: true } ? new string('•', _input.Length) : _input.ToString();
        int inner = w - 4 - label.Length;   // "│ " + label + text + " │"
        int offset = Math.Max(0, _caret - inner + 1);
        var visible = shown.Length > offset ? shown[offset..] : "";
        if (visible.Length > inner) visible = visible[..inner];
        var lines = new List<string>
        {
            frame("╭" + new string('─', w - 2) + "╮"),
            frame("│") + " " + (q != null ? _p.Warn(label) : _p.Accent(label, true)) + _p.Ink(visible) + new string(' ', Math.Max(0, inner - visible.Length)) + " " + frame("│"),
            frame("╰" + new string('─', w - 2) + "╯"),
        };
        string hint = _flash.Length > 0 ? _p.Warn("  " + _flash)
            : _cmd != null && q == null ? _p.Second("  working…")
            : _p.Muted("  / for commands · ↑↓ recall · PgUp/PgDn scroll" + (_scroll > 0 ? $" (+{_scroll})" : "") + " · Ctrl+C twice to quit");
        string right = _p.Muted(_p.Theme.Name + " ");
        int gap = w - Ansi.Width(hint) - Ansi.Width(right);
        lines.Add(gap > 0 ? hint + new string(' ', gap) + right : Ansi.Fit(hint, w));
        return (lines, 2 + label.Length + (_caret - offset));
    }

    // ── Kopf: Titel, GPU, System, laufende Server ──

    private const string Eighths = " ▏▎▍▌▋▊▉█", Spark = "▁▂▃▄▅▆▇█";

    private string Bar(double frac, int width, Rgb color)
    {
        frac = double.IsFinite(frac) ? Math.Clamp(frac, 0, 1) : 0;
        if (_args.Ascii)
        {
            int f = (int)Math.Round(frac * width);
            return _p.Frame("[") + _p.C(color, new string('#', f)) + _p.Frame(new string('.', width - f) + "]");
        }
        double cells = frac * width;
        int full = (int)cells, part = (int)Math.Round((cells - full) * 8);
        if (part == 8) { full++; part = 0; }
        var fill = new string('█', full) + (full < width && part > 0 ? Eighths[part].ToString() : "");
        return _p.Frame("▕") + _p.C(color, fill) + _p.Frame(new string('·', Math.Max(0, width - fill.Length))) + _p.Frame("▏");
    }

    private Rgb Level(double frac) => frac > 0.92 ? _p.Theme.Bad : frac > 0.80 ? _p.Theme.Warn : _p.Theme.Accent;

    private string Sparkline(ServerWatcher s, int width)
    {
        int n = Math.Min(width, s.HistoryCount);
        if (n == 0) return new string(' ', width);
        double max = Math.Max(1, Enumerable.Range(s.HistoryCount - n, n).Max(i => s.HistoryAt(i)));
        var sb = new StringBuilder(new string(' ', width - n));
        for (int i = s.HistoryCount - n; i < s.HistoryCount; i++)
        {
            double v = s.HistoryAt(i);
            sb.Append(v <= 0.01 ? (_args.Ascii ? '_' : '▁') : _args.Ascii ? ".:-=+*#%@"[(int)Math.Min(8, v / max * 8.99)] : Spark[(int)Math.Min(7, v / max * 7.99)]);
        }
        return _p.Second(sb.ToString());
    }

    // Statuszeile des Stykker-Proxys (Bereich 2): an/aus, Port, Ziel und die LAN-Kennzeichnung
    private void ProxyLine(List<string> lines, bool running, int port, string target, bool lan, int w)
    {
        var text = Strings.ProxyBar(running, port, target.Length > 0 ? target : Strings.ProxyTargetNone, lan);
        lines.Add(running ? " " + _p.Good(text) : " " + _p.Muted(text) + _p.Muted("   /proxy on | off | lan | serve <server|provider/model> | providers"));
    }

    // Anhaltender Hinweis (zweite Zeile darunter): Text und Logdatei, bis ihn /notice dismiss wegnimmt
    private void NoticeLine(List<string> lines, NoticeState? n, int w)
    {
        if (n == null) return;
        string tag = n.Alarm ? Strings.NoticeAlarm : Strings.NoticeHint;
        string text = $"{tag}: {n.Text}" + (n.LogFile.Length > 0 ? $"   [{Strings.ColLogFile}: {n.LogFile}]" : "");
        lines.Add(" " + (n.Alarm ? _p.Warn(Ansi.Fit(text, w - 2)) : _p.Second(Ansi.Fit(text, w - 2))) + _p.Muted("   /notice dismiss"));
    }

    // Serviert der Proxy gerade dieses Modell? (Chip wie auf der Karte im Fenster)
    private bool IsServed(string key) => _s.UsesServer
        ? _s.Server!.Proxy.Running && _s.Server!.Proxy.ServedKey == key
        : _s.Engine.Proxies.IsServed(key);

    private List<string> Header(int w, int maxLines)
    {
        // Läuft der Server für diesen Datenordner, liefert er Kopf und Zeilen – die TUI misst dann nicht selbst
        if (_s.UsesServer && _s.Server is { } state) return ServerHeader(state, w, maxLines);
        var e = _s.Engine;
        var lines = new List<string>();
        var tags = new List<string>();
        if (_s.Simulated) tags.Add(_p.Second("SIMULATION"));
        if (_s.ReadOnly && !_s.Simulated) tags.Add(_p.Warn("read-only"));
        if (_s.Limited) tags.Add(_p.Warn("limited"));
        tags.Add(_p.Muted(DateTime.Now.ToString("HH:mm:ss")));
        string left = " " + _p.Accent("◆ ", true) + _p.Ink("StykkerLLM", true);
        string right = string.Join(_p.Muted("  ·  "), tags) + " ";
        lines.Add(left + new string(' ', Math.Max(1, w - Ansi.Width(left) - Ansi.Width(right))) + right);

        int bw = w >= 110 ? 16 : w >= 80 ? 10 : 6;
        if (e.Gpu is { } g)
        {
            double vf = g.MemTotalGb > 0 ? g.MemUsedGb / g.MemTotalGb : 0;
            lines.Add(" " + _p.Muted("GPU ") + _p.Ink(Ansi.Fit(g.Name, w >= 110 ? 26 : 16, pad: true)) + " " + Bar(g.Util / 100, bw / 2 + 2, _p.Theme.Accent) + _p.Ink($" {g.Util,3:0}%") +
                _p.Muted("   VRAM ") + Bar(vf, bw, Level(vf)) + _p.Ink($" {g.MemUsedGb:0.0}/{g.MemTotalGb:0.0} GB") +
                _p.Muted($"   {g.TempC:0}°C  {g.PowerW:0} W") + (g.Throttled ? "  " + _p.Warn("throttled: " + g.ThrottleText()) : ""));
        }
        else lines.Add(" " + _p.Muted("GPU  not available"));
        if (e.Sys is { } sy)
        {
            double rf = sy.RamTotalGb > 0 ? sy.RamUsedGb / sy.RamTotalGb : 0;
            lines.Add(" " + _p.Muted("SYS ") + _p.Ink(Ansi.Fit($"{sy.Cores} threads", w >= 110 ? 26 : 16, pad: true)) + " " + Bar(sy.CpuPercent / 100, bw / 2 + 2, _p.Theme.Accent) + _p.Ink($" {sy.CpuPercent,3:0}%") +
                _p.Muted("   RAM  ") + Bar(rf, bw, Level(rf)) + _p.Ink($" {sy.RamUsedGb:0.0}/{sy.RamTotalGb:0.0} GB"));
        }

        var servers = e.Servers;
        ProxyLine(lines, e.Proxies.Running, e.Proxies.Port, e.Proxies.TargetName(), e.Proxies.BindLan, w);
        NoticeLine(lines, e.CurrentNotice is { } ln ? new NoticeState(ln.Text, ln.Alarm, ln.LogFile ?? "") : null, w);
        string title = _ticksDone == 0 ? " scanning… " : $" running · {servers.Count} ";
        lines.Add(_p.Frame("─") + _p.Muted(title) + _p.Frame(new string('─', Math.Max(0, w - title.Length - 1))));
        if (_ticksDone > 0 && servers.Count == 0) lines.Add("  " + _p.Muted(Strings.RunningEmpty + " – /history shows what ran before, /start starts it again"));
        int sparkW = w >= 120 ? 24 : w >= 96 ? 14 : 0;
        int room = Math.Max(1, maxLines - lines.Count - 1);
        foreach (var sv in servers.Take(room))
            lines.Add(ServerLine(sv, w, sparkW));
        if (servers.Count > room) lines.Add("  " + _p.Muted($"+{servers.Count - room} more – /status lists all"));
        lines.Add(_p.Frame(new string('─', w)));
        return lines;
    }

    private string ServerLine(ServerWatcher s, int w, int sparkW)
    {
        var state = Commands.State(s);
        string dot = state switch
        {
            "busy" => _p.Good(Out.Dot(true)), "idle" => _p.Accent(Out.Dot(true)), "loading" or "sleeping" => _p.Warn(Out.Dot(true)), _ => _p.Bad(Out.Dot(false)),
        };
        string stateText = state switch { "busy" => _p.Good("busy    "), "loading" => _p.Warn("loading "), "offline" => _p.Bad("offline "), _ => _p.Muted(state.PadRight(8)) };
        var sb = new StringBuilder();
        sb.Append("  ").Append(dot).Append(' ').Append(_p.Ink(Ansi.Fit(s.Name, 20, pad: true), true));
        sb.Append(_p.Muted($" :{s.Info.Port,-5} ")).Append(_p.Muted(Commands.BackendName(s.Kind).PadRight(9))).Append(' ').Append(stateText);
        if (sparkW > 0) sb.Append(' ').Append(Sparkline(s, sparkW));
        sb.Append(s.Online ? _p.Ink($" {s.Current,6:0.0}") + _p.Muted(" t/s") : _p.Muted("      – t/s"));
        if (w >= 96) sb.Append(_p.Muted("  VRAM ") + _p.Ink(s.VramGb is double v ? $"{v,5:0.0} GB" : "    – "));
        if (w >= 110 && s.Slots.Count > 0) sb.Append(_p.Muted("  slots ") + _p.Ink($"{s.Slots.Count(x => x.Busy)}/{s.Slots.Count}"));
        if (w >= 110 && s.QueueCount is int q && q > 0) sb.Append(_p.Warn("  +" + q + " " + Strings.ChipQueue));
        if (w >= 130 && CtxPressure.Worst(s.Slots) is { } full)
            sb.Append(_p.Warn("  " + Strings.CtxAlmostFull + " " + Strings.Pct100(CtxPressure.Fraction(full))));
        if (w >= 130 && s.SpecActive)
            sb.Append(_p.Good("  " + Strings.ChipDraft + " " + Strings.Pct100(s.SpecRate)));
        if (w >= 130 && ServerWatcher.SpillGb(s.Models, s.SharedGb) is double spill) sb.Append(_p.Warn("  " + Strings.ChipSharedRam + " " + Out.Gb(spill)));
        if (w >= 130) { string un = UnloadsText(s.Models); if (un.Length > 0) sb.Append(_p.Muted("  " + un)); }
        if (w >= 130 && s.Clients.Length > 0) sb.Append(_p.Muted("  ← " + string.Join(", ", s.Clients)));
        sb.Append(IsServed(s.Key) ? _p.Good("  " + Strings.BtnProxyServed) : _p.Muted("  " + Strings.BtnProxyCard));
        return sb.ToString();
    }

    // Wann das nächste Modell von selbst entladen wird (Ollama, LM Studio): derselbe Text wie im Fenster und im Web
    private static string UnloadsText(IEnumerable<LoadedModel> models)
    {
        DateTime? next = null;
        foreach (var m in models) if (m.ExpiresAt is DateTime e && (next == null || e < next)) next = e;
        return Strings.Until(next);
    }

    // ── Kopf und Zeilen aus dem Serverzustand (S5: der Server misst, die TUI zeigt) ──

    private List<string> ServerHeader(StateSnapshot st, int w, int maxLines)
    {
        var lines = new List<string>();
        var tags = new List<string>();
        if (st.Simulated) tags.Add(_p.Second("SIMULATION"));
        tags.Add(_p.Accent("server", true));
        if (st.ReadOnly) tags.Add(_p.Warn("read-only"));
        tags.Add(st.Access.Remote && st.Access.LanAddress.Length > 0 ? _p.Muted(st.Access.LanAddress) : _p.Muted("local only"));
        tags.Add(_p.Muted(DateTime.Now.ToString("HH:mm:ss")));
        string left = " " + _p.Accent("◆ ", true) + _p.Ink("StykkerLLM", true);
        string right = string.Join(_p.Muted("  ·  "), tags) + " ";
        lines.Add(left + new string(' ', Math.Max(1, w - Ansi.Width(left) - Ansi.Width(right))) + right);

        int bw = w >= 110 ? 16 : w >= 80 ? 10 : 6;
        if (st.Gpu is { } g)
        {
            double vf = g.MemTotalGb > 0 ? g.MemUsedGb / g.MemTotalGb : 0;
            lines.Add(" " + _p.Muted("GPU ") + _p.Ink(Ansi.Fit(g.Name, w >= 110 ? 26 : 16, pad: true)) + " " + Bar(g.Util / 100, bw / 2 + 2, _p.Theme.Accent) + _p.Ink($" {g.Util,3:0}%") +
                _p.Muted("   VRAM ") + Bar(vf, bw, Level(vf)) + _p.Ink($" {g.MemUsedGb:0.0}/{g.MemTotalGb:0.0} GB") +
                _p.Muted($"   {g.TempC:0}°C  {g.PowerW:0} W") + (g.Throttled ? "  " + _p.Warn("throttled: " + g.ThrottleText()) : ""));
        }
        else lines.Add(" " + _p.Muted("GPU  not available"));
        if (st.Sys is { } sy)
        {
            double rf = sy.RamTotalGb > 0 ? sy.RamUsedGb / sy.RamTotalGb : 0;
            lines.Add(" " + _p.Muted("SYS ") + _p.Ink(Ansi.Fit($"{sy.Cores} threads", w >= 110 ? 26 : 16, pad: true)) + " " + Bar(sy.CpuPercent / 100, bw / 2 + 2, _p.Theme.Accent) + _p.Ink($" {sy.CpuPercent,3:0}%") +
                _p.Muted("   RAM  ") + Bar(rf, bw, Level(rf)) + _p.Ink($" {sy.RamUsedGb:0.0}/{sy.RamTotalGb:0.0} GB"));
        }

        var servers = st.Servers;
        ProxyLine(lines, st.Proxy.Running, st.Proxy.Port, st.Proxy.TargetName, st.Proxy.BindLan, w);
        NoticeLine(lines, st.Notice, w);
        string title = st.Ticks == 0 ? " scanning… " : $" running · {servers.Count} ";
        lines.Add(_p.Frame("─") + _p.Muted(title) + _p.Frame(new string('─', Math.Max(0, w - title.Length - 1))));
        if (st.Ticks > 0 && servers.Count == 0) lines.Add("  " + _p.Muted(Strings.RunningEmpty + " – /history shows what ran before, /start starts it again"));
        int sparkW = w >= 120 ? 24 : w >= 96 ? 14 : 0;
        int room = Math.Max(1, maxLines - lines.Count - 1);
        foreach (var sv in servers.Take(room)) lines.Add(RemoteLine(sv, w, sparkW));
        if (servers.Count > room) lines.Add("  " + _p.Muted($"+{servers.Count - room} more – /status lists all"));
        lines.Add(_p.Frame(new string('─', w)));
        return lines;
    }

    private string RemoteLine(RemoteServer s, int w, int sparkW)
    {
        string dot = s.State switch
        {
            "busy" => _p.Good(Out.Dot(true)), "idle" => _p.Accent(Out.Dot(true)), "loading" or "sleeping" => _p.Warn(Out.Dot(true)), _ => _p.Bad(Out.Dot(false)),
        };
        string stateText = s.State switch { "busy" => _p.Good("busy    "), "loading" => _p.Warn("loading "), "offline" => _p.Bad("offline "), _ => _p.Muted(s.State.PadRight(8)) };
        var sb = new StringBuilder();
        sb.Append("  ").Append(dot).Append(' ').Append(_p.Ink(Ansi.Fit(s.Name, 20, pad: true), true));
        sb.Append(_p.Muted($" :{s.Port,-5} ")).Append(_p.Muted(s.Backend.PadRight(9))).Append(' ').Append(stateText);
        if (sparkW > 0) sb.Append(' ').Append(RemoteSpark(s, sparkW));
        sb.Append(s.Online ? _p.Ink($" {s.Current,6:0.0}") + _p.Muted(" t/s") : _p.Muted("      – t/s"));
        if (w >= 96) sb.Append(_p.Muted("  VRAM ") + _p.Ink(s.VramGb is double v ? $"{v,5:0.0} GB" : "    – "));
        if (w >= 110 && s.Slots.Count > 0) sb.Append(_p.Muted("  slots ") + _p.Ink($"{s.Slots.Count(x => x.Busy)}/{s.Slots.Count}"));
        if (w >= 110 && s.QueueCount > 0) sb.Append(_p.Warn("  +" + s.QueueCount + " " + Strings.ChipQueue));
        if (w >= 130 && s.SpillGb is double spill) sb.Append(_p.Warn("  " + Strings.ChipSharedRam + " " + Out.Gb(spill)));
        if (w >= 130) { string un = UnloadsText(s.Models); if (un.Length > 0) sb.Append(_p.Muted("  " + un)); }
        if (w >= 130 && s.Clients.Length > 0) sb.Append(_p.Muted("  ← " + string.Join(", ", s.Clients)));
        sb.Append(IsServed(s.Key) ? _p.Good("  " + Strings.BtnProxyServed) : _p.Muted("  " + Strings.BtnProxyCard));
        return sb.ToString();
    }

    private string RemoteSpark(RemoteServer s, int n)
    {
        var values = new List<double>();
        for (int i = Math.Max(0, s.HistoryCount - n); i < s.HistoryCount; i++) values.Add(s.HistoryAt(i));
        if (values.Count == 0) return _p.Second(new string(' ', n));
        double max = Math.Max(10, values.Max());
        var sb = new StringBuilder();
        foreach (double v in values)
            sb.Append(v <= 0.01 ? (_args.Ascii ? '_' : '▁') : _args.Ascii ? ".:-=+*#%@"[(int)Math.Min(8, v / max * 8.99)] : Spark[(int)Math.Min(7, v / max * 7.99)]);
        return _p.Second(sb.ToString());
    }
}
