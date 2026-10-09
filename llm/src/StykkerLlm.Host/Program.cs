using System.Diagnostics;
using StykkerLlm.Core;
using StykkerLlm.Platform.Windows;

namespace StykkerLlm.Host;

// StykkerHost: ein PC, der nur Modelle laufen lässt (docs/plan-hosts-gateway.md). Er misst GPU, System und die
// Modellserver dieses PCs mit derselben Engine wie der Server, zeigt den Stand im Tray und schreibt StykkerHost.log.
// Er öffnet keinen Port nach außen: er verbindet sich selbst mit dem Server, bei dem er gekoppelt ist (host.json).
internal static class Program
{
    private static int Main(string[] args)
    {
#if WINSERVICE
        // Windows-Dienst (P8): Einrichten per Befehl; Windows startet den Dienst mit --service
        if (args.Length > 0 && OperatingSystem.IsWindows() && ServiceSetup.IsCommand(args[0])) return ServiceSetup.RunCommand(args);
        bool service = args.Contains("--service");
#else
        const bool service = false;
#endif
        var paths = service ? HostServiceFiles.ServicePaths() : HostStatus.DefaultPaths();
#if DEBUG
        // Entwickler-Schalter (nur Debug): anderer Datenordner
        int i = Array.IndexOf(args, "--data-dir");
        if (i >= 0 && i + 1 < args.Length) paths = new AppPaths(Path.GetFullPath(args[i + 1]));
#endif
        Directory.CreateDirectory(paths.Root);
        AppLog.Init(service ? "StykkerHost-service" : "StykkerHost", paths);
        AppLog.CatchUnhandled();

        using var single = new SingleInstance(SingleInstance.NameFor(paths.Root));
        if (!single.IsFirst)
        {
            AppLog.Write(Strings.HostAlreadyRunning);
            return 0;
        }
#if WINSERVICE
        if (service && OperatingSystem.IsWindows())
        {
            ServiceSetup.RunService(paths);
            return 0;
        }
#endif
        using var app = new HostApp(paths);
        app.Run();
        return 0;
    }
}

internal sealed class HostApp : IDisposable
{
    private const int MenuPair = 20, MenuUnpair = 21, MenuAutostart = 10, MenuLog = 11, MenuQuit = 12;

    private readonly AppPaths _paths;
    private readonly bool _service;   // als Windows-Dienst: kein Tray, Kopplung über Anfragedateien (HostServiceFiles)
    private readonly IPlatform _platform;
    private readonly AppSettings _settings;
    private readonly MonitorEngine _engine;
    private readonly HostCommands _commands;
    private readonly ManualResetEventSlim _quit = new(false);
    private readonly object _linkGate = new();
    private HostConfig _config;
    private HostLinkClient _link;
    private CancellationTokenSource _linkStop = new();
    private Task _linkTask = Task.CompletedTask;
    private HostPairPage? _pairPage;
    private TrayIcon? _tray;

    public HostApp(AppPaths paths, bool service = false)
    {
        _paths = paths;
        _service = service;
        _platform = OperatingSystem.IsWindows() ? new WindowsPlatform() : new BasicPlatform();
        _settings = AppSettings.Load(paths.SettingsFile);
        _engine = new MonitorEngine(_platform, paths, _settings, readOnly: false);
        _commands = new HostCommands(_engine, HostSettings.Load(paths));
        _config = HostConfig.Load(paths, _platform);
        _link = NewLink(_config);
    }

    public void Run(CancellationToken stop = default)
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _quit.Set();
        using var stopping = stop.Register(() => _quit.Set());
        StartLink(_config);
        if (OperatingSystem.IsWindows() && !_service) _tray = Tray();

        var lastServers = new List<string>();
        bool first = true;
        var loop = Task.Run(async () =>
        {
            while (!_quit.IsSet)
            {
                try
                {
                    if (_service) await ServiceRequestAsync().ConfigureAwait(false);
                    await _engine.TickAsync().ConfigureAwait(false);
                    if (first) { first = false; AppLog.Write($"host: running, GPU {_engine.Gpu?.Name ?? Strings.HostNoGpu}"); }
                    if (OperatingSystem.IsWindows()) _tray?.SetTip(HostStatus.Tip(_engine.Servers, _engine.Gpu));
                    var now = _engine.Servers.Select(s => $"{s.Name}:{s.Info.Port}").ToList();
                    var change = HostStatus.Change(lastServers, now);
                    if (change.Length > 0) AppLog.Write(change);
                    lastServers = now;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Error("tick", ex); }
                _quit.Wait(Math.Max(500, _settings.IntervalMs));
            }
        });

