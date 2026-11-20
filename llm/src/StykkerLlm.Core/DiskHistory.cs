using Stykker.Shared.Disk;

namespace StykkerLlm.Core;

// Ein Takt eines Volumes für die Verlaufsanzeige: Lesen und Schreiben (Bytes je Sekunde), Auslastung (%). null = nicht gemessen.
public readonly record struct DiskPoint(double? Read, double? Write, double? Busy);

// Der Verlauf je Volume: die letzten Takte (ein Takt je Sekunde, also fünf Minuten). Der Takt schreibt, die Seite liest eine Kopie.
public sealed class DiskHistory
{
    public const int Length = 300;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<DiskPoint>> _series = new(StringComparer.Ordinal);

    public void Add(DiskSample sample)
    {
        lock (_gate)
        {
            foreach (var v in sample.Volumes)
            {
                if (!_series.TryGetValue(v.Letter, out var list)) _series[v.Letter] = list = new List<DiskPoint>();
                list.Add(new DiskPoint(v.ReadBytesPerSec, v.WriteBytesPerSec, v.BusyPercent));
                if (list.Count > Length) list.RemoveRange(0, list.Count - Length);
            }
        }
    }

    // Die Punkte eines Volumes, ältester zuerst (eine Kopie)
    public IReadOnlyList<DiskPoint> Of(string letter)
    {
        lock (_gate) return _series.TryGetValue(letter, out var list) ? list.ToArray() : Array.Empty<DiskPoint>();
    }

    // Die Buchstaben der Volumes mit Verlauf, alphabetisch
    public IReadOnlyList<string> Letters()
    {
        lock (_gate) return _series.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }
}
