namespace StykkerLlm.Core;

// Die Angaben zu einem Verlaufseintrag, die alle drei Oberflächen zeigen: Kommandozeile, Parameter, Laufstatistik und
// die Modelldatei. Fenster (HistoryDetailsForm), Web (/history/{key}) und die TUI (/show) sagen dasselbe, deshalb
// steht es hier und nicht in den Oberflächen. Das Lesen der Modelldatei dauert (Netzlaufwerk, große Datei),
// ModelFileFacts() wird deshalb von den Oberflächen im Hintergrund gerufen.
//
// Der Eintrag kommt entweder aus der Bibliothek (Server und Web) oder aus dem Zustand des Servers (Fenster, TUI);
// beide Wege liefern denselben HistoryRow, damit nichts doppelt implementiert und nichts auseinanderläuft.
public static class HistoryDetails
{
    public readonly record struct Fact(string Key, string Value);

    // Das, was alle drei Oberflächen vom Verlaufseintrag brauchen (ohne Geheimnisse – die stehen in der Quelle als "***")
    public readonly record struct Row(string Key, string Program, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env,
        string? WorkingDir, string? ModelPath, int? Port, int? Ctx, bool HasSecrets, DateTime FirstSeen, DateTime LastSeen,
        int Runs, double TotalSeconds, double BestTps, double MeanTps, double MaxVramGb, double? ModelSizeGb)
    {
        public static Row Of(HistoryEntry h) => new(h.Key, h.Program, h.Args, h.Env, h.WorkingDir, h.ModelPath, h.Port, h.Ctx,
            h.HasSecrets, h.FirstSeen, h.LastSeen, h.Runs, h.TotalSeconds, h.BestTps, h.MeanTps, h.MaxVramGb, h.ModelSizeGb);

        public static Row Of(RemoteHistory h) => new(h.Key, h.Program, h.Args, h.Env, h.WorkingDir.Length == 0 ? null : h.WorkingDir,
            h.ModelPath.Length == 0 ? null : h.ModelPath, h.Port > 0 ? h.Port : null, h.Ctx > 0 ? h.Ctx : null,
            h.HasSecrets, h.FirstSeen, h.LastSeen, h.Runs, h.TotalSeconds, h.BestTps, h.MeanTps, h.MaxVramGb, h.ModelSizeGb);

        // Der Eintrag darf direkt übergeben werden (Server und Web nehmen HistoryEntry, Fenster und TUI RemoteHistory)
        public static implicit operator Row(HistoryEntry h) => Of(h);
        public static implicit operator Row(RemoteHistory h) => Of(h);
    }

    public static string CommandLine(Row h) => CmdLine.Join(new[] { h.Program }.Concat(h.Args));

    // Parameter und Umgebung, wie sie auf der Kommandozeile standen (ohne Programm, Secrets bleiben unangetastet)
    public static IReadOnlyList<Fact> Parameters(Row h)
    {
        var facts = new List<Fact>();
        foreach (var e in LlamaServerArgs.Parse(h.Args, h.Env).Entries)
            facts.Add(new Fact(e.Name.Length > 0 ? e.Name : Strings.RowArgument, e.Value ?? ""));
        foreach (var (k, v) in h.Env ?? new Dictionary<string, string>()) facts.Add(new Fact(Strings.EnvRow(k), v));
        return facts;
    }

