using Stykker.Hardware;
using StykkerLlm.Core;

namespace StykkerLlm.Server;

// Macht aus dem Verlauf des Monitors (GPU, Laufwerke) den Vorlauf der Hardware-Seite. Beide Reihen werden vom Ende her
// zusammengelegt: der letzte GPU-Punkt zum letzten Laufwerkspunkt. Prozessor, Speicher und Netz führt der Monitor nicht,
// dort bleibt der Vorlauf leer (null). Die Zeitstempel sind Sekunden rückwärts ab jetzt; die Kurven zeichnen nach Index.
public static class HardwareSeed
{
    public static IReadOnlyList<HistoryPoint> From(GpuHistory gpu, DiskHistory disks)
    {
        var g = gpu.Points();
        var letters = disks.Letters();
        var series = letters.ToDictionary(l => l, disks.Of);
        int n = Math.Max(g.Count, series.Count == 0 ? 0 : series.Values.Max(s => s.Count));
        n = Math.Min(n, HardwareSampler.HistoryLength - 1);
        var now = DateTime.Now;
        var points = new List<HistoryPoint>(n);
        for (int back = n; back >= 1; back--)
        {
            GpuPoint? gp = g.Count >= back ? g[g.Count - back] : null;
            var volumes = new Dictionary<string, VolumeRate>(StringComparer.OrdinalIgnoreCase);
            foreach (var (letter, list) in series)
                if (list.Count >= back) { var d = list[list.Count - back]; volumes[letter] = new VolumeRate(d.Read, d.Write, d.Busy); }
            points.Add(new HistoryPoint(now.AddSeconds(-back), null, null, gp?.Util, gp?.MemUsedGb, gp?.PowerW, gp?.TempC,
                null, null, volumes));
        }
        return points;
    }
}
