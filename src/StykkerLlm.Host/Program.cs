using System.Diagnostics;
using StykkerLlm.Core;
using StykkerLlm.Platform.Windows;

namespace StykkerLlm.Host;

// StykkerHost: ein PC, der nur Modelle laufen lässt (docs/plan-hosts-gateway.md, P1). Er misst GPU, System und die
// Modellserver dieses PCs mit derselben Engine wie der Server, zeigt den Stand im Tray und schreibt StykkerHost.log.
// Er öffnet keinen Port: später (P2) verbindet er sich selbst mit dem Server.
internal static class Program
{
    private const int MenuAutostart = 10, MenuLog = 11, MenuQuit = 12;

    private static int Main(string[] args)
    {
        var paths = HostStatus.DefaultPaths();
#if DEBUG
        // Entwickler-Schalter (nur Debug): anderer Datenordner
        int i = Array.IndexOf(args, "--data-dir");
        if (i >= 0 && i + 1 < args.Length) paths = new AppPaths(Path.GetFullPath(args[i + 1]));
#endif
        Directory.CreateDirectory(paths.Root);
        AppLog.Init("StykkerHost", paths);
        AppLog.CatchUnhandled();

        using var single = new SingleInstance(SingleInstance.NameFor(paths.Root));
        if (!single.IsFirst)
        {
            AppLog.Write(Strings.HostAlreadyRunning);
            return 0;
        }

        IPlatform platform = OperatingSystem.IsWindows() ? new WindowsPlatform() : new BasicPlatform();
        var settings = AppSettings.Load(paths.SettingsFile);
        using var engine = new MonitorEngine(platform, paths, settings, readOnly: false);
        using var quit = new ManualResetEventSlim(false);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => quit.Set();

        // Verbindung zum Server (P2): Adresse und Token aus host.json (die Kopplung, P3, schreibt sie)
        var config = HostConfig.Load(paths, platform);
        var link = new HostLinkClient(config,
            () => StateJson.WriteText(engine, null, null, 0, DateTimeOffset.Now, withHistory: false),
            Environment.MachineName, AppLog.Version());
        link.Log += AppLog.Write;
        using var linkStop = new CancellationTokenSource();
        var linkTask = link.RunAsync(linkStop.Token);
        if (!config.Paired) AppLog.Write("link: not paired yet (no host.json)");

        TrayIcon? tray = null;
        if (OperatingSystem.IsWindows()) tray = Tray(engine, link, config, quit);

        var lastServers = new List<string>();
        bool first = true;
        var loop = Task.Run(async () =>
        {
            while (!quit.IsSet)
            {
                try
                {
                    await engine.TickAsync().ConfigureAwait(false);
                    if (first) { first = false; AppLog.Write($"host: running, GPU {engine.Gpu?.Name ?? Strings.HostNoGpu}"); }
                    if (OperatingSystem.IsWindows()) tray?.SetTip(HostStatus.Tip(engine.Servers, engine.Gpu));
                    var now = engine.Servers.Select(s => $"{s.Name}:{s.Info.Port}").ToList();
                    var change = HostStatus.Change(lastServers, now);
                    if (change.Length > 0) AppLog.Write(change);
                    lastServers = now;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Error("tick", ex); }
                quit.Wait(Math.Max(500, settings.IntervalMs));
            }
        });

        quit.Wait();
        loop.Wait(TimeSpan.FromSeconds(3));
        linkStop.Cancel();
        try { linkTask.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        if (OperatingSystem.IsWindows()) tray?.Dispose();
        platform.Dispose();
        AppLog.Write("host: stopped");
        return 0;
    }

    // Tray: Stand (grau), Kopplung (kommt mit P3), Mit Windows starten, Protokoll, Beenden
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static TrayIcon? Tray(MonitorEngine engine, HostLinkClient link, HostConfig config, ManualResetEventSlim quit)
    {
        var exe = Environment.ProcessPath ?? "";
        IReadOnlyList<TrayIcon.Item> Items()
        {
            var items = new List<TrayIcon.Item>();
            int id = 100;
            foreach (var line in HostStatus.Lines(engine.Servers, engine.Gpu)) items.Add(new TrayIcon.Item(id++, line, Disabled: true));
            items.Add(new TrayIcon.Item(0, "", Separator: true));
            items.Add(new TrayIcon.Item(1, LinkText(link, config), Disabled: true));
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
                case MenuAutostart:
                    bool on = !Autostart.IsOn(Strings.HostName, exe);
                    AppLog.Write($"autostart: {(Autostart.Set(Strings.HostName, exe, on) ? (on ? "on" : "off") : "could not change")}");
                    break;
                case MenuLog:
                    if (AppLog.File is { } log) Open(log);
                    break;
                case MenuQuit:
                    AppLog.Write("host: quit from tray");
                    quit.Set();
                    break;
            }
        }, () => { if (AppLog.File is { } log) Open(log); });
        if (tray.TryShow(Strings.HostName, out var error)) return tray;
        AppLog.Write($"{Strings.HostTrayFailed}: {error}");
        tray.Dispose();
        return null;
    }

    private static string LinkText(HostLinkClient link, HostConfig config) => link.State switch
    {
        HostLinkState.NotPaired => Strings.HostNotPaired,
        HostLinkState.Connected => Strings.HostConnected(config.ServerName.Length > 0 ? config.ServerName : config.Server),
        HostLinkState.Connecting => Strings.HostConnecting(config.Server),
        _ => Strings.HostWaiting(link.LastError),
    };

    private static void Open(string file)
    {
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { AppLog.Write("open log: " + ex.Message); }
    }
}
