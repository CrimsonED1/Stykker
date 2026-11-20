namespace StykkerMeshShare.Core;

// Freier Platz auf dem Laufwerk des geteilten Ordners. Unter der Grenze (MinFreeBytes, Vorgabe 5 GB, einstellbar)
// nimmt dieser PC nichts mehr an: keine Downloads von anderen PCs, kein Hochladen über die Seite. Seine eigenen
// Änderungen und Löschungen gibt er weiter, und sobald wieder genug frei ist, geht es von selbst weiter.
// Ein Download startet außerdem nur, wenn nach ihm noch die Grenze frei bleibt.
public sealed record DiskSpace(string Root, long Free, long Total);

public sealed partial class MeshNode
{
    public const long DefaultMinFreeBytes = 5L * 1024 * 1024 * 1024;

    private long _minFreeBytes = DefaultMinFreeBytes;
    private DiskSpace? _disk;
    private long _diskAt;

    // Die Grenze in Bytes; 0 schaltet die Prüfung aus.
    public long MinFreeBytes
    {
        get => Interlocked.Read(ref _minFreeBytes);
        set
        {
            Interlocked.Exchange(ref _minFreeBytes, Math.Max(0, value));
            WakeRequested?.Invoke();   // eine kleinere Grenze kann wartende Downloads freigeben: sofort abgleichen
        }
    }

    // Höchstens alle 2 Sekunden neu gemessen (die Seite fragt alle 1,5 s, jeder Download vor jeder Datei).
    public DiskSpace? Disk
    {
        get
        {
            long now = Environment.TickCount64;
            if (_disk != null && now - _diskAt < 2000) return _disk;
            _disk = Measure(_folder);
            _diskAt = now;
            return _disk;
        }
    }

    // Unter der Grenze: nichts annehmen. Ohne Messung (Laufwerk nicht lesbar) wird nicht angehalten.
    public bool LowSpace => Disk is { } disk && MinFreeBytes > 0 && disk.Free < MinFreeBytes;

    // Bleibt nach needed Bytes noch die Grenze frei?
    private bool EnoughSpace(long needed) => Disk is not { } disk || disk.Free - Math.Max(0, needed) >= MinFreeBytes;

    private DiskSpace? Measure(string folder)
    {
        if (_options.MeasureDisk != null) return _options.MeasureDisk(folder);
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            return new DiskSpace(root.TrimEnd('\\', '/'), drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }
}
