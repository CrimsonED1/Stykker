using System.ComponentModel;

namespace StykkerStarter.Core.Start;

public interface IStartedProgram
{
    int Pid { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
}

// Startet ein Programm (Windows in Stykker.Starter.Platform.Windows, Tests liefern Fakes).
public interface IProgramLauncher
{
    IStartedProgram Start(string exePath, string arguments, string workingDir);
}

public sealed record StartOutcome(bool Ok, int? Pid, string Message);

// „Startet nicht“ (Auftrag, Punkt 8): der Start scheitert, oder der Prozess endet innerhalb der Beobachtungszeit
// mit einem Code ungleich 0. Ein Ende mit 0 gilt als gestartet und gelaufen.
public static class ProgramStart
{
    public static StartOutcome Start(IProgramLauncher launcher, string exePath, string arguments, string workingDir, TimeSpan watch)
    {
        IStartedProgram started;
        try
        {
            started = launcher.Start(exePath, arguments, workingDir);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new StartOutcome(false, null, $"could not start: {ex.Message}");
        }

        var deadline = DateTime.UtcNow + watch;
        while (DateTime.UtcNow < deadline)
        {
            if (started.HasExited)
            {
                if (started.ExitCode is 0 or null)
                    return new StartOutcome(true, started.Pid, "started and ended with code 0");
                return new StartOutcome(false, started.Pid, $"ended with code {started.ExitCode} within {watch.TotalSeconds:0} s");
            }
            Thread.Sleep(50);
        }
        return new StartOutcome(true, started.Pid, "running");
    }
}
