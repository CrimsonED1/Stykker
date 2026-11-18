namespace StykkerLlm.Core;

// Die Texte der Ansicht „Model“ (ModelAnatomy.cs, Components/ModelView.razor): Aufbau, Experten, Schichten, was gerade läuft
public static partial class Strings
{
    public const string AnatTitle = "Model", AnatAria = "What the model is doing",
        AnatExpertsPerToken = "experts per token", AnatDense = "Dense model: every weight works on every token",
        AnatLayersTitle = "Layers", AnatGpu = "GPU", AnatCpu = "CPU", AnatUnknown = "unknown", AnatOutput = "out",
        AnatExpertsTitle = "Experts of one layer",
        AnatWhich = "Lit: the share that works per token. Which experts the router picks is not reported by llama.cpp.",
        AnatNoArgs = "Command line unknown: where the layers sit is not shown",
        AnatNglAuto = "-ngl not set or auto: llama.cpp decides itself where the layers sit",
        AnatNoModel = "The model file of this server cannot be read (GGUF), so its structure is not shown.",
        AnatNowTitle = "Now", AnatIdle = "Idle", AnatReading = "Reading the prompt", AnatWriting = "Writing",
        AnatLiveTitle = "Through the proxy", AnatWaiting = "Waiting for the first token", AnatThinking = "Thinking",
        AnatAnswering = "Answering", AnatToolAny = "Tool call", AnatNoSlots = "The server reports no slots.",
        AnatLegendMain = "upper: attention and the rest", AnatLegendExperts = "lower: experts";

    public static string AnatParams(string total) => $"{total} parameters";
    public static string AnatShared(int n) => n == 1 ? "+1 shared expert" : $"+{n} shared experts";
    public static string AnatActive(string active, double share) => $"≈ {active} active per token ({(share * 100).ToString("0", Inv)}%)";
    public static string AnatLayers(int n, int moe) => moe > 0 && moe < n ? $"{n} layers, {moe} with experts" : $"{n} layers";
    public static string AnatGpuLayers(int gpu, int n) => $"{gpu} of {n} on the GPU";
    public static string AnatExpertsCpu(int n) => n == 1 ? "experts of 1 layer on the CPU" : $"experts of {n} layers on the CPU";
    public static string AnatLayerTip(int i, string main, string? experts) =>
        experts == null ? $"Layer {i}: {main}" : $"Layer {i}: attention {main}, experts {experts}";
    public static string AnatOutputTip(string place) => $"Output layer: {place}";
    public static string AnatBadOverride(string pattern) => $"-ot pattern not readable: {pattern}";
    public static string AnatTool(string name) => $"Tool: {name}";
    public static string AnatSlot(int id) => $"Slot {id}";
    public static string AnatRead(int done, int cached) =>
        cached > 0 ? $"{done.ToString("N0", Inv)} read · {cached.ToString("N0", Inv)} from cache" : $"{done.ToString("N0", Inv)} read";
    public static string AnatWritten(int generated, int max) =>
        max > 0 ? $"{generated.ToString("N0", Inv)} of {max.ToString("N0", Inv)} tokens" : $"{generated.ToString("N0", Inv)} tokens";
    public static string AnatCtx(int used, int max) => $"context {used.ToString("N0", Inv)} / {max.ToString("N0", Inv)}";
    public static string AnatFor(double seconds) => seconds < 60 ? $"{seconds.ToString("0", Inv)} s" : $"{(seconds / 60).ToString("0", Inv)} min";
    public static string AnatChip(int used, int count) => $"{used}/{count}";
    public static string AnatChipTip(int used, int count, string active) => $"Mixture of experts: {used} of {count} experts per token, ≈ {active} active parameters";
}
