namespace StykkerLlm.Core;

// Die Texte des Abschnitts „Storage“ der Ansicht „Model“ (ModelPaging.cs): Lademodus, Puffer, Nachladen von der Platte
public static partial class Strings
{
    public const string StorTitle = "Storage",
        StorPaging = "Pages in from the disk while writing",
        StorInMemory = "In memory – the disk is idle",
        StorLocked = "Kept in RAM – does not page from the disk",
        StorReadOnce = "Read once at start – does not page from the disk",
        StorUnknown = "Paging not measured yet",
        StorAutoSuffix = " · llama.cpp default",
        StorNoBuffers = "Where the weights sit appears in the log when the model loads (needs --log-file).",
        StorNoPaging = "no paging",
        StorEstimate = "The amounts read are estimates: another program reading from the same drive at the same time counts too.",
        StorHint = "Reading does not wear an SSD; paging costs speed. More free RAM, fewer experts on the CPU (--n-cpu-moe) or -lm mlock keep the model in memory.",
        StorInRamTitle = "Measured in the working set of the server. Pages Windows moved to its standby list are still in RAM but do not count, so the share is a lower bound.";

    public static string StorMode(ModelLoadMode m) => m switch
    {
        ModelLoadMode.Mmap => "memory-mapped (mmap)",
        ModelLoadMode.MmapLock => "memory-mapped and locked in RAM (mmap+mlock)",
        ModelLoadMode.Lock => "read and locked in RAM (mlock)",
        ModelLoadMode.Read => "read into memory at start (no mmap)",
        _ => "read with direct I/O at start (dio)",
    };

    public static string StorLoading(string mode) => $"Loading: {mode}";
    public static string StorBuffers(double gpu, double ram, double mapped)
    {
        var parts = new List<string>();
        if (gpu > 0.05) parts.Add($"{N1(gpu)} GB on the GPU");
        if (ram > 0.05) parts.Add($"{N1(ram)} GB in RAM");
        if (mapped > 0.05) parts.Add($"{N1(mapped)} GB stays in the file (mmap)");
        return string.Join(" · ", parts);
    }
    public static string StorFile(string size, string? drive, string? kind) =>
        drive == null ? $"Model file {size}" : kind == null ? $"Model file {size} on {drive}:" : $"Model file {size} on {drive}: ({kind})";
    public static string StorInRam(double fraction) => fraction >= 0.995 ? "all of the mapped part in RAM" : $"≥ {(fraction * 100).ToString("0", Inv)}% of the mapped part in RAM";
    public static string StorNow(double faults, string? read, string? drive) =>
        read == null ? $"{faults.ToString("N0", Inv)} hard page faults/s" : $"{faults.ToString("N0", Inv)} hard page faults/s · {read} read from {drive}:";
    public static string StorRead(string amount, string? drive, double? times) =>
        $"≈ {amount} read from {drive ?? "the drive"}{(drive != null ? ":" : "")} while paging" + (times is double t ? $" (≈ {t.ToString(t < 10 ? "0.0" : "0", Inv)}× the model)" : "");
    public static string StorPerToken(string amount) => $"≈ {amount} per token";
}
