using Stykker.Shared.Disk;
using Stykker.Shared.Windows;
using StykkerSys.Core;

namespace StykkerSys.Platform.Windows;

// Die Windows-Sonde der Laufwerkseite: DiskCounters aus Stykker.Shared (Volumes, Datenträger, Platten-E/A je Prozess).
// Fehlen die Zähler, liefert sie null – die Seite zeigt „–", nie eine erfundene Zahl.
public sealed class WindowsDiskProbe : IDiskProbe
{
    private readonly DiskCounters _counters = new();

    public DiskSample? ReadDisks() => _counters.ReadDisks();

    public IReadOnlyList<ProcessIoRow>? ReadProcesses(int count) => _counters.ReadProcesses(count);

    public void Reset() => _counters.Reset();

    public void Dispose() => _counters.Dispose();
}
