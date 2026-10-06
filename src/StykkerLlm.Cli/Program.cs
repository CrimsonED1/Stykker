using System.Reflection;
using StykkerLlm.Cli.Tui;

namespace StykkerLlm.Cli;

internal static class Program
{
    private const string HelpText = """
        stykker – terminal companion of StykkerLLM

        Usage: stykker                      interactive: live status on top, type /commands below (like Claude Code)
               stykker <command> [options]  one command, for scripts

        Commands
          status                 measure for a moment and show GPU, system and running servers
            --json               machine-readable (stable schema, for status bars and scripts)
            --watch [N]          repeat every N seconds (JSON: one line per measurement)
          list [what]            saved (default), history, bench, recordings   [--json]
          show <id|name>         details of a history entry or saved profile: command line, parameters, runs   [--json]
          start <name|id>        start a saved profile (or a history entry by id)   [--wait]
          stop <name|port>       stop a running server (asks first; --yes to confirm in scripts)
          unload <server> <model>  Ollama / LM Studio: unload a model
          proxy [on|off|lan|serve <server>]   the Stykker-Proxy: state, on/off, network, served model
          notice [dismiss]        the standing hint; "dismiss" clears it
          recent [n]              the last requests: server, task, tokens, time, status
          about                   version, data folder, license
          sim                    interactive view of the simulator (fake servers, nothing real is touched)
          eval <server|url>      run the model test suite (coding with tests, reasoning, format, tools, long context) and score it
            --suite hard         the harder built-in suite (or a name/file)   --repeat N  N runs with different seeds
            --only coding,tools  only these categories      --md <file>  Markdown report
          eval results [N]       compare the last N eval runs      eval suites   list suites (own: <data>\eval\*.json)
          eval models            one line per model: score per category (mean ± spread over runs), speed   [--md file]
          web                    start the web interface (the server does the measuring)
          server [status|start|stop|restart]   the Stykker server as a process
          qr                     QR code and access code for the phone
          remote [on|off]        reach the web interface from the network (Home/VPN)
          devices [id]           signed-in devices; with an id: remove it
          role <id> viewer|admin what a signed-in device may do (viewer only looks, admin operates)
          approve <code> [viewer] let in a device that shows six digits (phone, tablet, a hub)
          nodes                  other PCs paired with this one: list, search, pair <url> [code], remove <id>

        Options
          --data-dir <folder>    other data folder (default: the one of the app)
          --local                ignore a running server and measure here
          --sim                  run the command against the simulator
          --yes, -y              answer confirmations with yes (secrets are never answered)
          --interval <ms>        measuring interval (default 1000)
          --color auto|truecolor|256|16|none    --no-color    --ascii
          --help, --version

        Exit codes: 0 ok · 1 error · 2 wrong arguments · 3 not found / ambiguous · 4 cancelled
        Read-only commands (status, list) never change anything and work next to the open app.
        """;

    private static async Task<int> Main(string[] argv)
    {
        var (a, error) = CliArgs.Parse(argv);
        // Linux/macOS ohne UTF-8-Locale (LANG=C): Rahmen- und Blockzeichen würden als Zeichensalat erscheinen → ASCII
        if (!OperatingSystem.IsWindows() && !LocaleIsUtf8()) a.Ascii = true;
        Out.Init(a);
        if (error != null) { Out.Error(error); Console.Error.WriteLine("Try 'stykker --help'."); return Commands.Usage; }
        if (a.Help || a.Command == "help") { Console.Out.WriteLine(HelpText); return Commands.Ok; }
        if (a.Version) { Console.Out.WriteLine("stykker " + Version()); return Commands.Ok; }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };   // Strg+C: sauber beenden (Engine entsorgen)
        try
        {
            switch (a.Command)
            {
                case "" or "top" or "sim":
                    if (a.Command == "sim") a.Sim = true;
                    // ohne Terminal (Pipe, Skript): einmal den Status ausgeben statt der interaktiven Oberfläche
                    if (a.Snapshot == null && (Console.IsInputRedirected || Console.IsOutputRedirected))
                        return await Commands.StatusAsync(a, cts.Token);
                    return await RunInteractiveAsync(a, cts.Token);
                case "status":
                    return await Commands.StatusAsync(a, cts.Token);
                case "list" or "ls":
                    return await Commands.ListAsync(a);
                case "show":
                    return Commands.Show(a);
                case "start":
                    return await Commands.StartAsync(a, cts.Token);
                case "stop":
                    return await Commands.StopAsync(a, cts.Token);
                case "unload":
                    return await Commands.UnloadAsync(a, cts.Token);
                case "proxy" or "notice" or "recent" or "about":
                    return await ProxyCommand.RunAsync(a.Command, a, cts.Token);
                case "eval":
                    return await EvalCommand.RunAsync(a, cts.Token);
                case "web":
                    return await ServerCommand.WebAsync(a, cts.Token);
                case "server":
                    return await ServerCommand.ServerAsync(a, cts.Token);
                case "qr":
                    return await ServerCommand.QrAsync(a, cts.Token);
                case "remote":
                    return await ServerCommand.RemoteAsync(a, cts.Token);
                case "devices":
                    return await ServerCommand.DevicesAsync(a, cts.Token);
                case "role":
                    return await ServerCommand.RoleAsync(a, cts.Token);
                case "approve":
                    return await ServerCommand.ApproveAsync(a, cts.Token);
                case "nodes" or "node":
                    return await NodeCommand.RunAsync(a, cts.Token);
                default:
                    Out.Error($"unknown command '{a.Command}'. Try 'stykker --help'.");
                    return Commands.Usage;
            }
        }
        catch (OperationCanceledException) { return Commands.Cancelled; }
        catch (CliException ex) { Out.Error(ex.Message); return ex.ExitCode; }
        catch (Exception ex) { Out.Error(ex.Message); return Commands.Error; }
    }

    private static async Task<int> RunInteractiveAsync(CliArgs a, CancellationToken ct)
    {
        var prompt = new TuiPrompt();
        using var s = new Session(a, SessionMode.Auto, prompt);
        ITerminal term = a.Snapshot != null ? new VirtualTerminal(a.SnapWidth, a.SnapHeight, a.Keys ?? "") : new ConsoleTerminal();
        var app = new TuiApp(term, s, prompt, a);
        await app.RunAsync(ct);
        if (term is VirtualTerminal vt)
        {
            // Bild für Tests und Doku: mit Farben (.ans, ansehen mit "cat") und als reiner Text (.txt)
            File.WriteAllText(a.Snapshot!, string.Join("\n", vt.Screen) + "\u001b[0m\n");
            File.WriteAllText(Path.ChangeExtension(a.Snapshot!, ".txt"), string.Join("\n", vt.Screen.Select(l => Ansi.Strip(l).TrimEnd())) + "\n");
        }
        return Commands.Ok;
    }

    internal static bool LocaleIsUtf8()
    {
        foreach (var name in new[] { "LC_ALL", "LC_CTYPE", "LANG" })
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(v)) continue;   // die erste gesetzte Variable entscheidet (wie bei setlocale)
            v = v.ToLowerInvariant();
            return v.Contains("utf-8") || v.Contains("utf8");
        }
        return false;
    }

    private static string Version() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
}
