using System.ComponentModel;
using System.Diagnostics;
using Stykker.Shared.Config;

namespace StykkerHud.Server;

// Startet einen eigenen Dienst der Zentrale (docs/plan-stykkernode.md, Abschnitt 4.1 bis 4.3) und beendet nur den Prozess,
// den sie selbst gestartet hat, über dessen PID. Das Token geht nur über die Umgebung, nie über die Kommandozeile und nie
// ins Log.
internal sealed class ServiceProcesses
{
    private readonly List<Process> _owned = [];
    private readonly Action<string> _log;

    public ServiceProcesses(Action<string> log) => _log = log;

    // Antwortet der Zielport schon, startet nichts: dann läuft, was da ist, und die Zentrale beendet es nicht.
    public async Task StartAsync(ServiceRoute route, CancellationToken cancel = default)
    {
        var start = route.Start;
        if (start == null) return;

        var probe = new HubModule(route.Id, route.Name, route.Port, null, true);
        if (await HubProbe.AnswersAsync(probe, route.Target, cancel))
        {
            _log($"{route.Name}: answers on {route.Target}; not started by the hub.");
            return;
        }

        var info = new ProcessStartInfo(start.Command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Der Zusatz der Zentrale hängt immer an, egal was in args steht
        foreach (var arg in WithoutOwnSwitches(start.Args)) info.ArgumentList.Add(arg);
        info.ArgumentList.Add("--hostname");
        info.ArgumentList.Add("127.0.0.1");
        info.ArgumentList.Add("--require-auth");
        foreach (var (name, value) in start.Env) info.Environment[name] = value;
        info.Environment[start.TokenEnv] = route.Token;

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log($"{route.Name}: could not start {start.Command}: {ex.Message}");
            return;
        }

        // Die Ausgabe ins Log der Zentrale, ohne das Token (falls der Dienst es doch ausgibt)
        string token = route.Token;
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) => Relay(route.Name, e.Data, token);
        process.ErrorDataReceived += (_, e) => Relay(route.Name, e.Data, token);
        process.Exited += (_, _) => _log($"{route.Name}: stopped (exit code {SafeExitCode(process)}).");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _owned.Add(process);
        _log($"{route.Name}: started (pid {process.Id}) on {route.Target}.");
    }

    // Beim Ende der Zentrale: nur die eigenen Prozesse, jeder über seine PID
    public void StopOwned()
    {
        foreach (var process in _owned)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* schon beendet */ }
            finally { process.Dispose(); }
        }
        _owned.Clear();
    }

    // --hostname und --require-auth setzt die Zentrale selbst: was in args steht, fällt weg
    internal static IEnumerable<string> WithoutOwnSwitches(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("--require-auth", StringComparison.OrdinalIgnoreCase)) continue;
            if (arg.Equals("--hostname", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            if (arg.StartsWith("--hostname=", StringComparison.OrdinalIgnoreCase)) continue;
            yield return arg;
        }
    }

    private void Relay(string name, string? line, string token)
    {
        if (string.IsNullOrEmpty(line)) return;
        _log($"{name}: {line.Replace(token, "***", StringComparison.Ordinal)}");
    }

    private static string SafeExitCode(Process process)
    {
        try { return process.ExitCode.ToString(); }
        catch (InvalidOperationException) { return "unknown"; }
    }
}
