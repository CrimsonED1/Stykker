using StykkerStarter.Core.Processes;
using StykkerStarter.Core.Start;

namespace StykkerStarter.Tests;

// Ein Prozess in der Fake-Liste. Closes = true: er beendet sich auf die höfliche Bitte.
internal sealed class FakeProc
{
    public required int Pid { get; init; }
    public required string ImagePath { get; init; }
    public bool Running { get; set; } = true;
    public bool Closes { get; init; }
}

internal sealed class FakeProcesses : IProcessSystem
{
    private int _nextPid = 5000;

    public List<FakeProc> Procs { get; } = new();

    // Protokoll der Aufrufe, in der Reihenfolge: "close 5001", "kill 5002"
    public List<string> Calls { get; } = new();

    // Wird nach einem erfolgreichen Schließen aufgerufen, z. B. um ein Kindprogramm zu starten
    public Action<FakeProcesses>? AfterClose { get; set; }

    public FakeProc Add(string imagePath, bool closes = false)
    {
        var proc = new FakeProc { Pid = _nextPid++, ImagePath = imagePath, Closes = closes };
        Procs.Add(proc);
        return proc;
    }

    public IReadOnlyList<ProcessInfo> List() =>
        Procs.Where(p => p.Running).Select(p => new ProcessInfo(p.Pid, p.ImagePath)).ToList();

    public bool IsRunning(int pid) => Procs.Any(p => p.Pid == pid && p.Running);

    public bool CloseMainWindow(int pid)
    {
        Calls.Add($"close {pid}");
        var proc = Procs.First(p => p.Pid == pid);
        if (!proc.Running || !proc.Closes) return false;
        proc.Running = false;
        AfterClose?.Invoke(this);
        return true;
    }

    public void Kill(int pid)
    {
        Calls.Add($"kill {pid}");
        Procs.First(p => p.Pid == pid).Running = false;
    }
}

internal sealed class FakeStarted : IStartedProgram
{
    public required int Pid { get; init; }
    public bool HasExited { get; set; }
    public int? ExitCode { get; set; }
}

// Startet Fakes. Pfade, die mit FailPrefix beginnen, scheitern. ExitCodes: Programmpfad-Präfix → Code, mit dem das
// Programm sofort endet.
internal sealed class FakeLauncher : IProgramLauncher
{
    private readonly FakeProcesses _processes;

    public FakeLauncher(FakeProcesses processes)
    {
        _processes = processes;
    }

    public string? FailPrefix { get; set; }
    public Dictionary<string, int> ExitCodes { get; } = new();
    public List<string> Started { get; } = new();

    public IStartedProgram Start(string exePath, string arguments, string workingDir)
    {
        Started.Add(exePath);
        if (FailPrefix != null && exePath.StartsWith(FailPrefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("the program cannot be started (test)");

        foreach (var (prefix, code) in ExitCodes)
        {
            if (exePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return new FakeStarted { Pid = 9000 + Started.Count, HasExited = true, ExitCode = code };
        }

        var proc = _processes.Add(exePath);
        return new FakeStarted { Pid = proc.Pid };
    }
}
