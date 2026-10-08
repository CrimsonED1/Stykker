using System.Globalization;
using StykkerLlm.Core;

namespace StykkerLlm.Server.Components;

// Was eine Serverkarte zeigt (docs/plan-ui-redesign.md, U4), gleich für Server auf diesem PC (ServerWatcher) und auf
// einem Model-Host (RemoteServer aus dem Stand des Hosts). Ein Wert, den das Backend nicht meldet, bleibt null – die
// Karte zeigt dann ein gedämpftes „–“ an seinem festen Platz.
public sealed record CardSlot(int Id, string State, double Frac, double Tps, long Generated, int CtxUsed, int CtxMax, bool Full = false);

public sealed class ServerCardModel
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Model { get; init; } = "";
    public BackendKind Kind { get; init; }
    public int Port { get; init; }
    public string Url { get; init; } = "";
    public string? HostId { get; init; }          // null = dieser PC
    public string? HostName { get; init; }
    public bool Online { get; init; }
    public bool Loading { get; init; }
    public bool Manual { get; init; }             // von Hand eingetragen: nicht von StykkerLLM gestartet
    public double Current { get; init; }
    public double Peak { get; init; }
    public double Avg { get; init; }
    public long GeneratedTotal { get; init; }
    public double? VramGb { get; init; }
    public double? RamGb { get; init; }
    public double? CpuPercent { get; init; }
    public double? ModelFileGb { get; init; }
    public int? CtxPerSlot { get; init; }
    public int? Queue { get; init; }
    public double? DraftRate { get; init; }       // 0–1, null = kein spekulatives Decoding
    public string[] Clients { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CardSlot> Slots { get; init; } = Array.Empty<CardSlot>();
    public IReadOnlyList<LoadedModel> Models { get; init; } = Array.Empty<LoadedModel>();
    public double[] History { get; init; } = Array.Empty<double>();   // tokens/s, älteste zuerst
    public bool Recording { get; init; }
    public DateTime? RecordingSince { get; init; }
    public double? SpillGb { get; init; }
    public int? SlowerPct { get; init; }          // „langsamer als sonst“ (U11), null = kein Hinweis

    public bool IsLocal => HostId == null;
    // Ollama und LM Studio führen mehrere Modelle in einem Server: Tabelle der geladenen Modelle statt Slots
    public bool MultiModel => Kind is BackendKind.Ollama or BackendKind.LmStudio;
    public string Backend => BackendProbes.DisplayName(Kind);
    public string BackendIcon => Kind switch
    {
        BackendKind.Ollama => "b-ollama", BackendKind.LmStudio => "b-lms", BackendKind.Vllm => "b-vllm", _ => "b-llama",
    };
    public string BackendVar => Kind switch
    {
        BackendKind.Ollama => "--b-ollama", BackendKind.LmStudio => "--b-lms", BackendKind.Vllm => "--b-vllm", _ => "--b-llama",
    };

    // Zustand der Karte: aus, lädt, schreibt, liest den Prompt, wartet
    public string State =>
        !Online ? (Loading ? "load" : "off")
        : Loading ? "load"
        : Slots.Any(s => s.State == "gen") || (Slots.Count == 0 && Current > 0) ? "gen"
        : Slots.Any(s => s.State == "read") ? "read"
        : "idle";

    public int SlotsBusy => Slots.Count(s => s.State != "idle");

    public static ServerCardModel From(ServerWatcher s, RecordingSession? rec, HistoryEntry? usual = null)
    {
        var slots = s.Slots.Select(Slot).ToList();
        var hist = new double[s.HistoryCount];
        for (int i = 0; i < hist.Length; i++) hist[i] = s.HistoryAt(i);
        return new ServerCardModel
        {
            Key = s.Key, Name = s.Name, Model = s.Model, Kind = s.Kind, Port = s.Info.Port, Url = s.Url,
            Online = s.Online, Loading = s.Loading, Manual = s.Info.Manual,
            Current = s.Current, Peak = s.Peak, Avg = s.AverageActive(), GeneratedTotal = s.GeneratedTotal,
            VramGb = s.VramGb, RamGb = s.RamGb, CpuPercent = s.CpuPercent, ModelFileGb = s.ModelFileGb,
            CtxPerSlot = s.Slots.Count > 0 ? s.Slots.Max(v => v.CtxMax) : s.Props?.NCtx,
            Queue = s.QueueCount, DraftRate = s.SpecActive ? (double)s.Spec.Accepted / Math.Max(1, s.Spec.Drafted) : null,
            Clients = s.Clients, Slots = slots, Models = s.Models.ToList(), History = hist,
            Recording = rec != null, RecordingSince = rec?.Started,
            SpillGb = ServerWatcher.SpillGb(s.Models, s.SharedGb),
            SlowerPct = usual == null ? null : SpeedHint.SlowerPercent(s.AverageActive(), hist.Count(v => v > 0), usual.MeanTps, usual.TpsCount),
        };
    }

    public static ServerCardModel From(RemoteServer rs, string hostId, string hostName)
    {
        var slots = rs.Slots.Select(Slot).ToList();
        int n = Math.Clamp(rs.HistoryCount, 0, rs.History.Length);
        return new ServerCardModel
        {
            Key = rs.Key, Name = rs.Name, Model = rs.Model, Kind = KindOf(rs.Backend), Port = rs.Port, Url = rs.Url,
            HostId = hostId, HostName = hostName,
            Online = rs.Online, Loading = rs.Loading, Manual = rs.Manual,
            Current = rs.Current, Peak = rs.Peak, Avg = rs.AverageActive, GeneratedTotal = rs.GeneratedTotal,
            VramGb = rs.VramGb, RamGb = rs.RamGb, CpuPercent = rs.CpuPercent, ModelFileGb = rs.ModelFileGb,
            CtxPerSlot = rs.Slots.Count > 0 ? rs.Slots.Max(v => v.CtxMax) : rs.Props?.NCtx,
            Queue = rs.QueueCount,
            DraftRate = rs.SpecDrafted is int d && d > 0 && rs.SpecAccepted is int a ? (double)a / d : null,
            Clients = rs.Clients, Slots = slots, Models = rs.Models.ToList(), History = rs.History.Take(n).ToArray(),
            Recording = rs.Recording,
        };
    }

    // Eine Slot-Kachel; Full = Kontext fast voll (dieselbe Regel wie überall: CtxPressure)
    private static CardSlot Slot(SlotView v) => new(v.Id, SlotState(v.Busy, v.ReadingPrompt),
        v.Busy && v.CtxMax > 0 ? Math.Clamp((double)v.CtxUsed / v.CtxMax, 0, 1) : 0, v.Tps, v.Generated, v.CtxUsed, v.CtxMax,
        v.Busy && CtxPressure.IsNearlyFull(v));

    public static string SlotState(bool busy, bool reading) => !busy ? "idle" : reading ? "read" : "gen";

    public static BackendKind KindOf(string backend) => (backend ?? "").ToLowerInvariant() switch
    {
        "ollama" => BackendKind.Ollama, "lmstudio" or "lm studio" => BackendKind.LmStudio, "vllm" => BackendKind.Vllm, _ => BackendKind.LlamaCpp,
    };

    // ── Kurve: Punkte im 300×70-Rahmen, oben 10 % Luft (wie bisher im Monitor) ──
    private double Scale => Math.Max(10, Math.Ceiling(Math.Max(Peak, History.DefaultIfEmpty(0).Max()) / 10) * 10);

    public string SparkPoints()
    {
        int n = History.Length;
        if (n < 2) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++)
        {
            double x = 300.0 * (ServerWatcher.HistoryLength - n + i) / (ServerWatcher.HistoryLength - 1);
            double y = 68 - 64 * Math.Min(Scale, History[i]) / Scale;
            sb.Append(F(x)).Append(',').Append(F(y)).Append(' ');
        }
        return sb.ToString();
    }

    public string SparkArea()
    {
        int n = History.Length;
        if (n < 2) return "";
        double x0 = 300.0 * (ServerWatcher.HistoryLength - n) / (ServerWatcher.HistoryLength - 1);
        return $"{F(x0)},70 {SparkPoints()}300,70";
    }

    // Höhe des Leuchtpunkts am Ende in Prozent der Kurvenhöhe
    public string? SparkTipTop() => History.Length < 2 ? null : F((68 - 64 * Math.Min(Scale, History[^1]) / Scale) / 70 * 100);

    public double SparkMax => Scale;

    private static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
}
