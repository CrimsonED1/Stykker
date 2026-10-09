using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Photino.NET;
using StykkerLlm.Core;
using StykkerLlm.Platform.Windows;

namespace StykkerLlm.UI;

// StykkerUI: das Fenster um die Weboberfläche. Ablauf:
//  1. Sofort ein Fenster mit Startbild (Statuszeile), damit man sieht, dass etwas passiert.
//  2. Im Hintergrund: läuft der Server? Sonst starten (wie "stykker web") und warten, bis er antwortet.
//  3. Ein eigenes Gerätetoken holen (einmal, mit dem Schlüssel des Datenordners; danach aus web-shell.dat).
//  4. Eine kleine Seite laden, die das Token an /pair/adopt schickt – der Server setzt das Cookie und leitet auf die
//     Startseite. Ab da ist es die normale Weboberfläche.
//  5. Solange das Fenster offen ist (auch im Tray), meldet es sich alle paar Sekunden beim Server (ServerHolds). Ist
//     kein Fenster, keine TUI und keine Webseite mehr offen, beendet sich der Server von selbst.
// Ohne GPU: unter Windows bekommt WebView2 --disable-gpu, unter Linux WebKitGTK WEBKIT_DISABLE_COMPOSITING_MODE=1.
internal static class Program
{
    public const string TokenFile = "web-shell.dat";
    // Die Seite im Fenster meldet sich (shell.ready), sobald ihr Skript läuft. Vorher darf nichts an die WebView
    // gehen: SendWebMessage vor dem Bereitsein der WebView2 endet in Photino mit einer Zugriffsverletzung (0xc0000005).
    private static readonly ManualResetEventSlim PageReady = new(false);

    [STAThread]
    private static int Main(string[] args)
    {
        var opt = ShellArgs.Parse(args);
        var paths = opt.DataDir != null ? new AppPaths(Path.GetFullPath(opt.DataDir)) : AppPaths.Default();
        IPlatform platform = OperatingSystem.IsWindows() ? new WindowsPlatform() : new BasicPlatform();
        AppLog.Init("StykkerUI", paths);
        AppLog.CatchUnhandled();

        if (!opt.Gpu && OperatingSystem.IsLinux())
        {
            // WebKitGTK: ohne Compositing zeichnet es in Software (kein VRAM); die zweite Variable umgeht DMA-BUF-Fehler
            Environment.SetEnvironmentVariable("WEBKIT_DISABLE_COMPOSITING_MODE", "1");
            Environment.SetEnvironmentVariable("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
        }

        // Ein Fenster je Datenordner: ein zweiter Start holt das vorhandene nach vorn (auch aus dem Tray) und endet
        var name = SingleInstance.NameFor(paths.Root);
        if (!opt.Child)
        {
            using var guard = new SingleInstance(name);
            if (!guard.IsFirst)
            {
                guard.SignalFirst();
                AppLog.Write("start: already open, brought to front");
                return 0;
            }
            return Watch(args, guard, name);
        }
        using var single = new SingleInstance(name + "-window");
        AppLog.Write($"start: port {opt.Port}, gpu {opt.Gpu}");

        var window = new PhotinoWindow()
            .SetTitle("StykkerLLM")
            .SetUseOsDefaultSize(false)
            .SetSize(new System.Drawing.Size(1280, 880))
            .Center()
            .SetTemporaryFilesPath(Path.Combine(paths.Root, "web-shell"));   // Cookies und Cache der WebView
        var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icon)) window.SetIconFile(icon);
        // Ohne GPU über die Umgebungsvariable, die WebView2 selbst liest – nicht über SetBrowserControlInitParameters:
        // damit stürzte Photino 4.0.16 zeitweise beim Anlegen des Fensters ab (Heap-Beschädigung 0xc0000374 in Photino_ctor).
        if (!opt.Gpu && OperatingSystem.IsWindows())
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-gpu --disable-gpu-compositing");