        _quit.Wait();
        loop.Wait(TimeSpan.FromSeconds(3));
        StopLink();
        AppLog.Write("host: stopped");
    }

    // ── Verbindung zum Server ──

    private HostLinkClient NewLink(HostConfig config)
    {
        var link = new HostLinkClient(config,
            () => StateJson.WriteText(_engine, null, null, 0, DateTimeOffset.Now, withHistory: false),
            Environment.MachineName, AppLog.Version(), onCommand: _commands.RunAsync);
        link.Log += AppLog.Write;
        return link;
    }

    private void StartLink(HostConfig config)
    {
        lock (_linkGate)
        {
            StopLinkLocked();
            _config = config;
            _link = NewLink(config);
            _linkStop = new CancellationTokenSource();
            _linkTask = _link.RunAsync(_linkStop.Token);
        }
        if (!config.Paired) AppLog.Write("link: not paired yet");
    }

    private void StopLink() { lock (_linkGate) StopLinkLocked(); }

    private void StopLinkLocked()
    {
        _linkStop.Cancel();
        try { _linkTask.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _linkStop.Dispose();
    }

    // Koppeln über die kleine Seite: Code an den Server, Token speichern, neu verbinden
    private async Task<HostPairClient.Result> PairAsync(string server, string code)
    {
        var result = await HostPairClient.PairAsync(server, code, Environment.MachineName).ConfigureAwait(false);
        AppLog.Write($"pairing with {server}: {(result.Ok ? "ok" : result.Message)}");
        if (!result.Ok) return result;
        var config = new HostConfig { Server = server.Trim().TrimEnd('/'), Token = result.Token, ServerName = result.ServerName };
        config.Save(_paths, _platform);
        StartLink(config);
        return result;
    }

    // Dienst: eine Anfrage von „StykkerHost pair-service / unpair-service“ ausführen und das Ergebnis hinlegen
    private async Task ServiceRequestAsync()
    {
        var req = HostServiceFiles.TakeRequest(_paths);
        if (req == null) return;
        if (req.Unpair)
        {
            Unpair();
            HostServiceFiles.WriteResult(_paths, new HostServiceFiles.Result(true, Strings.HostServiceUnpaired));
            return;
        }
        var r = await PairAsync(req.Server, req.Code).ConfigureAwait(false);
        HostServiceFiles.WriteResult(_paths, new HostServiceFiles.Result(r.Ok, r.Ok ? Strings.HostPairDone(req.Server) : r.Message));
    }

    private void Unpair()
    {
        try { File.Delete(Path.Combine(_paths.Root, HostConfig.FileName)); } catch (IOException) { }
        AppLog.Write("unpaired");
        StartLink(new HostConfig());
    }

    private void OpenPairPage()
    {
        try
        {
            if (_pairPage == null)
            {
                _pairPage = new HostPairPage(PairAsync);
                _pairPage.Start();
            }
            Open(_pairPage.Url);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.HttpListenerException) { AppLog.Write("pair page: " + ex.Message); }
    }

    // ── Tray ──

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private TrayIcon? Tray()
    {
        var exe = Environment.ProcessPath ?? "";
        IReadOnlyList<TrayIcon.Item> Items()
        {
            var items = new List<TrayIcon.Item>();
            int id = 100;
            foreach (var line in HostStatus.Lines(_engine.Servers, _engine.Gpu)) items.Add(new TrayIcon.Item(id++, line, Disabled: true));
            items.Add(new TrayIcon.Item(0, "", Separator: true));
            items.Add(new TrayIcon.Item(1, LinkText(), Disabled: true));
            items.Add(_config.Paired ? new TrayIcon.Item(MenuUnpair, Strings.HostUnpairMenu) : new TrayIcon.Item(MenuPair, Strings.HostPairMenu));
            items.Add(new TrayIcon.Item(0, "", Separator: true));
            items.Add(new TrayIcon.Item(MenuAutostart, Autostart.IsOn(Strings.HostName, exe) ? Strings.HostAutostartOn : Strings.HostAutostart));
            items.Add(new TrayIcon.Item(MenuLog, Strings.HostOpenLog));
            items.Add(new TrayIcon.Item(MenuQuit, Strings.HostQuit));
            return items;
        }
        var tray = new TrayIcon(Items, id =>
        {
            switch (id)
            {
                case MenuPair: OpenPairPage(); break;
                case MenuUnpair: Unpair(); break;
                case MenuAutostart:
                    bool on = !Autostart.IsOn(Strings.HostName, exe);
                    AppLog.Write($"autostart: {(Autostart.Set(Strings.HostName, exe, on) ? (on ? "on" : "off") : "could not change")}");
                    break;
                case MenuLog:
                    if (AppLog.File is { } log) Open(log);
                    break;
                case MenuQuit:
                    AppLog.Write("host: quit from tray");
                    _quit.Set();
                    break;
            }
        }, () => { if (!_config.Paired) OpenPairPage(); else if (AppLog.File is { } log) Open(log); });
        if (tray.TryShow(Strings.HostName, out var error)) return tray;
        AppLog.Write($"{Strings.HostTrayFailed}: {error}");
        tray.Dispose();
        return null;
    }

    private string LinkText() => _link.State switch
    {
        HostLinkState.NotPaired => Strings.HostNotPaired,
        HostLinkState.Connected => Strings.HostConnected(_config.ServerName.Length > 0 ? _config.ServerName : _config.Server),
        HostLinkState.Connecting => Strings.HostConnecting(_config.Server),
        _ => Strings.HostWaiting(_link.LastError),
    };

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { AppLog.Write("open: " + ex.Message); }
    }

    public void Dispose()
    {
        _pairPage?.Dispose();
        if (OperatingSystem.IsWindows()) _tray?.Dispose();
        _engine.Dispose();
        _platform.Dispose();
        _quit.Dispose();
    }
}
