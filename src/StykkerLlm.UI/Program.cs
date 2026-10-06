using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Photino.NET;
using StykkerLlm.Core;
using StykkerLlm.Platform.Windows;

namespace StykkerLlm.UI;

// StykkerUI: das Fenster um die Weboberfläche. Ablauf:
//  1. Läuft der Server? Sonst starten (wie "stykker web") und warten, bis er antwortet.
//  2. Ein eigenes Gerätetoken holen (einmal, mit dem Schlüssel des Datenordners; danach aus web-shell.dat).
//  3. Im Fenster eine kleine Seite laden, die das Token an /pair/adopt schickt – der Server setzt das Cookie und leitet
//     auf die Startseite. Ab da ist es die normale Weboberfläche.
// Ohne GPU: unter Windows bekommt WebView2 --disable-gpu, unter Linux WebKitGTK WEBKIT_DISABLE_COMPOSITING_MODE=1.
internal static class Program
{
    public const string TokenFile = "web-shell.dat";

    [STAThread]
    private static int Main(string[] args)
    {
        var opt = ShellArgs.Parse(args);
        var paths = opt.DataDir != null ? new AppPaths(Path.GetFullPath(opt.DataDir)) : AppPaths.Default();
        IPlatform platform = OperatingSystem.IsWindows() ? new WindowsPlatform() : new BasicPlatform();

        if (!opt.Gpu && OperatingSystem.IsLinux())
        {
            // WebKitGTK: ohne Compositing zeichnet es in Software (kein VRAM); die zweite Variable umgeht DMA-BUF-Fehler
            Environment.SetEnvironmentVariable("WEBKIT_DISABLE_COMPOSITING_MODE", "1");
            Environment.SetEnvironmentVariable("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
        }

        // Ein Fenster je Datenordner: ein zweiter Start holt das vorhandene nach vorn (auch aus dem Tray) und endet
        using var single = new SingleInstance(SingleInstance.NameFor(paths.Root));
        if (!single.IsFirst)
        {
            single.SignalFirst();
            ShellLog.Write(paths, "start: already open, brought to front");
            return 0;
        }
        ShellLog.Write(paths, $"start: port {opt.Port}, gpu {opt.Gpu}");
        string page = "", adopt = "";
        bool startedServer = false;
        try
        {
            var start = StartPage(paths, platform, opt).GetAwaiter().GetResult();
            page = start.Page;
            adopt = start.AdoptPage;       // dieselbe Seite holt das Fenster aus dem Tray zurück
            startedServer = start.StartedServer;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            page = Html.Message(Strings.ShellFailed, ex.Message);
            ShellLog.Write(paths, $"start failed: {ex.GetType().Name}: {ex.Message}");
        }

        var window = new PhotinoWindow()
            .SetTitle("StykkerLLM")
            .SetUseOsDefaultSize(false)
            .SetSize(new System.Drawing.Size(1280, 880))
            .Center()
            .SetTemporaryFilesPath(Path.Combine(paths.Root, "web-shell"));   // Cookies und Cache der WebView
        var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icon)) window.SetIconFile(icon);
        if (!opt.Gpu && OperatingSystem.IsWindows()) window.SetBrowserControlInitParameters("--disable-gpu --disable-gpu-compositing");

        // „Beim Schließen fragen, ob in den Tray": Tray-Symbol und Verstecken gibt es nur unter Windows.
        // Gefragt wird, sobald die Oberfläche steht – auch wenn den Server ein anderer gestartet hat.
        if (adopt.Length > 0 && OperatingSystem.IsWindows())
        {
            var closeToTray = new CloseToTray(window, paths, platform, opt.Port, adopt, startedServer);
            window.RegisterWindowClosingHandler(closeToTray.OnClosing);
            window.RegisterWebMessageReceivedHandler(closeToTray.OnMessage);
            single.ActivationRequested += () => Try(() => window.Invoke(closeToTray.Activate));
        }

