using System.Globalization;
using System.Text.RegularExpressions;
using Stykker.Shared.Disk;

namespace StykkerLlm.Core;

// Wie ein llama-server sein Modell von der Platte holt (Abschnitt „Storage“ der Ansicht „Model“).
// Von llama.cpp selbst kommen nur der Lademodus (-lm, früher --no-mmap/--mlock) und beim Laden die Größe jedes
// Modellpuffers im Log („CUDA0 model buffer size = … MiB“, „CPU_Mapped model buffer size = … MiB“). Ob im Betrieb von
// der SSD nachgeladen wird, misst Windows: harte Seitenfehler des Prozesses, wie viel der eingeblendeten Datei im
// Arbeitsspeicher liegt, und das Lesen vom Laufwerk der Modelldatei. Lesen nutzt eine SSD nicht ab – es kostet Zeit.
public enum ModelLoadMode { Mmap, MmapLock, Lock, Read, DirectIo }

public static class ModelLoad
{
    // (Modus, nur Vorgabe „auto“). -lm gewinnt; sonst die alten Schalter; ohne Angabe: mmap (llama.cpp-Vorgabe)
    public static (ModelLoadMode Mode, bool Auto) Of(LlamaServerArgs? a)
    {
        if (a?.LoadMode is { } lm)
            return lm.ToLowerInvariant() switch
            {
                "mmap" => (ModelLoadMode.Mmap, false),
                "mlock" => (ModelLoadMode.Lock, false),
                "mmap+mlock" => (ModelLoadMode.MmapLock, false),
                "none" => (ModelLoadMode.Read, false),
                "dio" => (ModelLoadMode.DirectIo, false),
                _ => (ModelLoadMode.Mmap, true),
            };
        bool noMmap = a?.Has("no-mmap") == true, mlock = a?.Has("mlock") == true;
        if (noMmap) return (mlock ? ModelLoadMode.Lock : ModelLoadMode.Read, false);
        if (mlock) return (ModelLoadMode.MmapLock, false);
        return (ModelLoadMode.Mmap, true);
    }

    // Nur mit mmap ohne mlock kann Windows Teile der Datei aus dem Speicher werfen und später von der Platte nachholen
    public static bool CanPage(ModelLoadMode m) => m == ModelLoadMode.Mmap;
}

// Ein Modellpuffer aus dem Ladelog: Name (CUDA0, Vulkan0, CPU, CPU_Mapped, CUDA_Host …) und Größe in MiB
public sealed record ModelBuffer(string Name, double MiB)
{
    // „…_Mapped“: die Gewichte bleiben in der eingeblendeten Datei (können von der Platte nachgeladen werden)
    public bool Mapped => Name.EndsWith("_Mapped", StringComparison.OrdinalIgnoreCase);
    // „CPU“, „CPU_Mapped“, „CUDA_Host“, „CPU_REPACK“: im Arbeitsspeicher; sonst auf einem Grafikgerät
    public bool Host => Name.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) || Name.EndsWith("_Host", StringComparison.OrdinalIgnoreCase);
    public double Gb => MiB / 1024;
}

public static partial class ModelBuffers
{
    [GeneratedRegex(@"load_tensors:\s+(\S+) model buffer size =\s*([\d.]+) MiB")]
    private static partial Regex Line();

    [GeneratedRegex(@"load_tensors: loading model tensors")]
    private static partial Regex LoadStart();

    // Die Puffer des zuletzt geladenen Modells. Neuere Builds schreiben je Name viele Zeilen (eine je Tensorgruppe,
    // gemessen mit b11854: 109× CPU_Mapped), ältere eine – deshalb wird je Name summiert. Ein neuer Ladevorgang
    // („loading model tensors“) beginnt von vorn, damit ein neu geladenes Modell das alte ersetzt.
    public static IReadOnlyList<ModelBuffer> Parse(string text)
    {
        var starts = LoadStart().Matches(text);
        for (var i = starts.Count - 1; i >= -1; i--)
        {
            var from = i < 0 ? 0 : starts[i].Index;
            var to = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;
            var map = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (Match m in Line().Matches(text[from..to]))
                if (double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mib))
                    map[m.Groups[1].Value] = map.GetValueOrDefault(m.Groups[1].Value) + mib;
            if (map.Count > 0) return map.Select(kv => new ModelBuffer(kv.Key, kv.Value)).ToList();
        }
        return [];
    }
}

// Die Messungen eines Servers über die Zeit. Sample kommt aus der Speicherabfrage (alle paar Sekunden, Hintergrund),
// Tick aus dem Takt der Engine (mit dem Lesen vom Laufwerk der Modelldatei). Gelesen wird von der Oberfläche.
public sealed class PagingTracker
{
    // ab so vielen harten Seitenfehlern je Sekunde gilt: der Server holt gerade Seiten von der Platte
    public const double PagingFaultsPerSec = 50;
    // so lange nach dem letzten Nachladen gilt er noch als „lädt nach“ (die Abfrage läuft nur alle paar Sekunden)
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private int _pid;
    private long? _faults;
    private DateTime _faultsAt;
    private DateTime _lastTick, _pagingSeen;

    public double? HardFaultsPerSec { get; private set; }
    public MappedFileResidency? Residency { get; private set; }
    public DateTime? ResidencyAt { get; private set; }
    // Geschätzt vom Laufwerk der Modelldatei gelesen, während der Server nachlud (Bytes seit dem Start der Messung)
    public double ReadBytes { get; private set; }
    // Lesen vom Laufwerk jetzt, solange der Server nachlädt (sonst 0)
    public double ReadBytesPerSec { get; private set; }

