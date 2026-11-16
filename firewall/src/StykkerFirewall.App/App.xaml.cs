using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Stykker.Shared.Windows;
using StykkerFirewall.Core;
using StykkerFirewall.Core.Ipc;

namespace StykkerFirewall.App;

// Läuft im Tray. Das Nachfrage-Fenster erscheint, sobald der Dienst eine offene Nachfrage meldet, und verschwindet,
// wenn keine mehr offen ist. Die Übersicht öffnet sich über das Tray-Symbol.
// Befehlszeile: -pipe <name> (Probelauf gegen einen Dienst mit Simulator), -show (Übersicht gleich öffnen).
public partial class App : Application
{
    private const int MenuOpen = 1, MenuAsk = 2, MenuMonitor = 3, MenuExit = 9;

    private Mutex? _single;
    private FirewallClient? _client;
    private ServiceProvider? _services;
    private TrayIcon? _tray;
    private MainWindow? _main;
    private PromptWindow? _prompt;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _single = new Mutex(true, @"Local\StykkerFirewall.App." + PipeArg(e.Args), out bool first);
        if (!first) { Shutdown(); return; }

        // WebView2 braucht einen beschreibbaren Datenordner; neben der exe (Program Files) geht das nicht.
        var webData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stykker", "Firewall", "WebView2");
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", webData);

        _client = new FirewallClient(PipeArg(e.Args));
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
        services.AddSingleton(_client);
        services.AddSingleton<UiActions>(new UiActions(HidePrompt));
        _services = services.BuildServiceProvider();

        _client.Changed += () => Dispatcher.BeginInvoke(UpdatePrompt);
        _client.PromptRaised += _ => Dispatcher.BeginInvoke(UpdatePrompt);
        _client.Start();

        _tray = new TrayIcon(TrayItems, id => Dispatcher.BeginInvoke(() => OnMenu(id)), () => Dispatcher.BeginInvoke(ShowMain));
        _tray.TryShow("StykkerFirewall", out _);
        if (e.Args.Any(a => a is "-show" or "--show")) ShowMain();
    }

    private static string PipeArg(string[] args)
    {
        int i = Array.FindIndex(args, a => a is "-pipe" or "--pipe");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : PipeNames.Default;
    }

    private IReadOnlyList<TrayIcon.Item> TrayItems()
    {
        var mode = _client?.State?.Status.Mode;
        return
        [
            new(MenuOpen, "Open overview"),
            new(0, "", Separator: true),
            new(MenuAsk, (mode == ProtectionMode.Ask ? "● " : "") + "Mode: ask for every app", Disabled: _client?.Connected != true),
            new(MenuMonitor, (mode == ProtectionMode.Monitor ? "● " : "") + "Mode: monitor only", Disabled: _client?.Connected != true),
            new(0, "", Separator: true),
            new(MenuExit, "Exit"),
        ];
    }

    private void OnMenu(int id)
    {
        switch (id)
        {
            case MenuOpen: ShowMain(); break;
            // Ask muss in der Übersicht bestätigt werden (Schutz gegen Aussperren), deshalb dort öffnen.
            case MenuAsk: _ = _client!.SetModeAsync(ProtectionMode.Ask); ShowMain(); break;
            case MenuMonitor: _ = _client!.SetModeAsync(ProtectionMode.Monitor); break;
            case MenuExit: Shutdown(); break;
        }
    }

    private void ShowMain()
    {
        _main ??= new MainWindow(_services!);
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void UpdatePrompt()
    {
        if (_client is null || _services is null) return;
        if (_client.Pending.Count > 0)
        {
            _prompt ??= new PromptWindow(_services);
            if (!_prompt.IsVisible) { _prompt.PlaceBottomRight(); _prompt.Show(); }
        }
        else
        {
            HidePrompt();
        }
    }

    private void HidePrompt() => Dispatcher.BeginInvoke(() => _prompt?.Hide());

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        if (_client is not null) _client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _services?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}

// Was Razor-Seiten am Fenster auslösen dürfen.
public sealed class UiActions(Action hidePrompt)
{
    public void HidePrompt() => hidePrompt();
}