        using var hold = new ServerHold();
        // „Beim Schließen fragen, ob in den Tray": Tray-Symbol und Verstecken gibt es nur unter Windows.
        CloseToTray? closeToTray = OperatingSystem.IsWindows() ? new CloseToTray(window, paths, hold.Release, hold.Hint) : null;
        if (closeToTray != null && OperatingSystem.IsWindows())
        {
            window.RegisterWindowClosingHandler(closeToTray.OnClosing);
            single.ActivationRequested += () => Try(() => window.Invoke(() => { if (OperatingSystem.IsWindows()) closeToTray.Activate(); }));
        }
        window.RegisterWebMessageReceivedHandler((s, m) =>
        {
            if (m == Html.MsgReady) { PageReady.Set(); return; }
            if (m == Html.MsgRetry) Boot(window, paths, platform, opt, hold, closeToTray);
            else if (OperatingSystem.IsWindows()) closeToTray?.OnMessage(s, m);
        });
        // Der Start läuft erst, wenn das Fenster steht – sonst gäbe es niemanden, der das Startbild weiterschaltet
        window.RegisterWindowCreatedHandler((_, _) => Boot(window, paths, platform, opt, hold, closeToTray));

        window.LoadRawString(Html.Splash(Strings.ShellConnecting));
        AppLog.Write("window: open");
        window.WaitForClose();
        hold.Release();
        AppLog.Write("window: closed");
        return 0;
    }

    // Der Wächter: startet das eigentliche Fenster als eigenen Prozess und öffnet es neu, wenn es kurz nach dem Start
    // abstürzt. Photino 4.0.16 stürzt gelegentlich schon beim Anlegen von Fenster und WebView2 ab (Heap-Beschädigung
    // 0xc0000374 in Photino_ctor, nativ, nicht abfangbar) – meist beim ersten Versuch, beim zweiten klappt es.
    private const int CrashHeap = unchecked((int)0xC0000374), CrashAccess = unchecked((int)0xC0000005);

    private static int Watch(string[] args, SingleInstance guard, string name)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        Process? child = null;
        // ein zweiter Start meldet sich beim Wächter; er reicht es an das Fenster weiter
        guard.ActivationRequested += () => { try { using var w = new SingleInstance(name + "-window"); if (!w.IsFirst) w.SignalFirst(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { } };
        for (int attempt = 1; ; attempt++)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(ShellArgs.ChildFlag);
            var started = DateTime.Now;
            child = Process.Start(psi);
            if (child == null) return 1;
            child.WaitForExit();
            int code = child.ExitCode;
            bool crashed = code is CrashHeap or CrashAccess;
            if (!crashed) return code;
            AppLog.Write($"window: crashed at start (0x{code:X8}, attempt {attempt})");
            if (attempt >= 3 || DateTime.Now - started > TimeSpan.FromSeconds(30)) return code;
        }
    }

    // Server finden oder starten, anmelden, Oberfläche laden – im Hintergrund, das Startbild zeigt den Schritt
    private static void Boot(PhotinoWindow window, AppPaths paths, IPlatform platform, ShellArgs opt, ServerHold hold, CloseToTray? closeToTray)
    {
        void Status(string text)
        {
            if (PageReady.IsSet) Try(() => window.Invoke(() => window.SendWebMessage(Html.StatusMessage(text))));
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var start = await StartPage(paths, platform, opt, Status).ConfigureAwait(false);
                hold.Start(NetAddr.Url("127.0.0.1", opt.Port), start.Token);
                if (!PageReady.Wait(TimeSpan.FromSeconds(20))) AppLog.Write("window: page did not report ready, loading anyway");
                window.Invoke(() =>
                {
                    if (OperatingSystem.IsWindows()) closeToTray?.Ready(start.AdoptPage);
                    window.LoadRawString(start.AdoptPage);
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException or JsonException)
            {
                AppLog.Write($"start failed: {ex.GetType().Name}: {ex.Message}");
                PageReady.Wait(TimeSpan.FromSeconds(20));
                Try(() => window.Invoke(() => window.LoadRawString(Html.Message(Strings.ShellFailed, ex.Message))));
            }
        });
    }

    // Kommt eine Aktivierung, bevor das Fenster steht, gibt es noch nichts nach vorn zu holen
    private static void Try(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException) { Debug.WriteLine(ex.Message); }
    }

    // Die erste Seite der Oberfläche: ein Formular, das sich selbst an /pair/adopt schickt (Token als Cookie setzen)
    private static async Task<StartPageResult> StartPage(AppPaths paths, IPlatform platform, ShellArgs opt, Action<string> status)
    {
        var baseUrl = NetAddr.Url("127.0.0.1", opt.Port);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (!await ServerClient.IsRunningAsync(opt.Port).ConfigureAwait(false))
        {
            status(Strings.ShellStarting);
            AppLog.Write("server: not answering, starting it");
            using var proc = StartServer(paths, opt.Port);
            var end = DateTime.Now.AddSeconds(30);
            while (!await ServerClient.IsRunningAsync(opt.Port).ConfigureAwait(false))
            {
                if (DateTime.Now > end) throw new InvalidOperationException(Strings.ServerNotRunning);
                // Der Server hat aufgegeben (Port belegt …): gleich sagen, statt 30 s zu warten
                if (proc?.HasExited == true)
                    throw new InvalidOperationException(proc.ExitCode == 1 ? Strings.ServerPortBusy(opt.Port, Strings.ShellSeeServerLog) : Strings.ServerNotRunning);
                await Task.Delay(300).ConfigureAwait(false);
            }
        }
        AppLog.Write("server: running");
        status(Strings.ShellSigningIn);
        var token = await Token(http, baseUrl, paths, platform).ConfigureAwait(false);
        AppLog.Write("token: ok");
        return new StartPageResult(Html.Adopt(baseUrl, token, opt.Page), token);
    }

    private static Process? StartServer(AppPaths paths, int port)
    {
        var exe = ServerLocator.Find() ?? throw new InvalidOperationException(Strings.ShellNoServer);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
        };
        foreach (var a in new[] { "--data-dir", paths.Root, "--port", port.ToString(Strings.Inv), "--no-browser", "--no-tray" }) psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }

    // Gespeichertes Token prüfen (gilt es noch?), sonst ein neues holen und gebunden an den Benutzer ablegen
    private static async Task<string> Token(HttpClient http, string baseUrl, AppPaths paths, IPlatform platform)
    {
        var file = Path.Combine(paths.Root, TokenFile);
        var saved = Load(file, platform);
        if (saved != null)
        {
            using var check = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/whoami");
            check.Headers.TryAddWithoutValidation(StateJson.DeviceHeader, saved);
            using var resp = await http.SendAsync(check).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return saved;
        }
        var key = ServerClient.ReadKey(paths, platform) ?? throw new InvalidOperationException(Strings.ShellNoKey);
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/pair/local")
        {
            Content = JsonContent.Create(new Dictionary<string, string> { ["name"] = Strings.ShellDeviceName }),
        };
        req.Headers.TryAddWithoutValidation(StateJson.KeyHeader, key);
        using var r = await http.SendAsync(req).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync().ConfigureAwait(false));
        var token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
        if (!r.IsSuccessStatusCode || string.IsNullOrEmpty(token)) throw new InvalidOperationException(Strings.ShellNoKey);
        Save(file, token, platform);
        return token;
    }

    private static string? Load(string file, IPlatform platform)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var raw = File.ReadAllText(file).Trim();
            var blob = Convert.FromBase64String(raw);
            var plain = platform.UnprotectForCurrentUser(blob) ?? blob;
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException) { return null; }
    }

    private static void Save(string file, string token, IPlatform platform)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var blob = platform.ProtectForCurrentUser(bytes) ?? bytes;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        AtomicFile.WriteAllText(file, Convert.ToBase64String(blob));
    }
}

