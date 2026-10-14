namespace StykkerStarter.Core.Processes;

public sealed record StopReport(IReadOnlyList<int> Closed, IReadOnlyList<int> Killed, IReadOnlyList<int> Left)
{
    public bool AllGone => Left.Count == 0;
}

// Beendet alles, was aus einem Ordner läuft: erst höflich (Hauptfenster schließen), nach der Frist endgültig.
// Danach wird noch einmal geprüft, weil ein Programm während des Beendens andere aus seinem Ordner gestartet haben
// kann. Mehr als drei Runden gibt es nicht; was dann noch läuft, steht in Left.
public static class FolderStopper
{
    private const int MaxRounds = 3;

    public static StopReport StopAll(IProcessSystem system, string folder, int ownPid, TimeSpan grace, StarterLog? log = null)
    {
        var closed = new List<int>();
        var killed = new List<int>();
        for (int round = 0; round < MaxRounds; round++)
        {
            var targets = FolderProcesses.RunningFrom(system, folder, ownPid);
            if (targets.Count == 0) break;

            foreach (var target in targets)
            {
                if (system.CloseMainWindow(target.Pid)) closed.Add(target.Pid);
                log?.Write($"stop: asked pid {target.Pid} to close");
            }

            var deadline = DateTime.UtcNow + grace;
            while (DateTime.UtcNow < deadline && targets.Any(t => system.IsRunning(t.Pid)))
                Thread.Sleep(50);

            // Vor dem Beenden noch einmal prüfen: die PID könnte in der Zwischenzeit einem anderen Prozess gehören
            foreach (var still in FolderProcesses.RunningFrom(system, folder, ownPid))
            {
                system.Kill(still.Pid);
                killed.Add(still.Pid);
                log?.Write($"stop: ended pid {still.Pid} after {grace.TotalSeconds:0} s");
            }
        }
        var left = FolderProcesses.RunningFrom(system, folder, ownPid).Select(p => p.Pid).ToList();
        return new StopReport(closed, killed, left);
    }
}