    public bool PagingNow(DateTime now) { lock (_gate) return _pagingSeen != default && now - _pagingSeen <= Hold; }

    public void Sample(DateTime now, int pid, long? hardFaults, MappedFileResidency? residency, bool residencyRead)
    {
        lock (_gate)
        {
            if (pid != _pid) { _pid = pid; _faults = null; HardFaultsPerSec = null; Residency = null; ResidencyAt = null; ReadBytes = 0; ReadBytesPerSec = 0; _pagingSeen = default; }
            if (hardFaults is long f)
            {
                if (_faults is long prev && f >= prev && now > _faultsAt)
                {
                    HardFaultsPerSec = (f - prev) / (now - _faultsAt).TotalSeconds;
                    if (HardFaultsPerSec >= PagingFaultsPerSec) _pagingSeen = now;
                }
                _faults = f; _faultsAt = now;
            }
            if (residencyRead) { Residency = residency; ResidencyAt = residency != null ? now : null; }
        }
    }

    // volumeReadBps: Lesen vom Laufwerk der Modelldatei jetzt (null = unbekannt)
    public void Tick(DateTime now, double? volumeReadBps)
    {
        lock (_gate)
        {
            double dt = _lastTick == default ? 0 : Math.Min(5, (now - _lastTick).TotalSeconds);
            _lastTick = now;
            bool paging = _pagingSeen != default && now - _pagingSeen <= Hold;
            ReadBytesPerSec = paging && volumeReadBps is double r and > 0 ? r : 0;
            if (dt > 0) ReadBytes += ReadBytesPerSec * dt;
        }
    }
}

// Was die Ansicht zeigt: aus Lademodus, Puffern, Datei, Laufwerk und den Messungen zusammengesetzt
public enum StorageVerdict { Unknown, InMemory, Locked, ReadOnce, Paging }

public sealed record ModelStorage(
    ModelLoadMode Mode, bool AutoMode, IReadOnlyList<ModelBuffer> Buffers, long FileBytes, string? Drive, string? DriveKind,
    MappedFileResidency? Residency, double? HardFaultsPerSec, bool PagingNow, double ReadBytesPerSec, double ReadBytes,
    double TokensPerSec)
{
    public double MappedGb => Buffers.Where(b => b.Mapped).Sum(b => b.Gb);
    public double DeviceGb => Buffers.Where(b => !b.Host).Sum(b => b.Gb);
    public double HostGb => Buffers.Where(b => b.Host && !b.Mapped).Sum(b => b.Gb);

    // Anteil des eingeblendeten Teils im Arbeitsspeicher: was im RAM liegt gegen die Puffer „…_Mapped“ (die Teile, die
    // auf die GPU kopiert wurden, braucht die Datei danach nicht mehr). Ohne Pufferzeilen: gegen die ganze Datei.
    public double? MappedInRam
    {
        get
        {
            if (Residency is not { } r || r.MappedBytes <= 0) return null;
            double basis = MappedGb > 0 ? MappedGb * 1073741824.0 : r.MappedBytes;
            return Math.Clamp(r.ResidentBytes / basis, 0, 1);
        }
    }

    // Wie oft die Datei (geschätzt) von der Platte gelesen wurde
    public double? ReadTimes => FileBytes > 0 && ReadBytes > 0 ? ReadBytes / FileBytes : null;
    // Gelesen je erzeugtem Token, während nachgeladen wird
    public double? BytesPerToken => ReadBytesPerSec > 0 && TokensPerSec > 0.5 ? ReadBytesPerSec / TokensPerSec : null;

    public StorageVerdict Verdict =>
        Mode is ModelLoadMode.Lock or ModelLoadMode.MmapLock ? StorageVerdict.Locked
        : Mode is ModelLoadMode.Read or ModelLoadMode.DirectIo ? StorageVerdict.ReadOnce
        : PagingNow ? StorageVerdict.Paging
        : MappedInRam is double f ? (f >= 0.95 || MappedGb is > 0 and < 1 ? StorageVerdict.InMemory : StorageVerdict.Unknown)
        : StorageVerdict.Unknown;

    // modelFileGb: die Größe, die der Server meldet (/v1/models); sonst die Größe der Datei
    public static ModelStorage Of(LlamaServerArgs? args, IReadOnlyList<ModelBuffer> buffers, string? modelPath, double? modelFileGb,
        DiskKind? driveKind, PagingTracker paging, DateTime now, double tokensPerSec)
    {
        var (mode, auto) = ModelLoad.Of(args);
        long size = modelFileGb is double gb and > 0 ? (long)(gb * 1073741824.0) : 0;
        string? drive = DriveOf(modelPath);
        try
        {
            if (size == 0 && !string.IsNullOrEmpty(modelPath) && new FileInfo(modelPath) is { Exists: true } fi) size = fi.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return new ModelStorage(mode, auto, buffers, size, drive, driveKind?.ToString(), paging.Residency, paging.HardFaultsPerSec,
            paging.PagingNow(now), paging.ReadBytesPerSec, paging.ReadBytes, tokensPerSec);
    }

    // „D:\models\x.gguf“ → „D“; ohne Laufwerksbuchstaben (UNC, Linux) null
    public static string? DriveOf(string? path) =>
        path is { Length: >= 2 } p && p[1] == ':' && char.IsLetter(p[0]) ? char.ToUpperInvariant(p[0]).ToString() : null;
}
