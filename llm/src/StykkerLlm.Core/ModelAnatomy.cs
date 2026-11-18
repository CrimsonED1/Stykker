using System.Globalization;
using System.Text.RegularExpressions;

namespace StykkerLlm.Core;

// Der Aufbau eines geladenen Modells für die Ansicht „Model“ (Detailseite eines Servers): Schichten, Experten (MoE) und
// wo jede Schicht liegt – Grafikkarte oder Prozessor. Alles kommt aus dem GGUF-Kopf und der Kommandozeile; llama.cpp
// meldet die Verteilung nicht selbst. Welche Experten gerade rechnen, meldet es auch nicht: die Ansicht zeigt deshalb nur
// den Anteil (k von n je Token), keine erfundene Auswahl.
public enum LayerPlace { Gpu, Cpu, Unknown }

// Eine Schicht: Attention (und alles außer den gerouteten Experten) und die Experten getrennt, weil --cpu-moe,
// --n-cpu-moe und -ot genau die Experten auf den Prozessor legen und den Rest auf der Grafikkarte lassen
public sealed record LayerView(int Index, bool Moe, LayerPlace Main, LayerPlace Experts);

public sealed record ModelAnatomy(
    string Architecture, string? Name, string Quantization, long Parameters, long ExpertParameters,
    int? ExpertCount, int? ExpertUsed, int? ExpertShared, IReadOnlyList<LayerView> Layers, LayerPlace Output,
    bool PlacementKnown, IReadOnlyList<string> Notes)
{
    public bool IsMoe => ExpertCount is > 1 && ExpertUsed is > 0 && Layers.Any(l => l.Moe);
    public int MoeLayers => Layers.Count(l => l.Moe);

    // Je Token arbeiten alle dichten Gewichte und k von n Experten (die geteilten zählen zu den dichten)
    public long ActiveParameters => IsMoe && ExpertParameters > 0
        ? Parameters - ExpertParameters + (long)Math.Round(ExpertParameters * (double)ExpertUsed!.Value / ExpertCount!.Value)
        : Parameters;

    public int MainOnGpu => Layers.Count(l => l.Main == LayerPlace.Gpu);
    public int ExpertsOnCpu => Layers.Count(l => l.Moe && l.Experts == LayerPlace.Cpu);
}

