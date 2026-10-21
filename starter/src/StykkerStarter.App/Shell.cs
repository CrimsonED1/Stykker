using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using Photino.NET;
using Stykker.Shared.Windows;
using StykkerStarter.Core;
using StykkerStarter.Core.Web;

namespace StykkerStarter.App;

// Das Fenster, das Tray-Symbol und die Brücke zur Seite. Das Fenster läuft auf dem Hauptthread (STA), der in
// WaitForClose wartet, solange es offen ist. Klicks aus dem Tray und Meldungen aus dem Hintergrund kommen auf ihren
// eigenen Threads und werden in eine Warteschlange gelegt, die der Hauptthread abarbeitet.
internal sealed class Shell : IDisposable
{
    private const int MenuFirstDynamic = 100;
    private const string IconFile = "StykkerStarter.ico";

    private readonly DataLayout _layout;
    private readonly StarterLog _log;
    private readonly bool _gpu;
    private readonly string _mutexName;
    private readonly BlockingCollection<Action> _requests = new();
    private readonly object _gate = new();
    private readonly Dictionary<int, Action> _menu = new();   // nur auf dem Tray-Thread benutzt
    private PhotinoWindow? _window;
    private int _windowThread;   // der Thread, in dem das Fenster läuft (der Hauptthread)
    private bool _ready;      // die Seite hat „shell.ready“ gemeldet: erst dann darf SendWebMessage kommen
    private volatile bool _quit;
    private bool _allowClose;           // das Fenster schließt ohne Frage (gewählt: in den Tray); nur Fensterthread
    private volatile string? _pendingAsk;   // Frage, die die Seite zeigen soll, sobald sie steht ("quit")
    private bool _disposed;
    private int _nextMenuId = MenuFirstDynamic;
    private Model? _model;
    private TrayIcon? _tray;
    private EventWaitHandle? _openEvent;
    private readonly bool _defaultData;
    private readonly int? _checkEvery;

    public Shell(DataLayout layout, Program.Options opt, string mutexName)
    {
        _layout = layout;
        _log = StarterLog.Open(AppContext.BaseDirectory, layout, Environment.MachineName);
        _gpu = opt.Gpu;
        _mutexName = mutexName;
        _checkEvery = opt.CheckEverySeconds;
        _defaultData = opt.DataDir == null;
    }

    public int Run(bool openWindow)
    {
        _log.Write($"start: window {(openWindow ? "open" : "tray only")}");
        _model = new Model(_layout, _log, _defaultData, _checkEvery, Environment.ProcessPath ?? "", Tell, RefreshTray, Balloon);

        _tray = new TrayIcon(BuildTrayItems, OnTrayMenu, () => Post(OpenWindow));
        if (!_tray.TryShow("Stykker Starter", out var error)) _log.Write($"tray: icon not shown: {error}");
        else _log.Write("tray: icon shown");

        _openEvent = SingleInstance.Listen(_mutexName, () => Post(OpenWindow));
        _model.Start();
        // Erst die Oberfläche, dann die Programme (siehe Model.BeginStartupAfter)
        if (openWindow)
        {
            _model.BeginStartupFallback(TimeSpan.FromSeconds(20));
            Post(OpenWindow);
        }
        else _model.BeginStartup();

        while (!_quit)
            _requests.Take()();
        return 0;
    }

