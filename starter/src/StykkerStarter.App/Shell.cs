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

    private readonly DataLayout _layout;
    private readonly StarterLog _log;
    private readonly bool _gpu;
    private readonly string _mutexName;
    private readonly BlockingCollection<Action> _requests = new();
    private readonly object _gate = new();
    private readonly Dictionary<int, Action> _menu = new();   // nur auf dem Tray-Thread benutzt
    private PhotinoWindow? _window;
    private bool _ready;      // die Seite hat „shell.ready“ gemeldet: erst dann darf SendWebMessage kommen
    private bool _quit;
    private int _nextMenuId = MenuFirstDynamic;
    private Model? _model;
    private TrayIcon? _tray;
    private EventWaitHandle? _openEvent;
    private readonly bool _defaultData;
    private readonly int? _checkEvery;

    public Shell(DataLayout layout, Program.Options opt, string mutexName)
    {
        _layout = layout;
        _log = StarterLog.In(layout);
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
        if (openWindow) Post(OpenWindow);

        while (!_quit)
            _requests.Take()();
        _log.Write("end");
        return 0;
    }

    public void Dispose()
    {
        _model?.Dispose();
        _tray?.Dispose();
        _openEvent?.Dispose();
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
                .SetSize(new Size(1080, 780))
                .Center()
                .SetTemporaryFilesPath(Path.Combine(_layout.Root, "web-shell"));
            _window.RegisterWebMessageReceivedHandler(OnMessage);
            // Photino verlangt eine Startseite vor WaitForClose; die Seite ist eingebettet (siehe WebContent)
            _window.LoadRawString(WebContent.Page(_gpu));
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
                case "quit": Quit(); break;
                case "openFolder":
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _layout.Root + "\"") { UseShellExecute = false });
                    break;
                default:
                    _model?.Command(root.Clone());
                    break;
            }
        }
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

    private void Quit()
    {
        _quit = true;
        lock (_gate) _window?.Close();   // beendet das offene Fenster; WaitForClose kehrt zurück
        Post(() => { });                 // weckt die Schleife, falls gerade kein Fenster offen ist
        _log.Write("quit");
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
        foreach (var p in programs)
        {
            if (p.Broken != null)
            {
                items.Add(new TrayIcon.Item(0, $"{(string.IsNullOrWhiteSpace(p.Name) ? "(no name)" : p.Name)} · broken entry", Disabled: true));
                continue;
            }
            var state = p.Running ? "running" : "stopped";
            var where = p.CopyToLocal ? p.Local ?? "–" : "runs from source";
            items.Add(new TrayIcon.Item(0, $"{p.Name} {where} · {state}", Disabled: true));
        }
        foreach (var p in programs.Where(p => p.UpdateVersion != null))
        {
            var id = _nextMenuId++;
            var programId = p.Id;
            _menu[id] = () => _model?.RequestUpdate(programId);
            items.Add(new TrayIcon.Item(id, $"Update {p.Name} to {p.UpdateVersion}"));
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
        _menu[quit] = () => Post(Quit);
        items.Add(new TrayIcon.Item(quit, "Quit the Starter"));
        return items;
    }

    private void OnTrayMenu(int id)
    {
        if (_menu.TryGetValue(id, out var action)) action();
    }
}
