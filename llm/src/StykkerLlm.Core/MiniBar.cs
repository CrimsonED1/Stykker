using Stykker.Shared.Net;
using System.Globalization;
using Stykker.Shared.Disk;

namespace StykkerLlm.Core;

// Die Hardware-Minibar oben auf jeder Seite außer dem Monitor (Entwurf: docs/design/minibar-preview.svg):
// acht Felder in zwei Reihen – VRAM, GPU, CPU, RAM, Laufwerk, Platten-E/A, Netzwerk, Temperatur.
// Hier steht nur, was angezeigt wird; die Seite (HwMiniBar.razor) zeichnet die Felder nur noch.
public enum MiniLevel { Normal, Good, Warn, Hot }

// Fraction 0..1 für den Balken; null = kein Maßstab bekannt (der Balken bleibt leer). Value "–", wenn der Wert fehlt.
public sealed record MiniCell(string Key, string Label, string Value, double? Fraction, MiniLevel Level, string Tip)
{
    public bool Missing => Value == MiniBar.None;
    public string? Href => MiniBar.Href(Key);
}

// Der Verlauf der Balken: je Feld die letzten Anteile (ein Eintrag je Messtakt), für die feine Linie hinter dem Feld.
// Ein fehlender Anteil bleibt als Lücke stehen, damit die Zeitachse aller Felder gleich lang ist.
public sealed class MiniHistory
{
    public const int Length = 60;
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<double?>> _series = new(StringComparer.Ordinal);

    public void Add(IEnumerable<MiniCell> cells)
    {
        lock (_gate)
        {
            foreach (var c in cells)
            {
                if (!_series.TryGetValue(c.Key, out var q)) _series[c.Key] = q = new Queue<double?>();
                q.Enqueue(c.Missing ? null : c.Fraction);
                while (q.Count > Length) q.Dequeue();
            }
        }
    }

    // Ältester zuerst (eine Kopie)
    public IReadOnlyList<double?> Of(string key)
    {
        lock (_gate) return _series.TryGetValue(key, out var q) ? q.ToArray() : Array.Empty<double?>();
    }

    // Der höchste Anteil der Reihe, null ohne Messwert
    public static double? Peak(IReadOnlyList<double?> values)
    {
        double? best = null;
        foreach (var v in values)
            if (v is double d && double.IsFinite(d) && (best == null || d > best)) best = d;
        return best;
    }
}

public static class MiniBar
{
    public const string None = "–";
    // ab hier wird ein Balken rot (wie hw-bar.hot im Design-System)
    public const double HotFrac = 0.92;
    // GPU-Temperatur: grün bis 74 °C, gelb ab 75 °C, rot ab 85 °C; der Balken reicht bis 90 °C
    public const double TempWarnC = 75, TempHotC = 85, TempScaleC = 90;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Auf dem Telefon zugeklappt stehen nur diese drei: der Speicher, die Last und die Hitze der Grafikkarte
    public static readonly IReadOnlyList<string> PhoneKeys = new[] { "vram", "gpu", "temp" };

    // Wohin ein Klick auf das Feld führt: der passende Abschnitt der Hardware-Seite (Stykker.Hardware)
    public static string? Href(string key) => key switch
    {
        "vram" or "gpu" or "temp" => "hardware#gpu",
        "cpu" or "ram" => "hardware#cpu",
        "disk" or "io" => "hardware#drives",
        "net" => "hardware#network",
        _ => null,
    };

    public static IReadOnlyList<MiniCell> Build(GpuSample? gpu, SystemSample? sys, DiskSample? disks, NetSample? net) =>
        new[] { Vram(gpu), GpuLoad(gpu), Cpu(sys), Ram(sys), Disk(disks), DiskIo(disks), Network(net), Temp(gpu) };

    private static MiniCell Missing(string key, string label, string tip) => new(key, label, None, null, MiniLevel.Normal, tip);

    private static MiniLevel ByFraction(double f) => f > HotFrac ? MiniLevel.Hot : MiniLevel.Normal;

    public static MiniCell Vram(GpuSample? g)
    {
        if (g == null || g.MemTotalGb <= 0) return Missing("vram", Strings.MiniVram, Strings.MiniNoGpu);
        double f = Math.Clamp(g.MemUsedGb / g.MemTotalGb, 0, 1);
        return new("vram", Strings.MiniVram, $"{g.MemUsedGb.ToString("0.0", Inv)} / {g.MemTotalGb.ToString("0", Inv)} GB", f, ByFraction(f),
            Strings.MiniVramTip(g.MemUsedGb, g.MemTotalGb, g.Name));
    }

    public static MiniCell GpuLoad(GpuSample? g)
    {
        if (g == null) return Missing("gpu", Strings.MiniGpu, Strings.MiniNoGpu);
        double f = Math.Clamp(g.Util / 100, 0, 1);
        return new("gpu", Strings.MiniGpu, g.Util.ToString("0", Inv) + "%", f, ByFraction(f), g.Name);
    }

    public static MiniCell Cpu(SystemSample? s)
    {
        if (s == null) return Missing("cpu", Strings.MiniCpu, Strings.MiniNoSystem);
        double f = Math.Clamp(s.CpuPercent / 100, 0, 1);
        return new("cpu", Strings.MiniCpu, s.CpuPercent.ToString("0", Inv) + "%", f, ByFraction(f), Strings.LogicalCores(s.Cores));
    }

