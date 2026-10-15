using System.Diagnostics;

namespace Stykker.Shared.Windows;

// Der Programmname zu einer PID, für die Zeilen der Grafik- und Speicherlisten. Die PIDs werden wiederverwendet, deshalb
// vergisst der Speicher seine Namen alle fünf Minuten. Ist der Prozess nicht mehr da, steht dort „PID <n>".
public sealed class ProcessNames
{
    private readonly Dictionary<int, string> _names = new();
    private DateTime _namesAt = DateTime.Now;

    public string NameOf(int pid)
    {
        if ((DateTime.Now - _namesAt).TotalMinutes > 5) { _names.Clear(); _namesAt = DateTime.Now; }
        if (_names.TryGetValue(pid, out var n)) return n;
        try { using var pr = Process.GetProcessById(pid); n = pr.ProcessName; } catch { n = $"PID {pid}"; }
        if (_names.Count > 200) _names.Clear();
        return _names[pid] = n;
    }
}
