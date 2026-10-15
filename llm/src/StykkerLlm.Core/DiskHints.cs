using System.Globalization;
using Stykker.Shared.Disk;

namespace StykkerLlm.Core;

// Ein Server für die Hinweise: sein Name, die PIDs (der Server selbst und seine Engines) und der Modellpfad. Der Pfad sagt,
// auf welchem Laufwerk das Modell liegt; Relative Pfade gelten ab dem Arbeitsordner.
public sealed record ServerIo(string Name, IReadOnlyList<int> Pids, string? ModelPath);

// Die Hinweise zu den Laufwerken für die Karte „Disks“. Ein Modellserver, der gerade viel liest (das Laden der Gewichte),
// wird mit Laufwerk genannt. Welches Laufwerk: zuerst der Modellpfad; ohne ihn nur dann, wenn genau ein Volume so viel liest
// wie der Server (sonst bleibt der Name weg, statt zu raten).
public static class DiskHints
{
    // Ab dieser Lesemenge nennt die Karte den Server (200 MB/s)
    public const double HeavyReadBytesPerSec = 200 * 1024.0 * 1024.0;

    // Die erkannten Server mit allen PIDs (der Server und seine Engines) und dem Modellpfad, relativ ab dem Arbeitsordner
    public static IReadOnlyList<ServerIo> ServersOf(IEnumerable<ServerWatcher> servers) =>
        servers.Select(s =>
        {
            var pids = new List<int>();
            if (s.Pid is int pid) pids.Add(pid);
            pids.AddRange(s.Info.Children.Select(c => c.Pid));
            var model = s.Info.ModelPath;
            string? full = string.IsNullOrEmpty(model) ? null : Path.IsPathRooted(model) ? model : Path.Combine(s.Info.WorkingDir ?? "", model);
            return new ServerIo(s.Name, pids, full);
        }).ToList();

    public static IReadOnlyList<string> Lines(IReadOnlyList<ServerIo> servers, IReadOnlyList<ProcessIoRow> processes,
        IReadOnlyList<VolumeRow> volumes, int max = 2)
    {
        var readByPid = new Dictionary<int, double>();
        foreach (var p in processes) readByPid[p.Pid] = readByPid.GetValueOrDefault(p.Pid) + p.ReadBytesPerSec;

        var lines = new List<(double Read, string Text)>();
        foreach (var s in servers)
        {
            double read = s.Pids.Distinct().Sum(pid => readByPid.GetValueOrDefault(pid));
            if (read <= HeavyReadBytesPerSec) continue;
            string rate = DiskFormat.Rate(read);
            string text = DriveFor(s.ModelPath, read, volumes) is { } drive
                ? Strings.DiskServerReads(s.Name, rate, drive)
                : Strings.DiskServerReadsAnywhere(s.Name, rate);
            lines.Add((read, text));
        }
        return lines.OrderByDescending(l => l.Read).Take(Math.Max(0, max)).Select(l => l.Text).ToList();
    }

    // Das Laufwerk mit Doppelpunkt ("C:"), auf dem das Modell liegt; null, wenn es nicht sicher ist
    public static string? DriveFor(string? modelPath, double read, IReadOnlyList<VolumeRow> volumes)
    {
        if (DriveOf(modelPath) is { } fromPath) return fromPath + ":";
        // Ohne Pfad: nur, wenn genau ein Volume den Großteil der Lesemenge trägt
        var busiest = volumes.Where(v => v.ReadBytesPerSec is double r && r > 0).OrderByDescending(v => v.ReadBytesPerSec).FirstOrDefault();
        if (busiest?.ReadBytesPerSec is double top && top >= 0.8 * read) return busiest.Letter + ":";
        return null;
    }

    // Der Buchstabe eines Pfads mit Laufwerk ("D:\models\x.gguf" → "D"); Netzpfade und Relatives → null
    public static string? DriveOf(string? path) =>
        !string.IsNullOrEmpty(path) && path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'
            ? char.ToUpperInvariant(path[0]).ToString()
            : null;
}

// Größen und Raten für die Anzeige, in Zweierpotenzen wie der Rest der Oberfläche: "1.2 GB/s", "340 MB/s", "12.0 KB/s"
public static class DiskFormat
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Rate(double bytesPerSec) => Size(bytesPerSec) + "/s";

    public static string Size(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes < 0) return "–";
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        string digits = v >= 100 || u == 0 ? "0" : "0.0";
        return v.ToString(digits, CultureInfo.InvariantCulture) + " " + Units[u];
    }
}
