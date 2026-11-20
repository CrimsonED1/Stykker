using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Photino.NET;
using Stykker.Pairing;
using Stykker.Shared.Windows;

namespace StykkerMeshShare.UI;

// StykkerMeshShare: das Fenster um die Oberfläche des Servers (Photino: unter Windows WebView2), aufgebaut wie der
// Stykker Starter:
//   - Es startet den Server (ohne dessen eigenes Tray-Symbol), falls er noch nicht läuft, und zeigt dessen Seite.
//   - Randlos: die Titelleiste ist die Kopfzeile der Seite (?window=1). Die Seite schickt einfache Nachrichten
//     (window:minimize/maximize/restore/close, window:move dx,dy), das Fenster meldet window:state zurück.
//   - Ein Tray-Symbol: Öffnen, Ordner öffnen, Beenden. Das X fragt „im Tray lassen oder beenden?“ (merkbar).
//   - Beenden (Tray oder Frage) beendet auch den Server: mit der GUI endet der Abgleich.
//   - Ein zweiter Start holt nur das Fenster nach vorn.
// Das Fenster zeichnet wie die anderen Werkzeuge ohne Grafikkarte (--disable-gpu); mit --gpu über die Grafikkarte.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var opt = Options.Parse(args);
        if (!opt.Gpu && OperatingSystem.IsWindows())
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-gpu --disable-gpu-compositing");

        // Läuft das Fenster schon (gleicher Port), nur dieses nach vorn holen.
        using var open = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\StykkerMeshShare.UI.{opt.Port}", out bool first);
        if (!first)
        {
            open.Set();
            return 0;
        }
        return new WindowApp(opt, open).Run();
    }
}

internal sealed class WindowApp(Options opt, EventWaitHandle openSignal)
{
    private const string IconFile = "StykkerMeshShare.ico";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly BlockingCollection<Action> _requests = new();
    private readonly string _settingsFile = Path.Combine(CacheDir(), "ui.json");
    private PhotinoWindow? _window;
    private TrayIcon? _tray;
    private volatile bool _quit;
    private bool _allowClose;     // das nächste Schließen nicht abfangen (Tray gewählt, oder beenden)
    private bool _ready;          // die Seite hat sich gemeldet: erst dann kann sie fragen
    private string? _startError;

    private sealed record UiSettings(string CloseAction = "ask");   // ask | tray | quit
    private UiSettings _settings = new();

