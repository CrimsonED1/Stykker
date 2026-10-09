using System.ComponentModel;
using System.Diagnostics;

namespace StykkerHud.Core;

// Was man mit einem Prozess tun kann. Jede Antwort sagt in Worten, was passiert ist – die Oberfläche zeigt den
// Text unverändert an (englisch, wie die ganze Oberfläche). Fehler sind Ergebnisse, keine Ausnahmen.
public sealed record ActionResult(bool Ok, string Message);

// Beenden, Priorität setzen, Dateipfad holen. RealTime fehlt absichtlich: damit lässt sich die Maschine
// lahmlegen, und für einen Monitor ist das keine sinnvolle Wahl.
public static class ProcessActions
{
    private static readonly Dictionary<string, ProcessPriorityClass> Levels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["idle"] = ProcessPriorityClass.Idle,
        ["belownormal"] = ProcessPriorityClass.BelowNormal,
        ["normal"] = ProcessPriorityClass.Normal,
        ["abovenormal"] = ProcessPriorityClass.AboveNormal,
        ["high"] = ProcessPriorityClass.High,
    };

    public static IEnumerable<string> LevelNames => Levels.Keys;

    public static ActionResult End(int pid, bool tree)
    {
        if (Refused(pid) is { } refused) return refused;
        try
        {
            using var process = Process.GetProcessById(pid);
            string name = process.ProcessName;
            process.Kill(tree);
            process.WaitForExit(2000);
            return new ActionResult(true, tree
                ? $"{name} (PID {pid}) and its children ended."
                : $"{name} (PID {pid}) ended.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new ActionResult(false, $"PID {pid} is no longer running.");
        }
        catch (Win32Exception ex)
        {
            return new ActionResult(false, $"Access denied for PID {pid}. Start StykkerHUD as administrator for this one. ({ex.Message})");
        }
    }

    public static ActionResult SetPriority(int pid, string level)
    {
        if (Refused(pid) is { } refused) return refused;
        if (!Levels.TryGetValue(level ?? "", out var priority))
            return new ActionResult(false, $"Unknown priority \"{level}\". Known: {string.Join(", ", Levels.Keys)}.");
        try
        {
            using var process = Process.GetProcessById(pid);
            process.PriorityClass = priority;
            return new ActionResult(true, $"{process.ProcessName} (PID {pid}) is now {priority}.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new ActionResult(false, $"PID {pid} is no longer running.");
        }
        catch (Win32Exception ex)
        {
            return new ActionResult(false, $"Priority of PID {pid} was refused – this one needs administrator rights. ({ex.Message})");
        }
    }

    // Der Dateipfad, wenn Windows ihn hergibt: geschützte Prozesse verweigern ihn.
    public static string? ExecutablePath(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception
                                      or NotSupportedException)
        {
            return null;
        }
    }

    // Zwei Prozesse bleiben unangetastet: die vier System-PIDs und dieser Server selbst.
    private static ActionResult? Refused(int pid)
    {
        if (pid <= 4) return new ActionResult(false, $"PID {pid} is a system process and is left alone.");
        if (pid == Environment.ProcessId) return new ActionResult(false, "That is StykkerHUD itself.");
        return null;
    }
}