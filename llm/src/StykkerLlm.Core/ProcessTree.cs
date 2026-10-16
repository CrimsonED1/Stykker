namespace StykkerLlm.Core;

// Speicher eines Prozessbaums: der Prozess selbst plus alle seine Nachfahren (z. B. venv-Starter → Python → Engine).
// Begrenzt in Tiefe und Anzahl, damit eine Abfrage im Hintergrund immer kurz bleibt.
public static class ProcessTree
{
    public const int MaxDepth = 4;
    public const int MaxNodes = 64;

    // Summe der Working Sets (GB) des Wurzelprozesses und seiner Nachfahren. Prozesse aus `skip` (eigene Kindserver,
    // die separat beobachtet werden) und deren Nachfahren zählen nicht mit. null = keiner der Prozesse ist lesbar.
    public static double? WorkingSetGb(IPlatform platform, int root, IReadOnlySet<int>? skip = null)
    {
        double sum = 0;
        bool any = false;
        var seen = new HashSet<int> { root };
        var level = new List<int> { root };
        for (int depth = 0; depth <= MaxDepth && level.Count > 0; depth++)
        {
            var next = new List<int>();
            foreach (var pid in level)
            {
                if (platform.ReadUsage(pid) is { } u) { sum += u.WorkingSetGb; any = true; }
                if (depth == MaxDepth) continue;
                foreach (var c in platform.ChildProcesses(pid))
                {
                    if (skip != null && skip.Contains(c.Pid)) continue;
                    if (seen.Count >= MaxNodes) break;
                    if (seen.Add(c.Pid)) next.Add(c.Pid);
                }
            }
            level = next;
        }
        return any ? sum : null;
    }
}
