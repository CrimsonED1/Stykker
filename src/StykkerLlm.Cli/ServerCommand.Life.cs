using StykkerLlm.Core;

namespace StykkerLlm.Cli;

// Den Server starten und beenden (S1/S7). Ohne Fenster bleibt sonst nur der Browser, um ihn auszuschalten.
public static partial class ServerCommand
{
    // stykker server status|start|stop   (prompt: die Rückfrage der aufrufenden Oberfläche, sonst das Terminal)
    public static async Task<int> ServerAsync(CliArgs a, CancellationToken ct, IUserPrompt? prompt = null)
    {
        var what = a.Words.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        switch (what)
        {
            case "start": return await WebAsync(a, ct).ConfigureAwait(false);
            case "stop": return await StopAsync(a, ct, prompt).ConfigureAwait(false);
            case "status": return await StatusAsync(a, ct).ConfigureAwait(false);
            case "restart":
            {
                await StopAsync(a, ct, prompt).ConfigureAwait(false);
                // der alte Prozess braucht einen Moment, sonst hält die Sperre den neuen Start kurz fest
                for (int i = 0; i < 20 && await ServerClient.IsRunningAsync(a.Port, ct).ConfigureAwait(false); i++)
                {
                    try { await Task.Delay(250, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                }
                return await WebAsync(a, ct).ConfigureAwait(false);
            }
            default:
                Out.Error("usage: stykker server [status|start|stop|restart]");
                return Commands.Usage;
        }
    }

    private static async Task<int> StatusAsync(CliArgs a, CancellationToken ct)
    {
        using var client = TryConnect(a);
        if (client?.State is not { } state)
        {
            Console.Out.WriteLine($"{Out.Dim("not running")}   (start with 'stykker web' or 'stykker server start')");
            return Commands.Ok;
        }
        var o = Console.Out;
        o.WriteLine($"{Out.Green("running")}  {NetAddr.Url("127.0.0.1", state.ServerPort == 0 ? a.Port : state.ServerPort)}   tick {state.Ticks}   {state.Servers.Count} servers");
        o.WriteLine(Out.Dim($"  data folder: {state.Settings.DataDir}"));
        o.WriteLine(state.Access.Remote
            ? $"  {Strings.RemoteOn}: {NetAddr.Url(state.Access.LanAddress, state.ServerPort == 0 ? a.Port : state.ServerPort)}   code {state.Access.Code}"
            : $"  {Strings.RemoteThisPcOnly}   (code {state.Access.Code})");
        o.WriteLine(state.Access.Devices.Count == 0
            ? Out.Dim("  devices: none")
            : "  devices: " + string.Join(", ", state.Access.Devices.Select(d => d.Name + " (" + d.LastSeen?.ToString("g") + ")")));
        if (state.Eval is { Running: true }) o.WriteLine(Out.Dim("  model tests are running"));
        var sessions = state.Recording.Sessions;
        if (sessions.Count > 0) o.WriteLine(Out.Dim("  recording: " + string.Join(", ", sessions.Select(s => s.Global ? Strings.AllServers : s.Key))));
        return Commands.Ok;
    }

    private static async Task<int> StopAsync(CliArgs a, CancellationToken ct, IUserPrompt? prompt = null)
    {
        using var client = TryConnect(a);
        if (client == null)
        {
            Out.Error(Strings.ServerNotRunning);
            return Commands.Error;
        }
        if (a.Words.Contains("force") || a.Yes)
        {
            var result = await client.SendAsync("shutdown", ct: ct).ConfigureAwait(false);
            Console.Out.WriteLine(result.Ok ? Out.Green(Strings.ServerStopping) : Out.Red(result.Message));
            return result.Ok ? Commands.Ok : Commands.Error;
        }
        if (!await ConfirmStop(prompt).ConfigureAwait(false))
        {
            Console.Out.WriteLine(Out.Dim(Strings.Cancelled));
            return Commands.Cancelled;
        }
        var res = await client.SendAsync("shutdown", ct: ct).ConfigureAwait(false);
        Console.Out.WriteLine(res.Ok ? Out.Green(Strings.ServerStopping) : Out.Red(res.Message));
        return res.Ok ? Commands.Ok : Commands.Error;
    }

    // Die TUI fragt im Bildschirm, das Terminal auf der Konsole – nie ein blockierendes ReadLine im Hintergrund
    private static async Task<bool> ConfirmStop(IUserPrompt? prompt)
    {
        if (prompt != null) return await prompt.ConfirmAsync(Strings.ServerTitle, Strings.ServerStopConfirm, warning: true).ConfigureAwait(false);
        return !Console.IsInputRedirected && Confirm(Strings.ServerStopConfirm);
    }

    private static bool Confirm(string text)
    {
        Console.Error.Write(text + " [y/N] ");
        var answer = Console.ReadLine();
        return answer is { } a && (a.Equals("y", StringComparison.OrdinalIgnoreCase) || a.Equals("j", StringComparison.OrdinalIgnoreCase));
    }
}