    // Laufstatistik: nur was wirklich gemessen wurde, damit die Liste im Fenster nicht halbleer wirkt
    public static IReadOnlyList<Fact> Runs(Row h)
    {
        var facts = new List<Fact>();
        Add(facts, Strings.HistProgram, h.Program);
        Add(facts, Strings.ColWorkingDir, h.WorkingDir);
        Add(facts, Strings.ColPort, h.Port?.ToString(Strings.Inv));
        Add(facts, Strings.ColContext, h.Ctx is int c ? Strings.N0(c) : null);
        Add(facts, Strings.RowFirstSeen, h.FirstSeen.ToString("yyyy-MM-dd HH:mm", Strings.Inv));
        Add(facts, Strings.ColLastSeen, h.LastSeen.ToString("yyyy-MM-dd HH:mm", Strings.Inv));
        Add(facts, Strings.ColRuns, h.Runs.ToString(Strings.Inv));
        Add(facts, Strings.RowRunningTime, Fmt.Dur(h.TotalSeconds));
        Add(facts, Strings.RowTpsBest, h.BestTps > 0 ? Strings.N1(h.BestTps) : null);
        Add(facts, Strings.RowTpsAverage, h.MeanTps > 0 ? Strings.N1(h.MeanTps) : null);
        Add(facts, Strings.RowGpuMax, h.MaxVramGb > 0 ? Strings.N1(h.MaxVramGb) + " GB" : null);
        Add(facts, Strings.HistSecrets, h.HasSecrets ? Strings.SecretsNote : null);
        return facts;
    }

    public static string? ModelPath(Row h) => ServerLauncher.ResolveModel(h.ModelPath, h.WorkingDir);

    // Dateigröße, GGUF-Kopf und die daraus geschätzte VRAM-Belegung (der Pfad selbst steht schon in der Oberfläche)
    public static IReadOnlyList<Fact> ModelFileFacts(Row h, string? path = null)
    {
        var facts = new List<Fact>();
        path ??= ModelPath(h);
        if (path == null) return facts;

        long? size = null;
        try { if (File.Exists(path)) size = new FileInfo(path).Length; } catch { /* Datei nicht lesbar: nur der Pfad bleibt */ }
        if (size == null) facts.Add(new Fact(Strings.HistFile, Strings.ModelFileMissing));
        else facts.Add(new Fact(Strings.HistFileSize, Strings.N1(size.Value / 1073741824.0) + " GB"));
        if (h.ModelSizeGb is double wasRunning && wasRunning > 0 && size == null)
            facts.Add(new Fact(Strings.HistFileSizeWhenRan, Strings.N1(wasRunning) + " GB"));

        var g = size != null ? Gguf.TryRead(path) : null;
        if (g == null) return facts;

        if (g.Name is { Length: > 0 } name) facts.Add(new Fact(Strings.ColName, name));
        if (g.Architecture.Length > 0) facts.Add(new Fact(Strings.HistArchitecture, g.Architecture));
        if (g.Quantization.Length > 0) facts.Add(new Fact(Strings.HistQuantization, g.Quantization));
        if (g.ParameterCount > 0) facts.Add(new Fact(Strings.HistParameters, Parameters(g.ParameterCount)));
        if (g.BlockCount is int layers) facts.Add(new Fact(Strings.HistLayers, layers.ToString(Strings.Inv)));
        if (g.ContextLength is long trained) facts.Add(new Fact(Strings.HistTrainedContext, Strings.N0(trained)));

        var args = LlamaServerArgs.Parse(h.Args, h.Env);
        int ctx = args.Ctx is > 0 ? args.Ctx.Value : h.Ctx ?? 0;
        if (ctx > 0)
        {
            var est = VramEstimate.Estimate(g, ctx, args.CacheTypeK, args.CacheTypeV, args.Ngl);
            facts.Add(new Fact(Strings.HistVramEstimate, Strings.VramEstimateParts(est.TotalGb, est.ModelGb, est.KvCacheGb, est.ComputeGb)));
        }
        return facts;
    }

    private static void Add(List<Fact> facts, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value)) facts.Add(new Fact(key, value));
    }

    private static string Parameters(long n) =>
        n >= 1_000_000_000 ? (n / 1e9).ToString("0.0", Strings.Inv) + " B"
        : n >= 1_000_000 ? (n / 1e6).ToString("0.0", Strings.Inv) + " M"
        : n.ToString(Strings.Inv);
}