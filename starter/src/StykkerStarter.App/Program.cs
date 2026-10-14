using System.Collections.Concurrent;
using System.Drawing;
using Photino.NET;
using Stykker.Shared.Windows;
using StykkerStarter.Core;

namespace StykkerStarter.App;

// StykkerStarter.exe: läuft im Tray weiter; das Fenster entsteht beim Öffnen und endet beim Schließen ganz
// (nicht versteckt). Die Seite ist eingebettet und kommt über ein eigenes Schema (starter://), nicht über einen
// Server. Ohne Grafikkarte zeichnet das Fenster wie StykkerUI; --gpu schaltet das ab.
//   StykkerStarter.exe [--data-dir <Ordner>] [--tray] [--gpu]
//     --tray      nur das Tray-Symbol, kein Fenster (für den Autostart)
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var opt = Options.Parse(args);
        if (!opt.Gpu && OperatingSystem.IsWindows())
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-gpu --disable-gpu-compositing");

        var layout = new DataLayout(opt.DataDir ?? DefaultDataDir());
        Directory.CreateDirectory(layout.Root);
        var shell = new Shell(layout, opt.Gpu);
        return shell.Run(openWindow: !opt.TrayOnly);
    }

    // %LOCALAPPDATA%\Stykker\Starter – nur ohne --data-dir, und nur hier (Tests nutzen einen Temp-Ordner)
    private static string DefaultDataDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stykker", "Starter");

    internal sealed record Options(string? DataDir, bool TrayOnly, bool Gpu)
    {
        public static Options Parse(string[] args)
        {
            string? dataDir = null;
            bool trayOnly = false, gpu = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--data-dir" when i + 1 < args.Length: dataDir = args[++i]; break;
                    case "--tray": trayOnly = true; break;
                    case "--gpu": gpu = true; break;
                }
            }
            return new Options(dataDir, trayOnly, gpu);
        }
    }
}

// Das Fenster und das Tray-Symbol. Alles, was ein Fenster braucht, läuft auf dem Hauptthread (STA): der wartet
// in WaitForClose, solange das Fenster offen ist. Klicks aus dem Tray kommen auf ihrem eigenen Thread und werden in
// eine Warteschlange gelegt, die der Hauptthread abarbeitet.
internal sealed class Shell
{
    private const int MenuOpen = 1, MenuQuit = 2;

    private readonly DataLayout _layout;
    private readonly StarterLog _log;
    private readonly bool _gpu;
    private readonly BlockingCollection<Action> _requests = new();
    private readonly object _gate = new();
    private PhotinoWindow? _window;
    private bool _ready;      // die Seite hat „shell.ready“ gemeldet: erst dann darf SendWebMessage kommen
    private bool _quit;

    public Shell(DataLayout layout, bool gpu)
    {
        _layout = layout;
        _log = StarterLog.In(layout);
        _gpu = gpu;
    }

    public int Run(bool openWindow)
    {
        IReadOnlyList<TrayIcon.Item> items =
        [
            new TrayIcon.Item(MenuOpen, "Open the Starter"),
            new TrayIcon.Item(0, "", Separator: true),
            new TrayIcon.Item(MenuQuit, "Quit the Starter"),
        ];
        using var tray = new TrayIcon(items, id => { if (id == MenuOpen) Post(OpenWindow); else if (id == MenuQuit) Post(Quit); },
            () => Post(OpenWindow));
        if (!tray.TryShow("Stykker Starter", out var error))
            _log.Write($"tray: icon not shown: {error}");
        else
            _log.Write("tray: icon shown");

        if (openWindow) Post(OpenWindow);
        while (!_quit)
            _requests.Take()();
        return 0;
    }

    private void Post(Action action)
    {
        try { _requests.Add(action); }
        catch (InvalidOperationException) { }   // schon beendet
    }

    // Läuft auf dem Hauptthread und kehrt erst zurück, wenn das Fenster geschlossen ist.
    private void OpenWindow()
    {
        if (_quit) return;
        lock (_gate)
        {
            if (_window != null) return;   // schon offen
            _ready = false;
            _window = new PhotinoWindow()
                .SetTitle("Stykker Starter")
                .SetUseOsDefaultSize(false)
                .SetSize(new Size(1080, 760))
                .Center()
                .SetTemporaryFilesPath(Path.Combine(_layout.Root, "web-shell"));
            _window.RegisterWebMessageReceivedHandler(OnMessage);
            _window.LoadRawString(WebContent.Page(_gpu));   // Photino verlangt eine Startseite vor WaitForClose
        }
        _log.Write("window: open");
        _window.WaitForClose();
        lock (_gate)
        {
            _window = null;
            _ready = false;
        }
        _log.Write("window: closed");
    }

    // Nachricht an die Seite. Vor „shell.ready“ passiert nichts: SendWebMessage davor bringt Photino zum Absturz
    // (llm/AGENTS.md).
    private void Tell(string json)
    {
        lock (_gate)
        {
            if (!_ready || _window == null) return;
            try { _window.SendWebMessage(json); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { _log.Write($"window: message not sent: {ex.GetType().Name}"); }
        }
    }

    private void OnMessage(object? sender, string message)
    {
        if (message == "shell.ready")
        {
            lock (_gate) _ready = true;
            _log.Write("window: page ready");
        }
    }

    private void Quit()
    {
        _quit = true;
        lock (_gate) _window?.Close();   // beendet das offene Fenster; WaitForClose kehrt zurück
        Post(() => { });                 // weckt die Schleife, falls gerade kein Fenster offen ist
        _log.Write("quit");
    }
}
