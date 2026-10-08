using System.Globalization;

namespace StykkerLlm.Core;

// Model-Host (docs/plan-hosts-gateway.md, P1): ein PC ohne Oberfläche, der nur Modelle laufen lässt. Hier stehen die
// Texte, die der Host aus seiner Messung macht – Tooltip und Statuszeilen im Tray-Menü, Zeilen im Protokoll.
public static class HostStatus
{
    // Eigener Datenordner neben dem des Servers: %APPDATA%\StykkerLLM\host (eigene Sperre, eigene Einstellungen)
    public static AppPaths DefaultPaths() => new(Path.Combine(AppPaths.Default().Root, "host"));

    // Kurz für den Tooltip (höchstens 63 Zeichen): „StykkerHost · 2 running · 9.8/16 GB“
    public static string Tip(IReadOnlyList<ServerWatcher> servers, GpuSample? gpu)
    {
        var running = servers.Count(s => s.Online);
        var tip = $"{Strings.HostName} · {Strings.HostRunning(running)}";
        if (gpu != null) tip += string.Create(CultureInfo.InvariantCulture, $" · {gpu.MemUsedGb:0.0}/{gpu.MemTotalGb:0} GB");
        return tip.Length <= 63 ? tip : tip[..63];
    }

    // Zeilen fürs Tray-Menü (grau, nur zum Lesen): GPU, dann je Server Name und Tokens/s
    public static IReadOnlyList<string> Lines(IReadOnlyList<ServerWatcher> servers, GpuSample? gpu)
    {
        var lines = new List<string>();
        lines.Add(gpu != null
            ? string.Create(CultureInfo.InvariantCulture, $"{gpu.Name} · VRAM {gpu.MemUsedGb:0.0}/{gpu.MemTotalGb:0} GB · {gpu.Util:0} %")
            : Strings.HostNoGpu);
        if (servers.Count == 0) lines.Add(Strings.RunningEmpty);
        foreach (var s in servers.Take(8))
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{(s.Online ? "●" : "○")} {s.Name} :{s.Info.Port} · {s.Current:0.0} t/s"));
        if (servers.Count > 8) lines.Add($"+{servers.Count - 8}");
        return lines;
    }

    // Eine Zeile fürs Protokoll, wenn sich etwas ändert (Server dazu/weg)
    public static string Change(IEnumerable<string> before, IEnumerable<string> now)
    {
        var b = before.ToHashSet(StringComparer.Ordinal);
        var n = now.ToHashSet(StringComparer.Ordinal);
        var parts = n.Except(b).Select(x => "+" + x).Concat(b.Except(n).Select(x => "-" + x)).ToList();
        return parts.Count == 0 ? "" : "servers: " + string.Join(", ", parts);
    }
}
