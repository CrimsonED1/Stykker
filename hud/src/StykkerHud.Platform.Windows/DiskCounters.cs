using Stykker.Shared.Windows;

namespace StykkerHud.Platform.Windows;

// Datenträger-Durchsatz der ganzen Maschine (Summe aller physischen Datenträger) über Leistungsindikatoren.
// Raten-Zähler liefern erst ab der zweiten Abtastung einen Wert: die erste Abtastung legt nur die Basiszahl,
// deshalb antwortet der erste Aufruf mit null und die Anzeige zeigt einmal „–".
internal sealed class DiskRateQuery : IDisposable
{
    private readonly IntPtr _query, _read, _write;
    private readonly bool _ok;
    private bool _warmup = true;

    public DiskRateQuery()
    {
        _ok = Pdh.Open(
            new[] { @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", @"\PhysicalDisk(_Total)\Disk Write Bytes/sec" },
            out _query, out var counters);
        _read = counters.Length > 0 ? counters[0] : IntPtr.Zero;
        _write = counters.Length > 1 ? counters[1] : IntPtr.Zero;
    }

    // Byte je Sekunde; null, solange noch keine Basiszahl steht.
    public (double Read, double Write)? Read()
    {
        if (!_ok) return null;
        if (_warmup)
        {
            _warmup = false;
            Pdh.Collect(_query);
            return null;
        }
        if (!Pdh.Collect(_query)) return null;
        return (Sum(_read), Sum(_write));
    }

    // Nach einer Pause wäre die Rate über die ganze Pause gemittelt: Basiszahl neu legen.
    public void Reset() => _warmup = true;

    private static double Sum(IntPtr counter)
    {
        double total = 0;
        foreach (var (_, value) in Pdh.Values(counter, Pdh.FmtDouble)) total += value;
        return total;
    }

    public void Dispose() => Pdh.Close(_query);
}