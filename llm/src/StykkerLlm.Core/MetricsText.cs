using System.Globalization;
using System.Text;

namespace StykkerLlm.Core;

// Eigener Metrik-Endpunkt in Prometheus-Text, damit Starship, waybar, Grafana oder ein eigenes
// Skript ohne Umweg über das JSON des Servers an die Zahlen kommen.
//
// Format wie überall: "# HELP name Text", "# TYPE name gauge", "name{label="…"} wert". Pro Prozess und pro Slot gibt
// es **Labels** – ohne sie hätte dieselbe Zeile mehrere Werte, und Prometheus würde die als Fehler ablehnen.
// Loopback ist Sache des Servers (Program.cs), nicht hier: die Werte selbst sind harmlos.
public static class MetricsText
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static string Write(MonitorEngine e, DateTimeOffset now, IReadOnlyList<(string Name, double Gb)>? vramTop = null,
        IReadOnlyList<(string Name, double Gb)>? ramTop = null, IReadOnlyList<(string Name, double Percent)>? gpuUtilTop = null)
    {
        var sb = new StringBuilder(8192);

        Gauge(sb, "stykker_up", 1, "1, solange der Server misst (Schema 1).");
        Gauge(sb, "stykker_ticks_total", e.Ticks, "Messdurchläufe seit dem Start.");
        Gauge(sb, "stykker_servers", e.Servers.Count, "Erkannte Server insgesamt.");
        Gauge(sb, "stykker_servers_online", e.Servers.Count(s => s.Online), "Davon gerade erreichbar.");
        Gauge(sb, "stykker_servers_busy", e.Servers.Count(s => s.Online && s.Current > 0.05), "Davon erzeugen gerade Token.");

        if (e.Gpu is { } g)
        {
            Gauge(sb, "stykker_gpu_utilization_percent", g.Util, "GPU-Auslastung in Prozent.");
            Gauge(sb, "stykker_gpu_vram_used_bytes", g.MemUsedGb * G, "VRAM belegt.");
            Gauge(sb, "stykker_gpu_vram_total_bytes", g.MemTotalGb * G, "VRAM gesamt.");
            Gauge(sb, "stykker_gpu_temperature_celsius", g.TempC, "GPU-Temperatur.");
            Gauge(sb, "stykker_gpu_power_watts", g.PowerW, "GPU-Leistungsaufnahme.");
            Gauge(sb, "stykker_gpu_throttled", g.Throttled ? 1 : 0, "1, wenn die Karte gedrosselt wird.");
        }
        if (e.Sys is { } s)
        {
            Gauge(sb, "stykker_system_cpu_percent", s.CpuPercent, "Systemlast in Prozent.");
            Gauge(sb, "stykker_system_ram_used_bytes", s.RamUsedGb * G, "RAM belegt.");
            Gauge(sb, "stykker_system_ram_total_bytes", s.RamTotalGb * G, "RAM gesamt.");
        }
        foreach (var t in vramTop ?? Array.Empty<(string, double)>())
            Gauge(sb, "stykker_process_vram_bytes", t.Gb * G, "VRAM je Prozess (die größten Posten).", $"process=\"{Label(t.Name)}\"");
        foreach (var t in ramTop ?? Array.Empty<(string, double)>())
            Gauge(sb, "stykker_process_ram_bytes", t.Gb * G, "RAM je Programm (die größten Posten).", $"process=\"{Label(t.Name)}\"");
        foreach (var t in gpuUtilTop ?? Array.Empty<(string, double)>())
            Gauge(sb, "stykker_process_gpu_utilization_percent", t.Percent, "GPU-Auslastung je Prozess.", $"process=\"{Label(t.Name)}\"");

        foreach (var sv in e.Servers) WriteServer(sb, sv);
        return sb.ToString();
    }

    private const double G = 1073741824.0;

    private static void WriteServer(StringBuilder sb, ServerWatcher s)
    {
        // Ein Server wird über sein Label adressiert (host_port) – der Name steht im HELP, der Schlüssel ist eindeutig.
        var id = $"server=\"{Slug(s)}\"";
        var help = $"{s.Name} ({s.Url}): erreicht, Tokens/s, Slots und Kontext.";
        Gauge(sb, "stykker_server_online", s.Online ? 1 : 0, help, id);
        Gauge(sb, "stykker_server_tps", s.Current, help, id);
        Gauge(sb, "stykker_server_peak_tps", s.Peak, help, id);
        Gauge(sb, "stykker_server_slots", s.Slots.Count, help, id);
        Gauge(sb, "stykker_server_slots_busy", s.Slots.Count(x => x.Busy), help, id);
        Gauge(sb, "stykker_server_queue", s.QueueCount ?? 0, help, id);
        Gauge(sb, "stykker_server_slots_max_tokens", s.Slots.Count == 0 ? 0 : s.Slots.Max(x => (long)x.CtxMax), help, id);
        if (s.VramGb is double vram) Gauge(sb, "stykker_server_vram_bytes", vram * G, help, id);
        if (s.RamGb is double ram) Gauge(sb, "stykker_server_ram_bytes", ram * G, help, id);
        if (s.CpuPercent is double cpu) Gauge(sb, "stykker_server_cpu_percent", cpu, help, id);
        if (s.ModelFileGb is double file) Gauge(sb, "stykker_server_model_file_bytes", file * G, help, id);
        if (s.SpecActive)
        {
            Gauge(sb, "stykker_server_spec_draft_tokens_total", s.Spec.Drafted, $"{s.Name}: spekulativ entworfene Token.", id);
            Gauge(sb, "stykker_server_spec_accepted_tokens_total", s.Spec.Accepted, $"{s.Name}: davon angenommen.", id);
        }

        foreach (var sl in s.Slots)
        {
            var sid = $"{id},slot=\"{sl.Id}\"";
            var sh = $"{s.Name}, Slot {sl.Id}: Zustand, Tokens/s und Kontext.";
            Gauge(sb, "stykker_slot_busy", sl.Busy ? 1 : 0, sh, sid);
            Gauge(sb, "stykker_slot_tps", sl.Tps, sh, sid);
            Gauge(sb, "stykker_slot_generated_tokens", sl.Generated, sh, sid);
            Gauge(sb, "stykker_slot_context_used", sl.CtxUsed, sh, sid);
            Gauge(sb, "stykker_slot_context_max", sl.CtxMax, sh, sid);
            Gauge(sb, "stykker_slot_context_almost_full", CtxPressure.IsNearlyFull(sl) ? 1 : 0, "1 ab 90 % Belegung.", sid);
        }
    }

    // Prometheus-Namen: Buchstaben, Ziffern, Unterstrich, Doppelpunkt. Alles andere wird zu "_".
    internal static string Slug(ServerWatcher s)
    {
        var raw = $"{s.Info.Host}_{s.Info.Port}";
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or ':' ? c : '_');
        return sb.ToString();
    }

    // Label-Wert: Anführungszeichen, Backslash und Zeilenumbrüche wären sonst eine kaputte Zeile
    private static string Label(string? value)
    {
        var s = (value ?? "?").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");
        return s.Length > 60 ? s[..60] : s;
    }

    private static void Gauge(StringBuilder sb, string name, double value, string help, string? labels = null)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(OneLine(help)).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" gauge\n");
        sb.Append(name);
        if (!string.IsNullOrEmpty(labels)) sb.Append('{').Append(labels).Append('}');
        sb.Append(' ').Append(N(value)).Append('\n');
    }

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ");

    private static string N(double v) => double.IsFinite(v) ? v.ToString("0.##", CultureInfo.InvariantCulture) : "0";
}