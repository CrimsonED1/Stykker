using System.Reflection;
using StykkerLlm.Cli.Tui;
using StykkerLlm.Core;

namespace StykkerLlm.Cli;

internal static class Program
{
    public const int Ok = 0, Error = 1, Usage = 2;

    private const string HelpText = """
        stykker – the terminal display of StykkerLLM

          stykker              live display: GPU, system, running servers, the web address (keys: w web, c code, ? help)
          stykker status       the same once, for scripts and a quick look
          stykker web          open the web interface in the browser (signed in) – everything is controlled there
          stykker stop         shut the StykkerLLM server down
          stykker bugreport <what happened>
                               zip with logs, settings and state (no secrets) + a link for a GitHub issue
          stykker help | version

        The server starts when needed and ends by itself when no window, terminal or web page uses it.
        """;

    private static async Task<int> Main(string[] argv)
    {
        var (a, error) = CliArgs.Parse(argv);
        Out.Init();
        if (error != null) { Out.Error(error); return Usage; }
        if (a.Snapshot == null && !a.Sim)
        {
            AppLog.Init("stykker", a.Paths.Get());
            AppLog.CatchUnhandled();
            AppLog.Write($"command: {(a.Command.Length == 0 ? "(display)" : a.Command)}");
        }
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            switch (a.Command)
            {
                case "":
                    return await DisplayAsync(a, cts.Token);
                case "status":
                    return await StatusAsync(a, cts.Token);
                case "web":
                    return await WebAsync(a, cts.Token);
                case "stop":
                    return await StopAsync(a, cts.Token);
                case "bugreport" or "bug":
                    return await BugReportCommand.RunAsync(a, cts.Token);
                case "help" or "-h" or "/?":
                    Console.Out.WriteLine(HelpText);
                    return Ok;
                case "version":
                    Console.Out.WriteLine("stykker " + Version());
                    return Ok;
                default:
                    Out.Error($"unknown command '{a.Command}'. Try 'stykker help'.");
                    return Usage;
            }
        }
        catch (OperationCanceledException) { return Error; }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
        {
            Out.Error(ex.Message);
            AppLog.Write($"error: {ex.Message}");
            return Error;
        }
    }

    // Die Anzeige. Ohne Terminal (Pipe, Skript) einmal den Stand ausgeben.
    private static async Task<int> DisplayAsync(CliArgs a, CancellationToken ct)
    {
        if (a.Snapshot == null && (Console.IsInputRedirected || Console.IsOutputRedirected)) return await StatusAsync(a, ct);
        using IStateSource source = a.Sim ? new SimSource() : new ServerSource(a);
        ITerminal term = a.Snapshot != null ? new VirtualTerminal(a.SnapWidth, a.SnapHeight, a.Keys) : new ConsoleTerminal();
        var app = new TuiApp(term, source, a.Snapshot != null ? _ => { } : null);
        await app.RunAsync(ct);
        if (term is VirtualTerminal vt && a.Snapshot != null)
        {
            // Bild für Doku und Prüfung (nur Debug): mit Farben (.ansi) und als reiner Text (.txt)
            File.WriteAllText(a.Snapshot, string.Join("\n", vt.Screen) + "\u001b[0m\n");
            File.WriteAllText(Path.ChangeExtension(a.Snapshot, ".txt"), string.Join("\n", vt.Screen.Select(l => Ansi.Strip(l).TrimEnd())) + "\n");
        }
        return Ok;
    }

    private static async Task<int> StatusAsync(CliArgs a, CancellationToken ct)
    {
        using var client = await ServerLink.ConnectAsync(a, ct);
        if (client?.State is not { } state)
        {
            Console.Out.WriteLine(Strings.TuiNotRunning);
            return Error;
        }
        int w = Console.IsOutputRedirected ? 120 : Math.Clamp(SafeWidth(), 60, 200);
        foreach (var line in TuiApp.StatusLines(state, a.Port, w, Out.Color)) Console.Out.WriteLine(line.TrimEnd());
        return Ok;
    }

    private static async Task<int> WebAsync(CliArgs a, CancellationToken ct)
    {
        using var client = await ServerLink.EnsureAsync(a, s => Console.Out.WriteLine(Out.Dim(s)), ct);
        var url = ServerLink.PairUrl(a.Port, client.State?.Access.Code ?? "");
        Console.Out.WriteLine($"{Out.Green(Strings.TuiWebLabel)}  {ServerClient.DefaultUrl(a.Port)}");
        ServerLink.OpenBrowser(url);
        return Ok;
    }

    private static async Task<int> StopAsync(CliArgs a, CancellationToken ct)
    {
        using var client = await ServerLink.ConnectAsync(a, ct);
        if (client == null) { Console.Out.WriteLine(Strings.TuiNotRunning); return Ok; }
        var r = await client.SendAsync("shutdown", ct: ct);
        Console.Out.WriteLine(r.Ok ? Strings.TuiStopped : r.Message);
        return r.Ok ? Ok : Error;
    }

    private static int SafeWidth()
    {
        try { return Console.WindowWidth; } catch (IOException) { return 120; }
    }

    private static string Version() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
}