internal sealed record ShellArgs(int Port, string? DataDir, bool Gpu, string Page, bool Child = false)
{
    // intern: der Wächter startet so das eigentliche Fenster
    public const string ChildFlag = "--window-process";

    // StykkerUI [--port 17400] [--data-dir <ordner>] [--gpu] [--page runs]
    public static ShellArgs Parse(string[] args)
    {
        int port = 17400; string? data = null; bool gpu = false; string page = ""; bool child = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
#if DEBUG
                // Entwickler-Schalter (nur Debug): anderer Port/Datenordner, gleich eine bestimmte Seite
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): port = p; i++; break;
                case "--data-dir" when i + 1 < args.Length: data = args[++i]; break;
                case "--page" when i + 1 < args.Length: page = args[++i]; break;
#endif
                case "--gpu": gpu = true; break;
                case ChildFlag: child = true; break;
            }
        }
        return new ShellArgs(port, data, gpu, page, child);
    }
}

// Was der Start ergeben hat: die Anmeldeseite (holt das Fenster auch nach einer Notfall-Seite zurück) und das Token
internal sealed record StartPageResult(string AdoptPage, string Token);

// Das Fenster hält den Server am Leben (POST /api/hold alle 5 s, mit dem Gerätetoken), beim Beenden meldet es sich ab
internal sealed class ServerHold : IDisposable
{
    private readonly string _id = "ui:" + Environment.ProcessId;
    private ServerClient? _client;
    private Timer? _timer;
    private int _released;