public static class ModelAnatomyBuilder
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    // args null: Kommandozeile unbekannt (z. B. LM Studio, entfernter Host) – die Verteilung bleibt dann unbekannt
    public static ModelAnatomy Build(GgufInfo g, LlamaServerArgs? args)
    {
        int n = Math.Max(0, g.BlockCount ?? 0);
        var notes = new List<string>();
        bool moeModel = g.IsMoe;
        int dense = Math.Max(0, g.LeadingDenseBlockCount ?? 0);
        int step = (int?)g.Int($"{g.Architecture}.interleave_moe_layer_step") is int s and > 1 ? s : 1;

        // -ngl: die Schichten vom Ende her auf die Grafikkarte; die Ausgabeschicht (Index n) kommt zuerst dran
        var (gpuStart, outputPlace, known) = Offload(args?.NglRaw, n);
        if (args == null) notes.Add(Strings.AnatNoArgs);
        else if (!known) notes.Add(Strings.AnatNglAuto);

        var rules = ExpertRules(args, notes);
        var layers = new List<LayerView>(n);
        for (int i = 0; i < n; i++)
        {
            bool moe = moeModel && i >= dense && (step == 1 || (i + 1) % step == 0);
            LayerPlace main = !known ? LayerPlace.Unknown : i >= gpuStart ? LayerPlace.Gpu : LayerPlace.Cpu;
            // Attention-Tensoren kann -ot ebenfalls verschieben
            main = Override(rules, $"blk.{i}.attn_q.weight") ?? main;
            LayerPlace experts = main;
            if (moe) experts = Override(rules, $"blk.{i}.ffn_up_exps.weight") ?? main;
            layers.Add(new LayerView(i, moe, main, moe ? experts : main));
        }
        return new ModelAnatomy(g.Architecture, g.Name, g.Quantization, g.ParameterCount, g.ExpertParameterCount,
            g.ExpertCount, g.ExpertUsedCount, g.ExpertSharedCount, layers, outputPlace, known, notes);
    }

    // (erste Schicht auf der GPU, Ausgabeschicht, bekannt). Ohne -ngl oder mit „auto“ entscheidet llama.cpp selbst.
    public static (int GpuStart, LayerPlace Output, bool Known) Offload(string? nglRaw, int layers)
    {
        if (string.IsNullOrWhiteSpace(nglRaw) || nglRaw.Equals("auto", StringComparison.OrdinalIgnoreCase) || nglRaw == "-1")
            return (0, LayerPlace.Unknown, false);
        if (nglRaw.Equals("all", StringComparison.OrdinalIgnoreCase) || nglRaw == "-2") return (0, LayerPlace.Gpu, true);
        if (!int.TryParse(nglRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ngl) || ngl < 0)
            return (0, LayerPlace.Unknown, false);
        int start = Math.Max(layers + 1 - ngl, 0);
        return (start, ngl >= 1 ? LayerPlace.Gpu : LayerPlace.Cpu, true);
    }

    // Die Regeln für Tensoren in der Reihenfolge, in der llama.cpp sie prüft (die erste passende gewinnt):
    // erst -ot, dann --cpu-moe bzw. --n-cpu-moe (dasselbe Muster wie in llama.cpp, common/arg.cpp)
    private static List<(Regex Re, LayerPlace Place)> ExpertRules(LlamaServerArgs? args, List<string> notes)
    {
        var list = new List<(Regex, LayerPlace)>();
        if (args == null) return list;
        foreach (var (pattern, buffer) in args.OverrideTensors)
        {
            try { list.Add((new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout), PlaceOf(buffer))); }
            catch (ArgumentException) { notes.Add(Strings.AnatBadOverride(pattern)); }
        }
        const string exps = @"\.ffn_(up|down|gate|gate_up)_(ch|)exps";
        if (args.CpuMoe) list.Add((new Regex(exps, RegexOptions.CultureInvariant, RegexTimeout), LayerPlace.Cpu));
        if (args.NCpuMoe is int k and > 0)
            for (int i = 0; i < k; i++) list.Add((new Regex($@"blk\.{i}{exps}", RegexOptions.CultureInvariant, RegexTimeout), LayerPlace.Cpu));
        return list;
    }

    // „CPU“, „CPU_REPACK“, „CPU_AARCH64“ = Prozessor; alles andere (CUDA0, Vulkan1, ROCm0, Metal) = ein Grafikgerät
    private static LayerPlace PlaceOf(string buffer) =>
        buffer.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) ? LayerPlace.Cpu : LayerPlace.Gpu;

    private static LayerPlace? Override(List<(Regex Re, LayerPlace Place)> rules, string tensor)
    {
        foreach (var (re, place) in rules)
        {
            try { if (re.IsMatch(tensor)) return place; }
            catch (RegexMatchTimeoutException) { }
        }
        return null;
    }

    // Welche Felder im Expertenraster leuchten: genau k von n, gleichmäßig verstreut (je ein Feld aus k gleich großen
    // Abschnitten, darin versetzt). Nur ein Bild für den Anteil – welche Experten der Router wählt, ist unbekannt.
    public static bool[] LitExperts(int n, int k)
    {
        var lit = new bool[Math.Max(0, n)];
        if (n <= 0 || k <= 0) return lit;
        k = Math.Min(k, n);
        for (int j = 0; j < k; j++)
        {
            int from = (int)((long)j * n / k), to = (int)((long)(j + 1) * n / k);
            int span = Math.Max(1, to - from);
            lit[from + (j * 7 + 3) % span] = true;
        }
        return lit;
    }

    // Spalten des Expertenrasters: bis 16 in einer Reihe, bis 64 in Reihen zu 16, darüber 32 (128 → vier Reihen)
    public static int ExpertColumns(int n) => n <= 16 ? Math.Max(1, n) : n <= 64 ? 16 : 32;

    // Parameter kurz: 30.5 B, 3.3 B, 820 M
    public static string ParamText(long p) =>
        p >= 1_000_000_000 ? (p / 1e9).ToString(p >= 100_000_000_000 ? "0" : "0.0", CultureInfo.InvariantCulture) + " B"
        : (p / 1e6).ToString("0", CultureInfo.InvariantCulture) + " M";
}

// Was ein Slot gerade tut, aus /slots abgeleitet (llama.cpp meldet nur is_processing; den Rest liefern die Zähler)
public enum SlotPhase { Idle, Reading, Writing }

public sealed record SlotActivity(int Id, SlotPhase Phase, double ReadFraction, int PromptDone, int PromptCached, double Tps,
    int Generated, int MaxTokens, int CtxUsed, int CtxMax)
{
    public double CtxFraction => CtxMax > 0 ? Math.Clamp((double)CtxUsed / CtxMax, 0, 1) : 0;

    // Der Balken der Zeile: beim Lesen der Fortschritt, beim Schreiben erzeugt / Grenze (ohne Grenze der Kontext), sonst der Kontext
    public double BarFraction => Phase switch
    {
        SlotPhase.Reading => ReadFraction,
        SlotPhase.Writing when MaxTokens > 0 => Math.Clamp((double)Generated / MaxTokens, 0, 1),
        _ => CtxFraction,
    };

    public static SlotActivity Of(SlotView s)
    {
        var phase = !s.Busy ? SlotPhase.Idle : s.ReadingPrompt ? SlotPhase.Reading : SlotPhase.Writing;
        double read = s.PromptProgress >= 0 ? Math.Clamp(s.PromptProgress, 0, 1) : 0;
        return new SlotActivity(s.Id, phase, read, s.PromptDone, s.PromptCached, s.Tps, s.Generated, s.MaxTokens, s.CtxUsed, s.CtxMax);
    }
}