        window.LoadRawString(page);
        ShellLog.Write(paths, "window: open");
        window.WaitForClose();
        ShellLog.Write(paths, "window: closed");
        return 0;
    }

    // Kommt eine Aktivierung, bevor das Fenster steht, gibt es noch nichts nach vorn zu holen
    private static void Try(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException) { Debug.WriteLine(ex.Message); }
    }

    // Die erste Seite im Fenster: ein Formular, das sich selbst an /pair/adopt schickt (Token als Cookie setzen)
    private static async Task<StartPageResult> StartPage(AppPaths paths, IPlatform platform, ShellArgs opt)
    {
        var baseUrl = NetAddr.Url("127.0.0.1", opt.Port);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        bool started = false;
        if (!await ServerClient.IsRunningAsync(opt.Port).ConfigureAwait(false))
        {
            ShellLog.Write(paths, "server: not answering, starting it");
            StartServer(paths, opt.Port);
            started = true;
            var end = DateTime.Now.AddSeconds(30);
            while (!await ServerClient.IsRunningAsync(opt.Port).ConfigureAwait(false))
            {
                if (DateTime.Now > end) throw new InvalidOperationException(Strings.ServerNotRunning);
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
        ShellLog.Write(paths, "server: running");
        var token = await Token(http, baseUrl, paths, platform).ConfigureAwait(false);
        ShellLog.Write(paths, "token: ok");
        var adopt = Html.Adopt(baseUrl, token, opt.Page);
        return new StartPageResult(adopt, adopt, started);
    }

    private static void StartServer(AppPaths paths, int port)
    {
        var exe = ServerLocator.Find() ?? throw new InvalidOperationException(Strings.ShellNoServer);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
        };
        foreach (var a in new[] { "--data-dir", paths.Root, "--port", port.ToString(Strings.Inv), "--no-browser", "--no-tray" }) psi.ArgumentList.Add(a);
        Process.Start(psi);
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

internal sealed record ShellArgs(int Port, string? DataDir, bool Gpu, string Page)
{
    // StykkerUI [--port 8078] [--data-dir <ordner>] [--gpu] [--page runs]
    public static ShellArgs Parse(string[] args)
    {
        int port = 8078; string? data = null; bool gpu = false; string page = "";
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): port = p; i++; break;
                case "--data-dir" when i + 1 < args.Length: data = args[++i]; break;
                case "--gpu": gpu = true; break;
                case "--page" when i + 1 < args.Length: page = args[++i]; break;
            }
        }
        return new ShellArgs(port, data, gpu, page);
    }
}

// Was der Start ergeben hat: die Seite fürs Fenster (und dieselbe Seite noch einmal, um das Fenster aus dem Tray
// zurückzuholen) und ob diese Hülle den Server dabei selbst gestartet hat – dann beendet sie ihn auch wieder.
internal sealed record StartPageResult(string Page, string AdoptPage, bool StartedServer);

internal static class Html
{
    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // Ladebild in den Farben der Vorgabe (Deep Sea): Rhombus mit Lichthof, darunter ein wandernder Balken
    private const string Style = "body{margin:0;background:radial-gradient(60% 50% at 50% 40%,#0d1830,#05070d 70%);color:#dce9fa;font:15px Segoe UI,system-ui,sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;text-align:center}"
        + ".logo{font-size:44px;color:#6cb8ff;text-shadow:0 0 24px #6cb8ffaa;animation:p 1.6s ease-in-out infinite;display:inline-block}"
        + ".name{letter-spacing:.18em;font-weight:600;margin-top:10px}.name b{color:#6cb8ff}"
        + ".load{width:160px;height:3px;margin:16px auto 0;border-radius:3px;background:#96beff18;overflow:hidden}"
        + ".load i{display:block;width:40%;height:100%;background:linear-gradient(90deg,transparent,#6cb8ff,transparent);animation:m 1.2s ease-in-out infinite}"
        + "@keyframes p{50%{transform:scale(1.15) rotate(45deg);text-shadow:0 0 40px #6cb8ff}}@keyframes m{from{transform:translateX(-100%)}to{transform:translateX(250%)}}";

