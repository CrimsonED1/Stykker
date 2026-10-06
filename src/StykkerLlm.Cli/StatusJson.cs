using System.Text.Encodings.Web;
using System.Text.Json;
using StykkerLlm.Core;

namespace StykkerLlm.Cli;

// "stykker status --json": stabile Schnittstelle für Statusleisten und Skripte. Felder nur hinzufügen, nie umbenennen oder
// entfernen; eine inkompatible Änderung erhöht "schema". Werte, die nicht messbar sind, stehen als null da.
// Ohne Reflection geschrieben (Utf8JsonWriter): später NativeAOT-tauglich.
public static class StatusJson
{
    public const int Schema = 1;

    public static void Write(Stream stream, MonitorEngine e, bool limited, bool indented, DateTimeOffset now)
    {
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        w.WriteStartObject();
        w.WriteNumber("schema", Schema);
        w.WriteString("time", now.ToString("yyyy-MM-ddTHH:mm:sszzz"));   // Ortszeit mit Versatz
        w.WriteBoolean("simulated", e.IsSimulated);
        w.WriteBoolean("limited", limited);

        if (e.Gpu is { } g)
        {
            w.WriteStartObject("gpu");
            w.WriteString("name", g.Name);
            w.WriteNumber("util", Math.Round(g.Util, 1));
            w.WriteNumber("vramUsedGb", Math.Round(g.MemUsedGb, 2));
            w.WriteNumber("vramTotalGb", Math.Round(g.MemTotalGb, 2));
            w.WriteNumber("vramFreeGb", Math.Round(g.MemFreeGb, 2));
            w.WriteNumber("powerW", Math.Round(g.PowerW, 1));
            w.WriteNumber("tempC", Math.Round(g.TempC, 0));
            w.WriteBoolean("throttled", g.Throttled);
            w.WriteString("throttle", g.ThrottleText());
            w.WriteEndObject();
        }
        else w.WriteNull("gpu");

        if (e.Sys is { } s)
        {
            w.WriteStartObject("system");
            w.WriteNumber("cpu", Math.Round(s.CpuPercent, 1));
            w.WriteNumber("cores", s.Cores);
            w.WriteNumber("ramUsedGb", Math.Round(s.RamUsedGb, 2));
            w.WriteNumber("ramTotalGb", Math.Round(s.RamTotalGb, 2));
            w.WriteEndObject();
        }
        else w.WriteNull("system");

        w.WriteStartArray("servers");
        foreach (var sv in e.Servers) WriteServer(w, sv);
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
    }

    private static void WriteServer(Utf8JsonWriter w, ServerWatcher s)
    {
        w.WriteStartObject();
        w.WriteString("name", s.Name);
        w.WriteString("key", s.Key);
        w.WriteString("url", s.Url);
        w.WriteString("backend", Commands.BackendName(s.Kind));
        WriteStringOrNull(w, "version", s.BackendVersion);
        WriteStringOrNull(w, "model", s.Model is "–" or "" ? null : s.Model);
        w.WriteString("state", Commands.State(s));
        w.WriteBoolean("online", s.Online);
        w.WriteBoolean("manual", s.Info.Manual);
        if (s.Pid is int pid) w.WriteNumber("pid", pid); else w.WriteNull("pid");
        w.WriteNumber("tps", Math.Round(s.Current, 1));
        w.WriteNumber("peakTps", Math.Round(s.Peak, 1));
        WriteNumberOrNull(w, "vramGb", s.VramGb);
        WriteNumberOrNull(w, "ramGb", s.RamGb);
        WriteNumberOrNull(w, "cpu", s.CpuPercent);
        w.WriteStartArray("slots");
        foreach (var sl in s.Slots)
        {
            w.WriteStartObject();
            w.WriteNumber("id", sl.Id);
            w.WriteBoolean("busy", sl.Busy);
            w.WriteBoolean("prompt", sl.ReadingPrompt);
            w.WriteNumber("tps", Math.Round(sl.Tps, 1));
            w.WriteNumber("ctxUsed", sl.CtxUsed);
            w.WriteNumber("ctxMax", sl.CtxMax);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("models");   // Ollama / LM Studio: geladene Modelle
        foreach (var m in s.Models)
        {
            w.WriteStartObject();
            w.WriteString("name", m.Name);
            w.WriteNumber("vramGb", Math.Round(m.VramBytes / 1073741824.0, 2));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("clients");
        foreach (var c in s.Clients) w.WriteStringValue(c);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteStringOrNull(Utf8JsonWriter w, string name, string? v)
    {
        if (v == null) w.WriteNull(name); else w.WriteString(name, v);
    }

    private static void WriteNumberOrNull(Utf8JsonWriter w, string name, double? v)
    {
        if (v is double d && double.IsFinite(d)) w.WriteNumber(name, Math.Round(d, 2)); else w.WriteNull(name);
    }
}
