using System.Text;
using StykkerLlm.Core;

namespace StykkerLlm.Cli.Tui;

// Die Anzeige im Terminal: oben GPU, System und die laufenden Server, darunter die Adresse der Weboberfläche und auf
// Tastendruck eine Tafel (Code + QR, letzte Anfragen, Speicher je Programm, Details eines Servers, Hilfe).
// Kein Eingabefeld, keine Befehle, keine Parameter: gesteuert wird im Web (w öffnet es, schon angemeldet).
public sealed class TuiApp
{
    public enum Panel { None, Help, Code, Recent, Memory, Details }

    private readonly ITerminal _term;
    private readonly IStateSource _source;
    private readonly Action<string> _openUrl;
    private readonly int _intervalMs;
    private Paint _p;
    private StateSnapshot? _state;
    private Task<StateSnapshot?>? _poll;
    private DateTime _nextPoll = DateTime.MinValue, _flashUntil = DateTime.MinValue, _ctrlCAt = DateTime.MinValue;
    private string _flash = "";
    private int _sel, _polls;
    private bool _quit;

    public Panel Current { get; private set; }

    public TuiApp(ITerminal term, IStateSource source, Action<string>? openUrl = null, int intervalMs = 1000)
    {
        _term = term;
        _source = source;
        _openUrl = openUrl ?? ServerLink.OpenBrowser;
        _intervalMs = intervalMs;
        _p = new Paint(TuiTheme.DeepSea, Paint.Detect(ColorMode.Auto));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _term.Enter();
        try
        {
            int lastW = 0, lastH = 0;
            bool dirty = true;
            while (!_quit && !ct.IsCancellationRequested)
            {
                while (!_quit && _term.KeyAvailable) { HandleKey(_term.ReadKey()); dirty = true; }
                var now = DateTime.Now;
                if (_poll == null && now >= _nextPoll) _poll = _source.PollAsync(ct);
                if (_poll is { IsCompleted: true })
                {
                    if (_poll.IsCompletedSuccessfully && _poll.Result is { } st)
                    {
                        _state = st;
                        _polls++;
                        if (TuiTheme.Find(st.Settings.Theme) is { } th && th != _p.Theme) _p = new Paint(th, _p.Mode);   // Thema aus dem Web
                    }
                    _poll = null;
                    _nextPoll = DateTime.Now.AddMilliseconds(_state == null ? 500 : _intervalMs);
                    dirty = true;
                }
                if (_term.Width != lastW || _term.Height != lastH) { lastW = _term.Width; lastH = _term.Height; dirty = true; }
                if (_flash.Length > 0 && now > _flashUntil) { _flash = ""; dirty = true; }
                if (dirty) { _term.Present(Frame(_term.Width, _term.Height), null); dirty = false; }

                // Bild ohne Terminal (Debug --snapshot, Tests): fertig, wenn das Tastenskript durch ist und ein paar Takte da sind
                if (_term is VirtualTerminal vt && vt.ScriptDone && _polls >= 3 && _poll == null)
                {
                    _term.Present(Frame(_term.Width, _term.Height), null);
                    break;
                }
                try { await Task.Delay(30, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
        finally { _term.Leave(); }
    }

    private void Flash(string text) { _flash = text; _flashUntil = DateTime.Now.AddSeconds(4); }

    // ── Tasten ──

    private void HandleKey(ConsoleKeyInfo k)
    {
        bool ctrl = (k.Modifiers & ConsoleModifiers.Control) != 0;
        if (ctrl && k.Key == ConsoleKey.C || k.KeyChar == '\u0003')
        {
            if ((DateTime.Now - _ctrlCAt).TotalSeconds < 2) { _quit = true; return; }
            _ctrlCAt = DateTime.Now;
            Flash(Strings.TuiCtrlCAgain);
            return;
        }
        int count = _state?.Servers.Count ?? 0;
        switch (k.Key)
        {
            case ConsoleKey.UpArrow: if (count > 0) _sel = (_sel - 1 + count) % count; return;
            case ConsoleKey.DownArrow: if (count > 0) _sel = (_sel + 1) % count; return;
            case ConsoleKey.Enter: Toggle(Panel.Details); return;
            case ConsoleKey.Escape: if (Current == Panel.None) _quit = true; else Current = Panel.None; return;
        }
        switch (char.ToLowerInvariant(k.KeyChar))
        {
            case 'q': _quit = true; break;
            case 'w': OpenWeb(); break;
            case 'c': Toggle(Panel.Code); break;
            case 'r': Toggle(Panel.Recent); break;
            case 'm': Toggle(Panel.Memory); break;
            case 'd': Toggle(Panel.Details); break;
            case '?' or 'h': Toggle(Panel.Help); break;
        }
    }

    private void Toggle(Panel p) => Current = Current == p ? Panel.None : p;

    private void OpenWeb()
    {
        var code = _state?.Access.Code ?? "";
        var url = ServerLink.PairUrl(_source.Port, code);
        _openUrl(url);
        Flash(Strings.TuiWebOpened);
    }

    // ── Bild ──

    public string[] Frame(int w, int h)
    {
        var top = new List<string>();
        Header(top, w);
        var bottom = new List<string> { HintLine(w) };
        var panel = new List<string>();
        int room = Math.Max(0, h - top.Count - bottom.Count);
        if (room > 0) PanelLines(panel, w, room);
        var frame = new List<string>(h);
        frame.AddRange(top);
        frame.AddRange(panel.Take(room));
        while (frame.Count < h - bottom.Count) frame.Add("");
        frame.AddRange(bottom);
        return frame.Take(h).Select(l => Ansi.Fit(l, w)).ToArray();
    }

    // Für "stykker status": dasselbe Bild einmal, ohne Tafel und ohne Tastenzeile
    public static IEnumerable<string> StatusLines(StateSnapshot state, int port, int w, bool color)
    {
        var app = new TuiApp(new VirtualTerminal(w, 10), new FixedSource(state, port))
        {
            _state = state,
            _p = new Paint(TuiTheme.Find(state.Settings.Theme) ?? TuiTheme.DeepSea, color ? Paint.Detect(ColorMode.Auto) : ColorMode.None),
        };
        var lines = new List<string>();
        app.Header(lines, w);
        return lines;
    }

    private void Header(List<string> lines, int w)
    {
        var st = _state;
        var tags = new List<string>();
        if (st?.Simulated == true) tags.Add(_p.Second("SIMULATION"));
        tags.Add(_p.Muted(DateTime.Now.ToString("HH:mm:ss")));
        string left = " " + _p.Accent("◆ ", true) + _p.Ink("STYKKER ", true) + _p.Accent("LLM", true);
        string right = string.Join(_p.Muted("  ·  "), tags) + " ";
        lines.Add(left + new string(' ', Math.Max(1, w - Ansi.Width(left) - Ansi.Width(right))) + right);

        if (st == null)
        {
            lines.Add("");
            lines.Add("  " + (_source.Status.Length > 0 && _source.Status != Strings.ShellConnecting && _source.Status != Strings.ShellStarting
                ? _p.Warn(_source.Status) : _p.Second(_source.Status.Length > 0 ? _source.Status : Strings.ShellConnecting)));
            lines.Add("");
            return;
        }

        int bw = w >= 110 ? 16 : w >= 80 ? 10 : 6;
        if (st.Gpu is { } g)
        {
            double vf = g.MemTotalGb > 0 ? g.MemUsedGb / g.MemTotalGb : 0;
            lines.Add(" " + _p.Muted("GPU ") + _p.Ink(Ansi.Fit(g.Name, w >= 110 ? 24 : 14, pad: true)) + " " + Bar(g.Util / 100, bw / 2 + 2, _p.Theme.Accent) + _p.Ink($" {g.Util,3:0}%") +
                _p.Muted("   VRAM ") + Bar(vf, bw, Level(vf)) + _p.Ink($" {g.MemUsedGb:0.0}/{g.MemTotalGb:0.0} GB") +
                _p.Muted($"   {g.TempC:0}°C  {g.PowerW:0} W") + (g.Throttled ? "  " + _p.Warn(g.ThrottleText()) : ""));
        }
        else lines.Add(" " + _p.Muted("GPU  " + Strings.TuiNoGpu));
        if (st.Sys is { } sy)
        {
            double rf = sy.RamTotalGb > 0 ? sy.RamUsedGb / sy.RamTotalGb : 0;
            lines.Add(" " + _p.Muted("SYS ") + _p.Ink(Ansi.Fit($"{sy.Cores} threads", w >= 110 ? 24 : 14, pad: true)) + " " + Bar(sy.CpuPercent / 100, bw / 2 + 2, _p.Theme.Accent) + _p.Ink($" {sy.CpuPercent,3:0}%") +
                _p.Muted("   RAM  ") + Bar(rf, bw, Level(rf)) + _p.Ink($" {sy.RamUsedGb:0.0}/{sy.RamTotalGb:0.0} GB"));
        }

        var servers = st.Servers;
        string title = st.Ticks == 0 ? " " + Strings.TuiScanning + " " : $" {Strings.SectionRunning.ToLowerInvariant()} · {servers.Count} ";
        lines.Add(_p.Frame("─") + _p.Muted(title) + _p.Frame(new string('─', Math.Max(0, w - title.Length - 1))));
        if (st.Ticks > 0 && servers.Count == 0) lines.Add("  " + _p.Muted(Strings.RunningEmpty));
        if (_sel >= servers.Count) _sel = Math.Max(0, servers.Count - 1);
        int sparkW = w >= 120 ? 20 : w >= 96 ? 12 : 0;
        for (int i = 0; i < servers.Count; i++) lines.Add(ServerLine(servers[i], w, sparkW, i == _sel && Current == Panel.Details));
        lines.Add(_p.Frame(new string('─', w)));

        // Die Weboberfläche: dort wird gesteuert
        string local = ServerClient.DefaultUrl(_source.Port);
        string web = " " + _p.Muted(Strings.TuiWebLabel + "  ") + _p.Accent(local, true);
        if (st.Access.Remote && Addresses(st.Access).Count > 0)
            web += _p.Muted("   " + Strings.TuiNetwork + "  ") + string.Join(_p.Muted("  ·  "),
                Addresses(st.Access).Select(ip => _p.Ink(NetAddr.Url(ip, _source.Port)) + (IsTailscale(ip) ? _p.Muted(" (Tailscale)") : "")));
        else if (!st.Simulated) web += _p.Muted("   " + Strings.RemoteThisPcOnly);
        lines.Add(web);
        if (st.Notice is { } n) lines.Add(" " + (n.Alarm ? _p.Warn(n.Text) : _p.Second(n.Text)));
    }

    // Alle Adressen im Netz (LAN, Tailscale …), die erste ist die bevorzugte
    private static List<string> Addresses(AccessStateView a)
    {
        var list = new List<string>();
        if (a.LanAddress.Length > 0) list.Add(a.LanAddress);
        foreach (var ip in a.AllAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!list.Contains(ip)) list.Add(ip);
        return list;
    }

    // Tailscale vergibt Adressen aus 100.64.0.0/10 (CGNAT)
    internal static bool IsTailscale(string ip)
    {
        var parts = ip.Split('.');
        return parts.Length == 4 && parts[0] == "100" && int.TryParse(parts[1], out var b) && b is >= 64 and <= 127;
    }

    private string ServerLine(RemoteServer s, int w, int sparkW, bool selected)
    {
        string dot = s.State switch
        {
            "busy" => _p.Good(Out.Dot(true)), "idle" => _p.Accent(Out.Dot(true)), "loading" or "sleeping" => _p.Warn(Out.Dot(true)), _ => _p.Bad(Out.Dot(false)),
        };
        var sb = new StringBuilder();
        sb.Append(selected ? _p.Accent(" ›", true) : "  ").Append(dot).Append(' ').Append(_p.Ink(Ansi.Fit(s.Name, 20, pad: true), true));
        sb.Append(_p.Muted($" :{s.Port,-5} "));
        if (sparkW > 0) sb.Append(' ').Append(Spark(s, sparkW));
        sb.Append(s.Online ? _p.Ink($" {s.Current,6:0.0}") + _p.Muted(" t/s") : _p.Muted("      – t/s"));
        if (w >= 80) sb.Append(_p.Muted("  ") + _p.Ink(s.VramGb is double v ? $"{v,5:0.0} GB" : "     – "));
        if (w >= 96 && s.Slots.Count > 0) sb.Append(_p.Muted("  slots ") + _p.Ink($"{s.Slots.Count(x => x.Busy)}/{s.Slots.Count}"));
        if (w >= 96 && s.QueueCount > 0) sb.Append(_p.Warn($"  +{s.QueueCount} {Strings.ChipQueue}"));
        return sb.ToString();
    }

    // ── Tafeln ──

    private void PanelLines(List<string> o, int w, int room)
    {
        var st = _state;
        if (st == null) return;
        switch (Current)
        {
            case Panel.Help:
                Title(o, w, Strings.TuiKeysTitle);
                foreach (var (key, text) in new[] { ("w", Strings.TuiKeyWeb), ("c", Strings.TuiKeyCode), ("r", Strings.TuiKeyRecent), ("m", Strings.TuiKeyMemory),
                             ("↑↓ Enter", Strings.TuiKeyDetails), ("Esc", Strings.TuiKeyClose), ("q", Strings.TuiKeyQuit) })
                    o.Add("  " + _p.Accent(key.PadRight(9), true) + _p.Ink(text));
                o.Add("");
                o.Add("  " + _p.Muted(Strings.TuiWebOnly));
                break;
            case Panel.Code:
                CodePanel(o, w, room, st);
                break;
            case Panel.Recent:
                Title(o, w, Strings.RecentTitle);
                var fin = st.Servers.SelectMany(s => s.Finished).OrderByDescending(f => f.Seen ?? DateTime.MinValue).Take(Math.Max(1, room - 2)).ToList();
                if (fin.Count == 0) { o.Add("  " + _p.Muted(Strings.NoneYet)); break; }
                o.Add("  " + _p.Muted($"{Strings.ColServer,-18} {Strings.ColTime,-8} {Strings.ColReqPrompt,10} {Strings.ColReqGeneration,12} {Strings.ColReqTokens,7} {Strings.ColReqTime,6}"));
                foreach (var f in fin)
                    o.Add("  " + _p.Ink(Ansi.Fit(f.Server, 18, pad: true)) + " " + _p.Muted(f.Seen?.ToString("HH:mm:ss") ?? "") +
                          _p.Ink($" {f.PromptTps,6:0} t/s {f.GenTps,8:0.0} t/s {f.GenTokens,7} {(f.Seconds > 0 ? $"{f.Seconds:0} s" : "–"),6}"));
                break;
            case Panel.Memory:
                Title(o, w, Strings.TuiMemoryTitle);
                Top(o, "VRAM", st.VramTop);
                Top(o, "RAM ", st.RamTop);
                break;
            case Panel.Details:
                if (st.Servers.Count == 0) { Title(o, w, Strings.TuiDetailsTitle); o.Add("  " + _p.Muted(Strings.RunningEmpty)); break; }
                Details(o, w, st.Servers[Math.Clamp(_sel, 0, st.Servers.Count - 1)]);
                break;
        }
    }

    private void Title(List<string> o, int w, string title)
    {
        o.Add("");
        o.Add(" " + _p.Accent(title, true));
    }

    private void Top(List<string> o, string label, List<(string Name, double Gb)> top)
    {
        if (top.Count == 0) { o.Add("  " + _p.Muted(label + "  –")); return; }
        o.Add("  " + _p.Muted(label) + "  " + string.Join(_p.Muted("  ·  "), top.Take(8).Select(t => _p.Ink(t.Name) + " " + _p.Muted($"{t.Gb:0.0} GB"))));
    }

    private void CodePanel(List<string> o, int w, int room, StateSnapshot st)
    {
        Title(o, w, Strings.RemoteTitle);
        var a = st.Access;
        if (a.Code.Length == 0) { o.Add("  " + _p.Muted(Strings.TuiNoCode)); return; }
        string url = a.Remote && a.LanAddress.Length > 0 ? NetInfo.PairUrl(_source.Port, a.Code, a.LanAddress) : ServerLink.PairUrl(_source.Port, a.Code);
        var qr = QrCode.Encode(url, QrCode.Ecc.M);
        int qrLines = (qr.Size + 3) / 2;      // zwei Modulzeilen je Textzeile (Halbblöcke)
        var info = new List<string>
        {
            _p.Muted(Strings.TuiCodeLabel) + "  " + _p.Accent(AccessControl.Pretty(a.Code), true),
            _p.Muted(a.CodeExpires is { } exp ? Strings.CodeValidFor(exp - DateTime.Now) : ""),
            "",
            _p.Muted("URL   ") + _p.Ink(url),
            "",
            a.Remote ? _p.Good(Strings.RemoteOn) : _p.Warn(Strings.RemoteOff),
            _p.Muted(a.Remote ? Strings.TuiCodePhone : Strings.TuiRemoteInWeb),
        };
        if (qrLines + 2 > room || qr.Size * 1 + 6 > w / 2)
        {
            foreach (var l in info) o.Add("  " + l);
            return;
        }
        for (int row = 0; row < qrLines; row++)
        {
            var sb = new StringBuilder("  ");
            for (int x = -1; x <= qr.Size; x++)
            {
                bool top = Dark(qr, x, row * 2 - 1), bottom = Dark(qr, x, row * 2);
                // helle Module als Blöcke (weiß auf dunklem Grund): so lesen Handykameras den Code auch im dunklen Terminal
                sb.Append(!top && !bottom ? '█' : !top ? '▀' : !bottom ? '▄' : ' ');
            }
            string text = row - 1 >= 0 && row - 1 < info.Count ? "   " + info[row - 1] : "";
            o.Add(_p.C(new Rgb(240, 244, 250), sb.ToString()) + text);
        }
    }

    private static bool Dark(QrCode qr, int x, int y) => x >= 0 && y >= 0 && x < qr.Size && y < qr.Size && qr[x, y];

    private void Details(List<string> o, int w, RemoteServer s)
    {
        Title(o, w, s.Name);
        void Row(string k, string v) { if (v.Length > 0) o.Add("  " + _p.Muted(k.PadRight(12)) + _p.Ink(v)); }
        Row(Strings.TuiModel, s.Model);
        Row("URL", s.Url);
        Row(Strings.TuiBackend, (s.Backend + " " + s.Version).Trim());
        Row(Strings.ColStatus, s.State);
        Row("t/s", $"{s.Current:0.0} · peak {s.Peak:0.0} · avg {s.AverageActive:0.0} · {s.GeneratedTotal:N0} tokens");
        Row(Strings.TuiMemory, $"VRAM {Out.Gb(s.VramGb)} · RAM {Out.Gb(s.RamGb)}" + (s.CpuPercent is double c ? $" · CPU {c:0} %" : ""));
        foreach (var sl in s.Slots)
            Row($"slot {sl.Id}", (sl.Busy ? (sl.ReadingPrompt ? Strings.SlotReading : Strings.SlotWriting) : Strings.SlotIdle) +
                (sl.CtxMax > 0 ? $" · ctx {sl.CtxUsed:N0}/{sl.CtxMax:N0}" : "") + (sl.Busy && sl.Tps > 0 ? $" · {sl.Tps:0.0} t/s" : ""));
        if (s.Models.Count > 0) Row(Strings.TuiModels, string.Join(", ", s.Models.Select(m => m.Name)));
        if (s.Clients.Length > 0) Row(Strings.Clients, string.Join(", ", s.Clients));
    }

    private string HintLine(int w)
    {
        string hint = _flash.Length > 0 ? _p.Warn("  " + _flash)
            : _p.Muted("  ") + Key("w") + _p.Muted(" web  ") + Key("c") + _p.Muted(" code  ") + Key("r") + _p.Muted(" recent  ") +
              Key("m") + _p.Muted(" memory  ") + Key("↑↓") + _p.Muted(" details  ") + Key("?") + _p.Muted(" help  ") + Key("q") + _p.Muted(" quit");
        string right = _p.Muted(_p.Theme.Name + " ");
        int gap = w - Ansi.Width(hint) - Ansi.Width(right);
        return gap > 0 ? hint + new string(' ', gap) + right : hint;
    }

    private string Key(string k) => _p.Accent(k, true);

    // ── Balken und Verlauf ──

    private const string Eighths = " ▏▎▍▌▋▊▉█", Sparks = "▁▂▃▄▅▆▇█";

    private string Bar(double frac, int width, Rgb color)
    {
        frac = double.IsFinite(frac) ? Math.Clamp(frac, 0, 1) : 0;
        double cells = frac * width;
        int full = (int)cells, part = (int)Math.Round((cells - full) * 8);
        if (part == 8) { full++; part = 0; }
        var fill = new string('█', full) + (full < width && part > 0 ? Eighths[part].ToString() : "");
        return _p.Frame("▕") + _p.C(color, fill) + _p.Frame(new string('·', Math.Max(0, width - fill.Length))) + _p.Frame("▏");
    }

    private Rgb Level(double frac) => frac > 0.92 ? _p.Theme.Bad : frac > 0.80 ? _p.Theme.Warn : _p.Theme.Accent;

    private string Spark(RemoteServer s, int n)
    {
        var values = new List<double>();
        for (int i = Math.Max(0, s.HistoryCount - n); i < s.HistoryCount; i++) values.Add(s.HistoryAt(i));
        if (values.Count == 0) return new string(' ', n);
        double max = Math.Max(10, values.Max());
        var sb = new StringBuilder(new string(' ', n - values.Count));
        foreach (double v in values) sb.Append(v <= 0.01 ? '▁' : Sparks[(int)Math.Min(7, v / max * 7.99)]);
        return _p.Second(sb.ToString());
    }

    // Ein fester Stand (für StatusLines)
    private sealed class FixedSource(StateSnapshot state, int port) : IStateSource
    {
        public Task<StateSnapshot?> PollAsync(CancellationToken ct) => Task.FromResult<StateSnapshot?>(state);
        public string Status => "";
        public int Port => port;
        public void Dispose() { }
    }
}
