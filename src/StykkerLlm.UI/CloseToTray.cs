using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Photino.NET;
using StykkerLlm.Core;
using StykkerLlm.Platform.Windows;

namespace StykkerLlm.UI;

// „Beim Schließen fragen" – nur unter Windows: dort gibt es ein Tray-Symbol ohne WinForms (Platform.Windows/TrayIcon,
// dieselbe Umsetzung, die der Server benutzt). Der Klick aufs X wird abgefangen, gefragt (eine Seite im Fenster, in
// den Farben des Themas), und auf Wunsch legt sich das Fenster in den Tray: ShowWindow(SW_HIDE) versteckt es wirklich,
// also auch aus der Taskleiste; das Symbol holt es zurück. Die Knöpfe der Rückfrage schicken dazu eine Nachricht an
// diesen Prozess (window.external.sendMessage – die Brücke, die die Fenster-Hülle in jede Seite einbaut).
[SupportedOSPlatform("windows")]
internal sealed class CloseToTray
{
    private const int SwHide = 0, SwShow = 9;
    private readonly PhotinoWindow _window;
    private readonly AppPaths _paths;
    private readonly IPlatform _platform;
    private readonly int _port;
    private readonly string _adoptPage;     // die Startseite der Hülle: holt das Fenster aus dem Tray zurück
    private readonly bool _startedServer;   // nur dann beendet sich der Server mit dem Fenster
    private readonly ThemeInfo _theme;
    private TrayIcon? _tray;
    // Nur auf dem Fenster-Thread: Rückfrage und Schließen kommen von dort, die Tray-Klicks werden dorthin geholt
    private bool _quit, _hidden;

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

    // true = wirklich schließen; false = das Fenster bleibt, die Rückfrage steht jetzt darin
    public bool OnClosing(object? sender, EventArgs e)
    {
        if (_quit || _hidden) return true;
        if (!EnsureTray()) return true;      // kein Symbol möglich: dann wie bisher schließen
        _window.LoadRawString(Html.AskClose(_theme));
        return false;
    }

    // Nachrichten der Rückfrage-Seite (window.external.sendMessage)
    public void OnMessage(object? sender, string message)
    {
        switch (message)
        {
            case Html.MsgTray: _window.Invoke(ToTray); break;
            case Html.MsgQuit: _window.Invoke(Quit); break;
        }
    }

    // Das Symbol anlegen und zeigen, bevor die Frage kommt: geht es hier nicht, schließt das Fenster wie bisher –
    // statt sichtbar in einer Frage zu hängen, die sich gar nicht mehr erfüllen lässt (Tray geht nicht überall).
    private bool EnsureTray()
    {
        if (_tray != null) return true;
        var tray = new TrayIcon(
            new[] { new TrayIcon.Item(1, Strings.TrayShow), new TrayIcon.Item(2, Strings.TrayExit) },
            id => { if (id == 1) _window.Invoke(Show); else _window.Invoke(Quit); },
            () => _window.Invoke(Show));
        if (!tray.TryShow(Strings.AppName, out var error))
        {
            Debug.WriteLine($"[tray] {error}");
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

    // Zurück ins Bild: erst die Oberfläche holen (die Rückfrage war die letzte Seite), dann das Fenster zeigen.
    // Die Startseite setzt das Gerätecookie neu, so dass die Anmeldung auch nach langer Tray-Zeit noch steht.
    private void Show()
    {
        if (!_hidden) return;
        _hidden = false;
        _window.LoadRawString(_adoptPage);
        ShowWindow(_window.WindowHandle, SwShow);
        SetForegroundWindow(_window.WindowHandle);
    }

    private void Quit()
    {
        if (_quit) return;
        _quit = true;
        _tray?.Dispose();
        _tray = null;
        StopServer();
        _window.Close();
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

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}