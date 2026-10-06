using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Photino.NET;
using StykkerLlm.Core;
using StykkerLlm.Platform.Windows;

namespace StykkerLlm.UI;

// „Beim Schließen fragen" – nur unter Windows: dort gibt es ein Tray-Symbol ohne WinForms (Platform.Windows/TrayIcon,
// dieselbe Umsetzung, die der Server benutzt). Der Klick aufs X wird abgefangen und gefragt: als Dialog über der
// aktuellen Seite (ui.js zeichnet ihn, die Seite bleibt dahinter stehen). Auf Wunsch legt sich das Fenster in den Tray:
// ShowWindow(SW_HIDE) versteckt es wirklich, also auch aus der Taskleiste; das Symbol holt es auf dieselbe Seite zurück.
// Die Antwort kann gemerkt werden (ShellChoice); der Tray-Eintrag „Ask when closing“ holt die Frage zurück.
[SupportedOSPlatform("windows")]
internal sealed class CloseToTray
{
    private const int SwHide = 0, SwShow = 9;
    private readonly PhotinoWindow _window;
    private readonly AppPaths _paths;
    private readonly IPlatform _platform;
    private readonly int _port;
    private readonly string _adoptPage;     // die Startseite der Hülle: setzt das Gerätecookie und lädt die Oberfläche
    private readonly bool _startedServer;   // nur dann beendet sich der Server mit dem Fenster
    private readonly ThemeInfo _theme;
    private TrayIcon? _tray;
    private Timer? _fallback;
    // Nur auf dem Fenster-Thread: Rückfrage und Schließen kommen von dort, die Tray-Klicks werden dorthin geholt
    private bool _quit, _hidden, _acked, _fallbackShown;

    public CloseToTray(PhotinoWindow window, AppPaths paths, IPlatform platform, int port, string adoptPage, bool startedServer)
    {
        _window = window;
        _paths = paths;
        _platform = platform;
        _port = port;
        _adoptPage = adoptPage;
        _startedServer = startedServer;
        _theme = ThemeCatalog.Find(AppSettings.Load(paths.SettingsFile).Theme);
    }

    // true = nicht schließen; false = wirklich schließen. Achtung, die Richtung ist die Gegenrichtung der naheliegenden
    // Lesart: Photino.NET setzt in OnWindowClosing bei true „noClose = 1“ (gegen Photino.NET 4.0.16 geprüft, 2026-10-06).
    public bool OnClosing(object? sender, EventArgs e)
    {
        if (_quit || _hidden) return false;
        if (!EnsureTray()) return false;          // kein Symbol möglich: dann wie bisher schließen
        var choice = ShellChoice.Load(_paths);
        Log($"close: choice {choice}");
        switch (choice)
        {
            case ShellChoice.Tray: ToTray(); return true;
            case ShellChoice.Quit: Leave(); return false;
        }
        Ask();
        return true;                              // offen lassen, die Frage steht jetzt im Fenster
    }

    // Die Frage an die Seite schicken. Meldet sie sich nicht (lädt gerade, ist keine Seite des Servers), kommt nach
    // kurzer Zeit die eigene Frage-Seite – dann ist die Oberfläche weg und wird bei „Abbrechen“ neu geladen.
    private void Ask()
    {
        _acked = false;
        _window.SendWebMessage(Html.AskMessage());
        _fallback?.Dispose();
        _fallback = new Timer(_ => _window.Invoke(() =>
        {
            if (_acked || _hidden || _quit) return;
            Log("close: page did not answer, own question page");
            _fallbackShown = true;
            _window.LoadRawString(Html.AskClose(_theme));
        }), null, 1500, Timeout.Infinite);
    }

    // Nachrichten der Seite (window.external.sendMessage); ein „!“ am Ende heißt „nicht mehr fragen“
    public void OnMessage(object? sender, string message)
    {
        Log($"message: {message}");
        var remember = message.EndsWith('!');
        var what = remember ? message[..^1] : message;
        switch (what)
        {
            case Html.MsgAck: _window.Invoke(() => _acked = true); break;
            case Html.MsgTray:
                if (remember) ShellChoice.Save(_paths, ShellChoice.Tray);
                _window.Invoke(ToTray);
                break;
            case Html.MsgQuit:
                if (remember) ShellChoice.Save(_paths, ShellChoice.Quit);
                _window.Invoke(Quit);
                break;
            case Html.MsgCancel: _window.Invoke(Restore); break;
        }
    }