    public int Run()
    {
        _settings = LoadSettings();
        try { EnsureServer(opt.Port, opt.Server); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { _startError = ex.Message; }

        if (OperatingSystem.IsWindows() && TrayIcon.Possible)
        {
            _tray = new TrayIcon(TrayItems, OnTrayMenu, ShowWindow);
            if (!_tray.TryShow("StykkerMeshShare", out var error)) Console.Error.WriteLine($"[tray] {error}");
        }
        // Zweiter Start: das Fenster nach vorn.
        var listener = new Thread(() =>
        {
            while (!_quit)
            {
                try { if (openSignal.WaitOne(1000) && !_quit) ShowWindow(); }
                catch (ObjectDisposedException) { return; }
            }
        }) { IsBackground = true, Name = "open-signal" };
        listener.Start();

        Post(OpenWindow);
        while (!_quit) _requests.Take()();
        if (OperatingSystem.IsWindows()) _tray?.Dispose();
        return 0;
    }

    private void Post(Action action)
    {
        try { _requests.Add(action); }
        catch (InvalidOperationException) { }
    }

    // Von jedem Thread: ist das Fenster offen, nach vorn holen (auf seinem Thread); sonst öffnen.
    private void ShowWindow()
    {
        var window = _window;
        if (window == null) { Post(OpenWindow); return; }
        try
        {
            window.Invoke(() =>
            {
                window.SetMinimized(false);
                if (OperatingSystem.IsWindows()) SetForegroundWindow(window.WindowHandle);
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { Post(OpenWindow); }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    // Läuft auf dem Hauptthread und kehrt erst zurück, wenn das Fenster geschlossen ist.
    private void OpenWindow()
    {
        if (_quit || _window != null) return;
        _ready = false;
        _allowClose = false;
        var url = $"http://127.0.0.1:{opt.Port}/?window=1" + (opt.Gpu ? "" : "&shell=1");
        var window = new PhotinoWindow()
            .SetTitle("StykkerMeshShare")
            .SetUseOsDefaultSize(false)
            .SetUseOsDefaultLocation(false)
            .SetSize(new System.Drawing.Size(1100, 820))
            .Center()
            .SetChromeless(true)
            .SetTemporaryFilesPath(Path.Combine(CacheDir(), "web-shell"));
        var icon = Path.Combine(AppContext.BaseDirectory, IconFile);
        if (File.Exists(icon)) window.SetIconFile(icon);
        window.RegisterWebMessageReceivedHandler((_, message) => OnMessage(window, message));
        window.RegisterMaximizedHandler((_, _) => SendState(window, true));
        window.RegisterRestoredHandler((_, _) => SendState(window, false));
        window.RegisterWindowClosingHandler(OnClosing);
        if (_startError == null) window.Load(new Uri(url));
        else window.LoadRawString(ErrorPage(_startError));
        _window = window;
        window.WaitForClose();
        _window = null;
        _ready = false;
    }

    // Das X (eigene Titelleiste, Alt+F4, Taskleiste). true = offen lassen (Photino.NET: true heißt „nicht schließen“).
    private bool OnClosing(object? sender, EventArgs e)
    {
        if (_quit || _allowClose) return false;
        if (!_ready) return false;   // keine Seite, die fragen könnte: in den Tray
        switch (_settings.CloseAction)
        {
            case "tray": return false;
            case "quit": Quit(); return false;
        }
        Send(_window, "window:ask-close");
        return true;
    }

    // Nachrichten der Seite (einfache Zeichenketten). Läuft auf dem Fensterthread.
    private void OnMessage(PhotinoWindow window, string message)
    {
        try
        {
            switch (message)
            {
                case "window:minimize": window.SetMinimized(true); return;
                case "window:maximize": window.SetMaximized(true); return;
                case "window:restore": window.SetMaximized(false); return;
                case "window:close": window.Close(); return;
                case "window:ready": _ready = true; SendState(window, window.Maximized); return;
            }
            // Antwort auf die Frage beim Schließen: „window:close-tray“ / „window:close-quit“, mit „ remember“ gemerkt.
            if (message.StartsWith("window:close-", StringComparison.Ordinal))
            {
                var parts = message.Split(' ');
                var choice = parts[0]["window:close-".Length..];
                if (choice is not ("tray" or "quit")) return;
                if (parts.Length > 1 && parts[1] == "remember") SaveSettings(_settings with { CloseAction = choice });
                if (choice == "quit") { Quit(); return; }
                _allowClose = true;
                window.Close();
                return;
            }
            if (message.StartsWith("window:move ", StringComparison.Ordinal))
            {
                var parts = message["window:move ".Length..].Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out var dx) && int.TryParse(parts[1], out var dy) && !window.Maximized)
                    window.Offset(new System.Drawing.Point(dx, dy));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    // Beenden: erst den Server (mit der GUI endet der Abgleich), dann das Fenster und die Schleife.
    private void Quit()
    {
        if (_quit) return;
        _quit = true;
        ShutdownServer();
        var window = _window;
        if (window != null)
        {
            _allowClose = true;
            try { window.Invoke(window.Close); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        }
        Post(() => { });   // die Schleife wecken, damit sie _quit sieht
    }

    private void ShutdownServer() => PostToServer("/api/shutdown");

    private void OpenFolder() => PostToServer("/api/open-folder");

    private void PostToServer(string path)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            http.PostAsync($"http://127.0.0.1:{opt.Port}{path}", content).GetAwaiter().GetResult().Dispose();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) { }
    }

    // ── Tray ──
    private const int MenuOpen = 1, MenuFolder = 2, MenuAsk = 3, MenuQuit = 4;

    private IReadOnlyList<TrayIcon.Item> TrayItems()
    {
        var items = new List<TrayIcon.Item>
        {
            new(MenuOpen, "Open StykkerMeshShare"),
            new(MenuFolder, "Open the shared folder"),
            new(0, "", Separator: true),
        };
        if (_settings.CloseAction != "ask")
            items.Add(new TrayIcon.Item(MenuAsk, _settings.CloseAction == "tray"
                ? "Closing the window keeps it in the tray – ask again"
                : "Closing the window quits – ask again"));
        items.Add(new TrayIcon.Item(MenuQuit, "Quit StykkerMeshShare (stops syncing)"));
        return items;
    }

    private void OnTrayMenu(int id)
    {
        switch (id)
        {
            case MenuOpen: ShowWindow(); break;
            case MenuFolder: OpenFolder(); break;
            case MenuAsk: SaveSettings(_settings with { CloseAction = "ask" }); break;
            case MenuQuit: Quit(); break;
        }
    }

    private static void SendState(PhotinoWindow window, bool maximized) => Send(window, "window:state " + (maximized ? "maximized" : "restored"));

    private static void Send(PhotinoWindow? window, string message)
    {
        try { window?.SendWebMessage(message); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    private UiSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFile) && JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(_settingsFile)) is { } stored
                && stored.CloseAction is "ask" or "tray" or "quit") return stored;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new UiSettings();
    }

    private void SaveSettings(UiSettings settings)
    {
        _settings = settings;
        try
        {
            var temp = _settingsFile + ".new";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, _settingsFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Randlos hat das Fenster keine Windows-Leiste: die Fehlerseite bringt eine eigene mit (Ziehen, Schließen).
    private static string ErrorPage(string message) =>
        "<!doctype html><meta charset=\"utf-8\"><body style=\"font-family:'Segoe UI',sans-serif;margin:0;background:#060916;color:#e6e9f5\">" +
        "<div id=\"bar\" style=\"display:flex;align-items:center;height:40px;padding-left:14px;background:#0e1430;border-bottom:1px solid #1d2547;cursor:grab;user-select:none\">" +
        "<b style=\"font-family:Consolas,monospace;letter-spacing:.14em;font-size:12px\">STYKKER <span style=\"color:#38e8ff\">MESHSHARE</span></b>" +
        "<span style=\"flex:1\"></span><button onclick=\"window.external.sendMessage('window:close')\" style=\"width:44px;height:40px;border:0;background:none;color:inherit;cursor:pointer\">✕</button></div>" +
        "<div style=\"margin:32px\"><h2>StykkerMeshShare could not start its server</h2><p>" + WebUtility.HtmlEncode(message) + "</p>" +
        "<p>Build the solution first, or start with <code>--server &lt;path&gt;</code>.</p></div>" +
        "<script>var d=false,x=0,y=0,b=document.getElementById('bar');b.onmousedown=function(e){if(e.target.closest('button'))return;d=true;x=e.screenX;y=e.screenY;};" +
        "document.onmousemove=function(e){if(!d)return;var dx=e.screenX-x,dy=e.screenY-y;x=e.screenX;y=e.screenY;if(dx||dy)window.external.sendMessage('window:move '+dx+','+dy);};document.onmouseup=function(){d=false;};</script></body>";

    private static string CacheDir()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StykkerMeshShare");
        Directory.CreateDirectory(root);
        return root;
    }

    // Läuft schon ein Server auf dem Port, wird er benutzt; sonst gestartet und bis zu 30 s abgewartet.
    // Der gestartete Server läuft, solange das Fenster (oder sein Tray-Symbol) läuft; „Quit“ beendet ihn.
    private static void EnsureServer(int port, string? serverPath)
    {
        if (WaitForPort(port, TimeSpan.FromMilliseconds(600)).GetAwaiter().GetResult()) return;

        var exe = FindServer(serverPath)
            ?? throw new InvalidOperationException("StykkerMeshShare-Server.exe not found – build the solution first, or pass --server <path>.");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
        };
        // Ohne eigenes Tray-Symbol: das Symbol gehört dem Fenster (ein Symbol statt zwei).
        foreach (var arg in new[] { "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--no-browser", "--no-tray" })
            start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("the server did not start");
        if (!WaitForPort(port, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult())
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException($"the server did not answer on port {port}");
        }
    }

    private static async Task<bool> WaitForPort(int port, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(700) };
        var end = DateTime.Now + timeout;
        while (true)
        {
            try
            {
                // Nur unser Server zählt: ein anderes Programm auf dem Port bekäme sonst das Fenster.
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/api/ping").ConfigureAwait(false);
                if (response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Contains("\"app\":\"StykkerMeshShare\"", StringComparison.Ordinal)) return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) { }
            if (DateTime.Now >= end) return false;
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    // Der Server liegt veröffentlicht neben dem Fenster; beim Entwickeln im eigenen bin-Ordner nebenan.
    private static string? FindServer(string? explicitPath)
    {
        const string name = "StykkerMeshShare-Server.exe";
        if (!string.IsNullOrEmpty(explicitPath)) return File.Exists(explicitPath) ? explicitPath : null;

        var here = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(here)) return here;

        // Beim Entwickeln: nur innerhalb des Repos suchen (bis zum Ordner mit der .slnx), nie hinauf bis C:\.
        int level = 0;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && level < 8; dir = dir.Parent, level++)
        {
            var project = Path.Combine(dir.FullName, "StykkerMeshShare.Server");
            if (Directory.Exists(project))
            {
                var found = Directory.GetFiles(project, name, SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (found != null) return found;
            }
            if (File.Exists(Path.Combine(dir.FullName, "StykkerMeshShare.slnx"))) break;   // Wurzel des Werkzeugs erreicht
        }
        return null;
    }
}

internal sealed record Options(int Port, string? Server, bool Gpu)
{
    // StykkerMeshShare [--port 17404] [--server <Pfad>] [--gpu]
    public static Options Parse(string[] args)
    {
        int port = StykkerPorts.MeshShare;
        string? server = null;
        bool gpu = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): port = p; i++; break;
                case "--server" when i + 1 < args.Length: server = args[++i]; break;
                case "--gpu": gpu = true; break;
            }
        }
        return new Options(port, server, gpu);
    }
}