    // Das Beenden in fester Reihenfolge; RequestQuit hat schon die Engine und den Timer gestoppt und das Fenster
    // geschlossen. Jeder Schritt steht im Protokoll. Ein Fehler in einem Schritt bricht das Beenden nicht ab.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown.Run(_log, new (string, Action)[]
        {
            ("tray icon removed", () => _tray?.Dispose()),
            ("queue stopped", () => _model?.Dispose()),
            ("open listener stopped", () => _openEvent?.Dispose()),
            ("process ends", () => { }),
        });
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
            _allowClose = false;
            _window = new PhotinoWindow()
                .SetTitle("Stykker Starter")
                .SetUseOsDefaultSize(false)
                .SetUseOsDefaultLocation(false)
                .SetSize(new Size(1080, 780))
                .Center()
                .SetChromeless(true)
                .SetTemporaryFilesPath(Path.Combine(_layout.Root, "web-shell"));
            // Das Symbol des Fensters (Taskleiste); die Datei liegt neben der Exe
            var icon = Path.Combine(AppContext.BaseDirectory, IconFile);
            if (File.Exists(icon)) _window.SetIconFile(icon);
            _window.RegisterWebMessageReceivedHandler(OnMessage);
            _window.RegisterWindowClosingHandler(OnClosing);
            // Fenster-Chrome: Zustandsänderungen (Maximize/Restore) melden an die Seite
            _window.RegisterMaximizedHandler((s, e) => SendWindowState(true));
            _window.RegisterRestoredHandler((s, e) => SendWindowState(false));
            // Photino verlangt eine Startseite vor WaitForClose; die Seite ist eingebettet (siehe WebContent)
            _window.LoadRawString(WebContent.Page(_gpu));
            _windowThread = Environment.CurrentManagedThreadId;
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

    // Nachricht von der Seite. Läuft auf dem Hauptthread, also im Fenster selbst.
    private void OnMessage(object? sender, string message)
    {
        if (message == "shell.ready")
        {
            lock (_gate) _ready = true;
            _log.Write("window: page ready");
            var model = _model;
            model?.Enqueue(model.Publish);
            model?.BeginStartup();
            SendWindowState();
            if (_pendingAsk is { } ask)
            {
                _pendingAsk = null;
                ReplyFromWindow(AskJson(ask));
            }
            return;
        }
        // Fenster-Chrome: einfache Zeichenketten (nicht JSON)
        if (message == "window:minimize") { _window?.SetMinimized(true); return; }
        if (message == "window:maximize") { _window?.SetMaximized(true); return; }
        if (message == "window:restore") { _window?.SetMaximized(false); return; }
        if (message == "window:close") { _window?.Close(); return; }
        if (message.StartsWith("window:move "))
        {
            var parts = message.Substring("window:move ".Length).Split(',');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out var dx) &&
                int.TryParse(parts[1], out var dy))
            {
                try
                {
                    if (_window?.Maximized != true)
                        _window?.Offset(new Point(dx, dy));
                }
                catch { }
            }
            return;
        }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(message); }
        catch (JsonException) { return; }
        using (doc)
        {
            var root = doc.RootElement;
            var cmd = root.TryGetProperty("cmd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            switch (cmd)
            {
                case "pick": Pick(); break;
                case "close": _window?.Close(); break;
                case "quit": AskQuit(); break;
                case "closeChoice":
                {
                    var choice = root.TryGetProperty("choice", out var ch) ? ch.GetString() : null;
                    var remember = root.TryGetProperty("remember", out var r) && r.ValueKind == JsonValueKind.True;
                    _log.Write($"close: choice {choice}{(remember ? ", remembered" : "")}");
                    if (choice is not ("tray" or "quit")) break;
                    if (remember) _model?.RememberCloseAction(choice);
                    if (choice == "tray") CloseToTray();
                    else AskQuit();
                    break;
                }
                case "quitWith":
                {
                    var stop = root.TryGetProperty("stop", out var s) && s.ValueKind == JsonValueKind.True;
                    _log.Write($"quit: stop the programs {(stop ? "yes" : "no")}");
                    if (stop && _model != null) _model.StopAllThen(RequestQuit);
                    else RequestQuit();
                    break;
                }
                case "openFolder":
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _layout.Root + "\"") { UseShellExecute = false });
                    break;
                case "openProgramFolder":
                    OpenProgramFolder(root.TryGetProperty("id", out var fid) && fid.ValueKind == JsonValueKind.String ? fid.GetString() : null);
                    break;
                case "showLog":
                    OpenLog();
                    break;
                default:
                    _model?.Command(root.Clone());
                    break;
            }
        }
    }

    // Das X (eigene Titelleiste, Alt+F4, Taskleiste). true = offen lassen (Photino.NET: true heißt „nicht schließen“).
    // Je nach Einstellung: fragen, in den Tray (das Fenster schließt, der Starter läuft weiter) oder beenden.
    private bool OnClosing(object? sender, EventArgs e)
    {
        if (_quit || _allowClose) return false;
        bool ready;
        lock (_gate) ready = _ready;
        if (!ready) return false;               // keine Seite, die fragen könnte: wie bisher in den Tray
        switch (_model?.CloseAction ?? "ask")
        {
            case "tray": return false;
            case "quit": AskQuit(); return true;
        }
        ReplyFromWindow(AskJson("close"));
        return true;
    }

    private void CloseToTray()
    {
        _allowClose = true;
        _window?.Close();
    }

    // Beenden: laufen Programme, fragt die Seite, ob sie mit gestoppt werden; sonst endet der Starter gleich.
    // Von jedem Thread (Tray, Seite); ist das Fenster zu, geht es dafür auf.
    private void AskQuit()
    {
        var running = _model?.Programs.Any(p => p.Running) == true;
        if (!running) { RequestQuit(); return; }
        bool ready;
        lock (_gate) ready = _ready && _window != null;
        if (ready) { Tell(AskJson("quit")); return; }
        _pendingAsk = "quit";
        Post(OpenWindow);
    }

    private static string AskJson(string what) => JsonSerializer.Serialize(new { type = "ask", what });

    // Der Ordner der laufenden Kopie im Explorer; ohne Kopie der Quellordner. Fehlt der Ordner, steht es im Protokoll.
    private void OpenProgramFolder(string? id)
    {
        var program = _model?.Programs.FirstOrDefault(p => p.Id == id);
        var folder = program?.Folder ?? (program?.Source is { Length: > 0 } source ? Path.GetDirectoryName(source) : null);
        if (folder == null || !Directory.Exists(folder))
        {
            _log.Write($"open folder: no folder for {id ?? "(none)"}");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = false });
    }

    // Die Protokolldatei des Starters, in der Standard-App für Textdateien
    private void OpenLog()
    {
        var file = _log.FilePath;
        if (!File.Exists(file))
        {
            _log.Write("show log: the log file does not exist yet");
            return;
        }
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    // Der Dateidialog gehört zum Fenster; das Ergebnis geht zurück an die Seite
    private void Pick()
    {
        var files = _window?.ShowOpenFile("Choose the program", null, false, new[] { ("Programs (*.exe)", new[] { "*.exe" }) });
        if (files is { Length: > 0 })
            ReplyFromWindow(JsonSerializer.Serialize(new { type = "picked", path = files[0] }));
    }

    // Aus dem Hauptthread: direkt an die Seite
    private void ReplyFromWindow(string json)
    {
        lock (_gate)
        {
            if (!_ready || _window == null) return;
            try { _window.SendWebMessage(json); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        }
    }

    // Fenster-Chrome: Maximisierungs-Zustand an die Seite melden (Button □/⬜).
    private void SendWindowState()
    {
        bool maximized = false;
        try { maximized = _window?.Maximized == true; }
        catch { }
        SendWindowState(maximized);
    }

    private void SendWindowState(bool maximized)
    {
        ReplyFromWindow("window:state " + (maximized ? "maximized" : "restored"));
    }

    // Aus Hintergrundthreads: auf den Fensterthread umleiten. Nicht unter dem Schloss, weil das Fenster darauf wartet.
    private void Tell(string json)
    {
        PhotinoWindow? window;
        lock (_gate)
        {
            if (!_ready) return;
            window = _window;
        }
        if (window == null) return;
        // Aus dem Fensterthread (ein Befehl der Seite) direkt senden: ein Invoke auf dem eigenen Thread käme nicht dran
        if (Environment.CurrentManagedThreadId == _windowThread)
        {
            try { window.SendWebMessage(json); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
            return;
        }
        try
        {
            window.Invoke(() =>
            {
                try { window.SendWebMessage(json); }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    // Beenden, von jedem Thread (Tray, Seite). Erst die Engine und der Timer, dann das Fenster schließen; danach kehrt
    // Run zurück und Dispose räumt auf. Das Fenster schließt diese Funktion selbst: der Hauptthread wartet im Fenster,
    // und eine Nachricht in der Warteschlange käme erst nach dem Schließen dran (dann liefe das Beenden nie).
    private void RequestQuit()
    {
        if (_quit) return;
        _quit = true;
        _log.Write("quit: requested");
        _model?.StopEngine();
        _log.Write("quit: engine and timer stopped");

        PhotinoWindow? window;
        int windowThread;
        lock (_gate)
        {
            window = _window;
            windowThread = _windowThread;
        }
        if (window != null)
        {
            if (Environment.CurrentManagedThreadId == windowThread) window.Close();
            else
            {
                try { window.Invoke(window.Close); }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
            }
        }
        Post(() => { });   // weckt die Schleife, falls gerade kein Fenster offen ist
    }

    // ---------- Tray ----------

    private void RefreshTray()
    {
        var programs = _model?.Programs ?? Array.Empty<ProgramView>();
        int running = programs.Count(p => p.Running);
        _tray?.SetTip($"Stykker Starter · {running} running");
    }

    private void Balloon(string title, string text) => _tray?.ShowBalloon(title, text);

    // Das Menü wird bei jedem Rechtsklick neu gebaut: Statuszeilen und „Update“-Einträge spiegeln den Stand
    private IReadOnlyList<TrayIcon.Item> BuildTrayItems()
    {
        _menu.Clear();
        _nextMenuId = MenuFirstDynamic;
        var items = new List<TrayIcon.Item> { new(0, "Stykker Starter", Disabled: true) };
        var programs = _model?.Programs ?? Array.Empty<ProgramView>();
        if (programs.Count == 0) items.Add(new TrayIcon.Item(0, "No programs in the list", Disabled: true));
        // Je Programm ein Untermenü: nur die Befehle, die gerade passen
        foreach (var p in programs)
        {
            if (p.Broken != null)
            {
                items.Add(new TrayIcon.Item(0, $"{(string.IsNullOrWhiteSpace(p.Name) ? "(no name)" : p.Name)} · broken entry", Disabled: true));
                continue;
            }
            var programId = p.Id;
            var children = new List<TrayIcon.Item>();
            if (p.Running)
            {
                children.Add(Command("Restart", () => _model?.RequestRestart(programId)));
                children.Add(Command("Stop", () => _model?.RequestStop(programId)));
            }
            else children.Add(Command("Start", () => _model?.RequestStart(programId)));
            if (p.UpdateVersion != null)
            {
                children.Add(Command(p.UpdateIsNewBuild ? "Update (new build)" : $"Update to {p.UpdateVersion}", () => _model?.RequestUpdate(programId)));
            }
            var state = p.Busy ?? (p.Running ? "running" : "stopped");
            items.Add(new TrayIcon.Item(0, $"{p.Name} · {state}", Children: children));
        }
        items.Add(new TrayIcon.Item(0, "", Separator: true));
        var check = _nextMenuId++;
        _menu[check] = () => _model?.RequestCheck();
        items.Add(new TrayIcon.Item(check, "Check for updates now"));
        items.Add(new TrayIcon.Item(0, "", Separator: true));
        var open = _nextMenuId++;
        _menu[open] = () => Post(OpenWindow);
        items.Add(new TrayIcon.Item(open, "Open the Starter"));
        var quit = _nextMenuId++;
        _menu[quit] = AskQuit;
        items.Add(new TrayIcon.Item(quit, "Quit the Starter"));
        return items;
    }

    // Ein Befehl im Tray-Menü: die Nummer zeigt auf die Aktion
    private TrayIcon.Item Command(string text, Action action)
    {
        var id = _nextMenuId++;
        _menu[id] = action;
        return new TrayIcon.Item(id, text);
    }

    private void OnTrayMenu(int id)
    {
        if (_menu.TryGetValue(id, out var action)) action();
    }
}
