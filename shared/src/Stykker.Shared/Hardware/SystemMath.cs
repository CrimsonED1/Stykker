namespace Stykker.Shared.Hardware;

// Die Umrechnung der Prozessor- und Speicherwerte ohne Windows-Aufrufe (testbar mit Testdaten).
public static class SystemMath
{
    public const double Gib = 1024.0 * 1024 * 1024;

    // Auslastung in Prozent aus zwei Abtastungen der Gesamtzeiten (GetSystemTimes). Die Kernelzeit enthält die
    // Leerlaufzeit, deshalb zählt die Summe aus Kernel und User als Gesamtzeit. null, wenn seit der letzten
    // Abtastung keine Zeit vergangen ist (dann bleibt der letzte Wert stehen, statt auf null zu springen).
    public static double? CpuPercent(long idle, long kernel, long user, long prevIdle, long prevKernel, long prevUser)
    {
        long dIdle = idle - prevIdle, dTotal = (kernel - prevKernel) + (user - prevUser);
        if (dTotal <= 0) return null;
        return Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
    }

    // Auslastung eines Kerns aus den Zeiten je Kern (Leerlauf und Gesamt, Gesamt enthält den Leerlauf).
    public static double CoreLoad(long totalDelta, long idleDelta) =>
        totalDelta > 0 ? Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100) : 0;

    // Die größten Programme nach Arbeitsspeicher (Working Set). Gleichnamige Prozesse werden zusammengefasst, Prozesse
    // mit PID 4 und darunter (System und Dienste des Kerns) bleiben weg. Ergebnis in GB, größte zuerst.
    public static List<(string Name, double Gb)> TopPrograms(IEnumerable<(int Pid, string Name, long WorkingSetBytes)> processes, int count)
    {
        var sums = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, name, bytes) in processes)
        {
            if (pid <= 4 || string.IsNullOrEmpty(name)) continue;
            sums[name] = sums.GetValueOrDefault(name) + bytes;
        }
        return sums.OrderByDescending(kv => kv.Value).Take(Math.Max(0, count))
                   .Select(kv => (kv.Key, kv.Value / Gib)).ToList();
    }
}