    // Das Symbol anlegen und zeigen, bevor die Frage kommt: geht es hier nicht, schließt das Fenster wie bisher –
    // statt sichtbar in einer Frage zu hängen, die sich gar nicht mehr erfüllen lässt (Tray geht nicht überall).
    private bool EnsureTray()
    {
        if (_tray != null) return true;
        var tray = new TrayIcon(
            new[]
            {
                new TrayIcon.Item(1, Strings.TrayShow), new TrayIcon.Item(3, Strings.TrayAskOnClose),
                new TrayIcon.Item(0, "", Separator: true), new TrayIcon.Item(2, Strings.TrayExit),
            },
            id =>
            {
                if (id == 1) _window.Invoke(Show);
                else if (id == 3) ShellChoice.Save(_paths, ShellChoice.Ask);
                else if (id == 2) _window.Invoke(Quit);
            },
            () => _window.Invoke(Show));
        if (!tray.TryShow(Strings.AppName, out var error))
        {
            Log($"tray: {error}");
            tray.Dispose();
            return false;
        }
        _tray = tray;
        return true;
    }

    private void ToTray()
    {
        _hidden = true;
        ShowWindow(_window.WindowHandle, SwHide);
    }

    // Stand die eigene Frage-Seite im Fenster, ist die Oberfläche weg: dann über die Startseite zurück. Sonst steht
    // die Seite noch, wie sie war – nichts neu laden, man landet genau dort, wo man war.
    private void Restore()
    {
        if (!_fallbackShown) return;
        _fallbackShown = false;
        _window.LoadRawString(_adoptPage);
    }

    private void Show()
    {
        if (!_hidden) return;
        _hidden = false;
        Restore();
        ShowWindow(_window.WindowHandle, SwShow);
        SetForegroundWindow(_window.WindowHandle);
    }

    // Ein zweiter Start von StykkerUI: aus dem Tray holen, sonst nach vorn
    public void Activate()
    {
        Log("activate: second start");
        if (_hidden) { Show(); return; }
        ShowWindow(_window.WindowHandle, SwShow);
        SetForegroundWindow(_window.WindowHandle);
    }

    private void Quit()
    {
        if (_quit) return;
        Leave();
        _window.Close();
    }

    // Aufräumen vor dem Schließen: Symbol weg, den eigenen Server beenden
    private void Leave()
    {
        _quit = true;
        _fallback?.Dispose();
        _tray?.Dispose();
        _tray = null;
        StopServer();
    }

    // Den Server beenden, den diese Hülle gestartet hat – sonst läuft er ohne Fenster und ohne Symbol weiter.
    // Lief er schon vorher (Fenster, „stykker web“), bleibt er: er gehört dann nicht uns.
    private void StopServer()
    {
        if (!_startedServer) return;
        var key = ServerClient.ReadKey(_paths, _platform);
        if (key == null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var client = new ServerClient(ServerClient.DefaultUrl(_port), key);
            client.SendAsync("shutdown", ct: cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            Debug.WriteLine($"[quit] server not stopped: {ex.Message}");
        }
    }

    private void Log(string line) => ShellLog.Write(_paths, line);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}

// Die gemerkte Antwort auf „Beim Schließen?“ – eine Datei im Datenordner, nur für dieses Fenster (nicht in den
// Einstellungen, die der Server selbst schreibt). Der Tray-Eintrag „Ask when closing“ setzt sie zurück.
internal static class ShellChoice
{
    public const string Ask = "ask", Tray = "tray", Quit = "quit";
    private const string FileName = "web-shell-close.txt";

    public static string Load(AppPaths paths)
    {
        try
        {
            var file = Path.Combine(paths.Root, FileName);
            return File.Exists(file) ? File.ReadAllText(file).Trim() : Ask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Ask; }
    }

    public static void Save(AppPaths paths, string choice)
    {
        try { AtomicFile.WriteAllText(Path.Combine(paths.Root, FileName), choice); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine($"[close] {ex.Message}"); }
    }
}

// Kurzes Protokoll der Hülle (logs/web-shell.log): Start, Schließen, Tray, Nachrichten der Seite
internal static class ShellLog
{
    public static void Write(AppPaths paths, string line)
    {
        try
        {
            Directory.CreateDirectory(paths.LogsDir);
            File.AppendAllText(Path.Combine(paths.LogsDir, "web-shell.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(line); }
    }
}