    public void Start(string baseUrl, string token)
    {
        if (_client != null) return;
        _client = ServerClient.ForDevice(baseUrl, token);
        _timer = new Timer(_ => { _ = _client.HoldAsync(_id); }, null, 0, 5000);
    }

    // Vor der Rückfrage: was passiert mit dem Server, wenn dieses Fenster jetzt geht (null, wenn der Server nicht antwortet).
    // Höchstens eine Sekunde; der Server läuft auf diesem Rechner.
    public ShutdownHint? Hint(bool ownPage)
    {
        if (_client == null) return null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        return _client.ShutdownHintAsync(_id, ownPage ? 1 : 0, cts.Token).GetAwaiter().GetResult();
    }

    // einmal; danach beendet sich der Server nach wenigen Sekunden, wenn ihn sonst niemand braucht
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1 || _client == null) return;
        _timer?.Dispose();
        try { _client.ReleaseAsync(_id).Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        AppLog.Write("hold released");
    }

    public void Dispose()
    {
        Release();
        _client?.Dispose();
    }
}

internal static class Html
{
    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // Ladebild in den Farben von Dark: Rhombus mit Lichthof, darunter ein wandernder Balken
    private const string Style = "body{margin:0;background:radial-gradient(60% 50% at 50% 40%,#0e1430,#060916 70%);color:#e8eeff;font:15px Segoe UI,system-ui,sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;text-align:center}"
        + ".logo{font-size:44px;color:#4fe3ff;text-shadow:0 0 24px #4fe3ffaa;animation:p 1.6s ease-in-out infinite;display:inline-block}"
        + ".name{letter-spacing:.18em;font-weight:600;margin-top:10px}.name b{color:#4fe3ff}"
        + ".load{width:160px;height:3px;margin:16px auto 0;border-radius:3px;background:#ffffff1c;overflow:hidden}"
        + ".load i{display:block;width:40%;height:100%;background:linear-gradient(90deg,transparent,#4fe3ff,transparent);animation:m 1.2s ease-in-out infinite}"
        + "@keyframes p{50%{transform:scale(1.15) rotate(45deg);text-shadow:0 0 40px #4fe3ff}}@keyframes m{from{transform:translateX(-100%)}to{transform:translateX(250%)}}";