    public static MiniCell Ram(SystemSample? s)
    {
        if (s == null || s.RamTotalGb <= 0) return Missing("ram", Strings.MiniRam, Strings.MiniNoSystem);
        double free = Math.Max(0, s.RamTotalGb - s.RamUsedGb);
        return new("ram", Strings.MiniRam, Strings.MiniGbFree(free), s.RamFrac, ByFraction(s.RamFrac),
            Strings.MiniRamTip(s.RamUsedGb, s.RamTotalGb));
    }

    // Das vollste Laufwerk: dort läuft man zuerst auf (Modelle, Auslagerungsdatei, Aufnahmen)
    public static MiniCell Disk(DiskSample? d)
    {
        var v = d?.Volumes.Where(x => x.TotalBytes > 0).OrderByDescending(x => x.UsedBytes / x.TotalBytes).FirstOrDefault();
        if (v == null) return Missing("disk", Strings.MiniDisk, Strings.MiniNoDisks);
        double f = Math.Clamp(v.UsedBytes / v.TotalBytes, 0, 1);
        return new("disk", Strings.MiniDisk, $"{v.Letter}: {(f * 100).ToString("0", Inv)}%", f, ByFraction(f),
            Strings.MiniDiskTip(v.Letter, DiskFormat.Size(v.FreeBytes), DiskFormat.Size(v.TotalBytes)));
    }

    // Lesen / Schreiben über alle physischen Laufwerke; der Balken zeigt das am stärksten ausgelastete Laufwerk
    public static MiniCell DiskIo(DiskSample? d)
    {
        var rows = d?.Disks ?? (IReadOnlyList<PhysicalDiskRow>)Array.Empty<PhysicalDiskRow>();
        var read = rows.Where(x => x.ReadBytesPerSec != null).Select(x => x.ReadBytesPerSec!.Value).ToList();
        var write = rows.Where(x => x.WriteBytesPerSec != null).Select(x => x.WriteBytesPerSec!.Value).ToList();
        if (read.Count == 0 && write.Count == 0) return Missing("io", Strings.MiniIo, d == null ? Strings.MiniNoDisks : Strings.DisksNoRates);
        double r = read.Sum(), w = write.Sum();
        var busy = rows.Where(x => x.BusyPercent != null).Select(x => x.BusyPercent!.Value).DefaultIfEmpty(-1).Max();
        double? f = busy >= 0 ? Math.Clamp(busy / 100, 0, 1) : null;
        return new("io", Strings.MiniIo, Pair(r, w), f, f is double ff ? ByFraction(ff) : MiniLevel.Normal,
            Strings.MiniIoTip(DiskFormat.Rate(r), DiskFormat.Rate(w), busy >= 0 ? busy : null));
    }

    // Empfangen / Senden; der Balken misst das größere gegen die Verbindungsgeschwindigkeit (falls bekannt)
    public static MiniCell Network(NetSample? n)
    {
        if (n == null) return Missing("net", Strings.MiniNet, Strings.MiniNoNet);
        double? f = n.LinkBytesPerSec > 0 ? Math.Clamp(Math.Max(n.RxBytesPerSec, n.TxBytesPerSec) / n.LinkBytesPerSec, 0, 1) : null;
        return new("net", Strings.MiniNet, Pair(n.RxBytesPerSec, n.TxBytesPerSec), f, f is double ff ? ByFraction(ff) : MiniLevel.Normal,
            Strings.MiniNetTip(DiskFormat.Rate(n.RxBytesPerSec), DiskFormat.Rate(n.TxBytesPerSec), n.LinkBytesPerSec > 0 ? LinkText(n.LinkBytesPerSec) : null));
    }

    public static MiniCell Temp(GpuSample? g)
    {
        if (g == null || g.TempC <= 0) return Missing("temp", Strings.MiniTemp, Strings.MiniNoGpu);
        var level = g.TempC >= TempHotC ? MiniLevel.Hot : g.TempC >= TempWarnC ? MiniLevel.Warn : MiniLevel.Good;
        return new("temp", Strings.MiniTemp, g.TempC.ToString("0", Inv) + " °C", Math.Clamp(g.TempC / TempScaleC, 0, 1), level,
            g.Throttled ? Strings.GpuClocksLimited + ": " + g.ThrottleText() : Strings.GpuClocksFree);
    }

    // Zwei Raten in einer Einheit („10.6 / 23.8 MB/s“), damit die Zahlen sich vergleichen lassen
    public static string Pair(double a, double b)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double max = Math.Max(Math.Max(a, b), 0);
        int u = 0;
        double div = 1;
        while (max / div >= 1024 && u < units.Length - 1) { div *= 1024; u++; }
        return $"{Part(a, div, u)} / {Part(b, div, u)} {units[u]}/s";
    }

    // Ein Wert, der in der gemeinsamen Einheit auf 0.0 fiele, aber nicht 0 ist, steht als „<0.1“ – sonst sähe ein kleines
    // Schreiben neben großem Lesen aus wie gar keins
    private static string Part(double v, double div, int unit)
    {
        double x = Math.Max(v, 0) / div;
        if (unit == 0) return x.ToString("0", Inv);
        return x > 0 && x < 0.05 ? "<0.1" : x.ToString("0.0", Inv);
    }

    // Verbindungsgeschwindigkeit wie auf dem Adapter: 1 Gbit/s, 2.5 Gbit/s, 100 Mbit/s
    public static string LinkText(double bytesPerSec)
    {
        double bits = bytesPerSec * 8;
        return bits >= 1e9 ? (bits / 1e9).ToString("0.#", Inv) + " Gbit/s" : (bits / 1e6).ToString("0", Inv) + " Mbit/s";
    }
}