    public static string Adopt(string baseUrl, string token, string page) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><style>{{Style}}</style></head><body>
        <form id="f" method="post" action="{{Esc(baseUrl)}}/pair/adopt">
        <input type="hidden" name="token" value="{{Esc(token)}}"><input type="hidden" name="next" value="{{Esc(page)}}"></form>
        <div><div class="logo">◆</div><div class="name">STYKKER <b>LLM</b></div><div class="load"><i></i></div></div>
        <script>document.getElementById('f').submit();</script></body></html>
        """;

    public static string Message(string title, string text) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><style>{{Style}}</style></head><body>
        <div><h2 style="font-weight:600">{{Esc(title)}}</h2><p style="color:#6f86a6">{{Esc(text)}}</p></div></body></html>
        """;

    // Nachrichten der Rückfrage-Seite an den Prozess (window.external.sendMessage, die Brücke der Fenster-Hülle)
    public const string MsgTray = "shell.tray", MsgQuit = "shell.quit", MsgCancel = "shell.cancel", MsgAck = "shell.ack";

    // Die Frage als Nachricht an die Seite: ui.js zeichnet daraus den Dialog über der aktuellen Seite (Texte von hier,
    // weil die Seite Strings nicht kennt)
    public static string AskMessage() => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["t"] = "ask", ["text"] = Strings.ShellCloseText, ["tray"] = Strings.ShellCloseTray, ["quit"] = Strings.ShellCloseQuit,
        ["cancel"] = Strings.ShellCloseCancel, ["remember"] = Strings.ShellCloseRemember,
    });

    // Die Rückfrage beim Schließen – eine Seite im Fenster in den Farben des Themas (dieselben CSS-Variablen wie die
    // Weboberfläche), damit sie genauso aussieht wie der Rest und keine zweite Oberfläche gebraucht wird.
    public static string AskClose(ThemeInfo theme) => $$$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>{{{Esc(Strings.AppName)}}}</title>
        <style>{{{theme.CssVariables()}}}
        body{margin:0;background:linear-gradient(180deg,var(--bg-top),var(--bg-bottom) 1200px);color:var(--ink);font:15px/1.6 var(--font);display:flex;align-items:center;justify-content:center;min-height:100vh}
        .box{background:linear-gradient(180deg,var(--card-top),var(--card-bottom));border:1px solid var(--card-border);border-radius:var(--radius);padding:24px 22px;max-width:440px;margin:16px;box-shadow:inset 0 1px 0 var(--top-line),0 18px 48px rgb(0 0 0/.5)}
        h1{font-size:16px;margin:0 0 10px;letter-spacing:.06em}.acc{color:var(--acc)}
        p{color:var(--muted);margin:0 0 12px}label{display:block;color:var(--muted);font-size:13px;margin:0 0 16px;cursor:pointer}
        .row{display:flex;gap:10px;justify-content:flex-end;flex-wrap:wrap}
        button{background:color-mix(in srgb,var(--bg) 88%,var(--acc));color:var(--ink);border:1px solid color-mix(in srgb,var(--bg) 45%,var(--acc));border-radius:999px;padding:8px 18px;font:inherit;cursor:pointer}
        button:hover{background:color-mix(in srgb,var(--bg) 70%,var(--acc))}button.ghost{background:transparent;color:var(--muted);border-color:var(--line)}
        </style></head><body><div class="box">
        <h1>◆ STYKKER <span class="acc">LLM</span></h1>
        <p>{{{Esc(Strings.ShellCloseText)}}}</p>
        <label><input type="checkbox" id="rem"> {{{Esc(Strings.ShellCloseRemember)}}}</label>
        <div class="row">
        <button class="ghost" onclick="tell('{{{MsgCancel}}}',false)">{{{Esc(Strings.ShellCloseCancel)}}}</button>
        <button class="ghost" onclick="tell('{{{MsgQuit}}}',true)">{{{Esc(Strings.ShellCloseQuit)}}}</button>
        <button onclick="tell('{{{MsgTray}}}',true)">{{{Esc(Strings.ShellCloseTray)}}}</button>
        </div></div>
        <script>function tell(m,r){if(r&&document.getElementById('rem').checked)m+='!';try{window.external.sendMessage(m)}catch(e){/* kein Fenster: nichts zu melden */}}</script></body></html>
        """;
}
