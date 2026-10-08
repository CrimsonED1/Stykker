using System.Text.Json;

namespace StykkerLlm.Core;

// Die Hosts als JSON (/api/hosts, später im Zustand für Web und TUI): ohne Token, nur was man sehen darf
public static class HostStateJson
{
    public static string Write(IEnumerable<HostLive> hosts, DateTime now)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartArray("hosts");
            foreach (var h in hosts)
            {
                w.WriteStartObject();
                w.WriteString("id", h.Entry.Id);
                w.WriteString("name", h.Name.Length > 0 ? h.Name : h.Entry.Name);
                w.WriteBoolean("connected", h.Connected);
                if (h.Since is { } since) w.WriteString("since", since.ToString("o"));
                if (h.LastSeen is { } seen) w.WriteNumber("secondsAgo", Math.Max(0, (int)(now - seen).TotalSeconds));
                w.WriteString("address", h.Address);
                w.WriteString("version", h.Version);
                if (h.State?.Gpu is { } g)
                {
                    w.WriteString("gpu", g.Name);
                    w.WriteNumber("vramUsedGb", Math.Round(g.MemUsedGb, 1));
                    w.WriteNumber("vramTotalGb", Math.Round(g.MemTotalGb, 1));
                }
                w.WriteNumber("servers", h.State?.Servers.Count ?? 0);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