    public static string Adopt(string baseUrl, string token, string page) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><style>{{Style}}</style></head><body>
        <form id="f" method="post" action="{{Esc(baseUrl)}}/pair/adopt">
        <input type="hidden" name="token" value="{{Esc(token)}}"><input type="hidden" name="next" value="{{Esc(page)}}"></form>
        <div><div class="logo">◆</div><div class="name">STYKKER <b>LLM</b></div><div class="load"><i></i></div></div>
        <script>document.getElementById('f').submit();</script></body></html>
        """;

    // Startbild: sofort da, die Statuszeile schaltet der Start per Nachricht weiter (StatusMessage)
    public static string Splash(string status) => $$$"""
        <!doctype html><html><head><meta charset="utf-8"><style>{{{Style}}}.st{color:#6f86a6;font-size:13px;margin-top:14px;min-height:1.4em}</style></head><body>
        <div><div class="logo">◆</div><div class="name">STYKKER <b>LLM</b></div><div class="load"><i></i></div><div class="st" id="st">{{{Esc(status)}}}</div></div>
        <script>try{window.external.sendMessage('shell.ready')}catch(e){}try{window.external.receiveMessage(function(m){try{var o=JSON.parse(m);if(o.t==='status')document.getElementById('st').textContent=o.text}catch(e){}})}catch(e){}</script>
        </body></html>
        """;

    public static string StatusMessage(string text) => JsonSerializer.Serialize(new Dictionary<string, string> { ["t"] = "status", ["text"] = text });

    public static string Message(string title, string text) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><style>{{Style}}button{margin-top:10px;background:#0f1a2c;color:#dce9fa;border:1px solid #2a4a74;border-radius:999px;padding:8px 20px;font:inherit;cursor:pointer}button:hover{background:#16263f}</style></head><body>
        <div><div class="logo" style="animation:none">◆</div><h2 style="font-weight:600">{{Esc(title)}}</h2><p style="color:#6f86a6;max-width:520px">{{Esc(text)}}</p>
        <button onclick="try{window.external.sendMessage('{{MsgRetry}}')}catch(e){}">{{Esc(Strings.ShellRetry)}}</button></div></body></html>
        """;

    // Nachrichten der Rückfrage-Seite an den Prozess (window.external.sendMessage, die Brücke der Fenster-Hülle)
    public const string MsgTray = "shell.tray", MsgQuit = "shell.quit", MsgCancel = "shell.cancel", MsgAck = "shell.ack", MsgRetry = "shell.retry", MsgReady = "shell.ready";

    // Die Frage als Nachricht an die Seite: ui.js zeichnet daraus den Dialog über der aktuellen Seite (Texte von hier,
    // weil die Seite Strings nicht kennt)
    public static string AskMessage(ShutdownHint? hint) => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["t"] = "ask", ["text"] = Strings.ShellCloseText, ["tray"] = Strings.ShellCloseTray, ["quit"] = Strings.ShellCloseQuit,
        ["cancel"] = Strings.ShellCloseCancel, ["remember"] = Strings.ShellCloseRemember,
        ["note"] = hint?.Note ?? "", ["warning"] = hint?.Warning ?? "",
    });

    // Der Hinweis zum Server unter der Frage; die Warnung (wenn eine Anfrage abbräche) in der Warnfarbe
    private static string HintHtml(ShutdownHint? hint) => hint == null ? "" :
        $"<p>{Esc(hint.Note)}</p>" + (hint.Warning == null ? "" : $"<p class=\"warn\">{Esc(hint.Warning)}</p>");

    // Die Rückfrage beim Schließen – eine Seite im Fenster in den Farben des Themas (dieselben CSS-Variablen wie die
    // Weboberfläche), damit sie genauso aussieht wie der Rest und keine zweite Oberfläche gebraucht wird.
    public static string AskClose(ThemeInfo theme, ShutdownHint? hint) => $$$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>{{{Esc(Strings.AppName)}}}</title>
        <style>{{{theme.CssVariables()}}}
        body{margin:0;background:linear-gradient(180deg,var(--bg-top),var(--bg-bottom) 1200px);color:var(--ink);font:15px/1.6 var(--font);display:flex;align-items:center;justify-content:center;min-height:100vh}
        .box{background:linear-gradient(180deg,var(--card-top),var(--card-bottom));border:1px solid var(--card-border);border-radius:var(--radius);padding:24px 22px;max-width:440px;margin:16px;box-shadow:inset 0 1px 0 var(--top-line),0 18px 48px rgb(0 0 0/.5)}
        h1{font-size:16px;margin:0 0 10px;letter-spacing:.06em}.acc{color:var(--acc)}
        p{color:var(--muted);margin:0 0 12px}label{display:block;color:var(--muted);font-size:13px;margin:0 0 16px;cursor:pointer}p.warn{color:var(--warn)}
        .row{display:flex;gap:10px;justify-content:flex-end;flex-wrap:wrap}
        button{background:color-mix(in srgb,var(--bg) 88%,var(--acc));color:var(--ink);border:1px solid color-mix(in srgb,var(--bg) 45%,var(--acc));border-radius:999px;padding:8px 18px;font:inherit;cursor:pointer}
        button:hover{background:color-mix(in srgb,var(--bg) 70%,var(--acc))}button.ghost{background:transparent;color:var(--muted);border-color:var(--line)}
        </style></head><body><div class="box">
        <h1>◆ STYKKER <span class="acc">LLM</span></h1>
        <p>{{{Esc(Strings.ShellCloseText)}}}</p>
        {{{HintHtml(hint)}}}
        <label><input type="checkbox" id="rem"> {{{Esc(Strings.ShellCloseRemember)}}}</label>
        <div class="row">
        <button class="ghost" onclick="tell('{{{MsgCancel}}}',false)">{{{Esc(Strings.ShellCloseCancel)}}}</button>
        <button class="ghost" onclick="tell('{{{MsgQuit}}}',true)">{{{Esc(Strings.ShellCloseQuit)}}}</button>
        <button onclick="tell('{{{MsgTray}}}',true)">{{{Esc(Strings.ShellCloseTray)}}}</button>
        </div></div>
        <script>function tell(m,r){if(r&&document.getElementById('rem').checked)m+='!';try{window.external.sendMessage(m)}catch(e){/* kein Fenster: nichts zu melden */}}</script></body></html>
        """;
}
