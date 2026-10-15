using System.Globalization;

namespace Stykker.Shared.Disk;

// Die Umrechnung und Zuordnung der Laufwerkswerte, ohne Windows: alles hier ist mit Testdaten prüfbar. Die Windows-Seite
// (Windows/DiskCounters.cs) liest nur die Rohwerte und gibt sie hierher.
public static class DiskMath
{
    // Busarten (STORAGE_BUS_TYPE) mit eigener Art; alle anderen Busse entscheiden über den „seek penalty“.
    public const int BusUsb = 7, BusNvme = 17;

    // Art aus Bus und Suchzeit: NVMe und USB sagt der Bus; sonst gilt, dass eine Platte mit Suchzeit eine HDD ist.
    // Fehlt die Angabe zur Suchzeit (null), bleibt es bei Unknown.
    public static DiskKind Classify(int busType, bool? seekPenalty) => busType switch
    {
        BusNvme => DiskKind.Nvme,
        BusUsb => DiskKind.Usb,
        _ => seekPenalty switch
        {
            true => DiskKind.Hdd,
            false => DiskKind.Ssd,
            null => DiskKind.Unknown,
        },
    };

    // Die Nummer eines physischen Laufwerks aus dem Instanznamen: "0 C: D:" → 0 (die Zahl, dann die Buchstaben seiner Volumes).
    // "_Total" und alles ohne Zahl → null.
    public static int? PhysicalNumber(string instance)
    {
        int space = instance.IndexOf(' ');
        var head = space < 0 ? instance : instance[..space];
        return int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= 0 ? number : null;
    }

    // Der Buchstabe eines Volumes aus dem Instanznamen: "c:" → "C". Alles andere ("_Total", Einhängepunkte) → null.
    public static string? VolumeLetter(string instance) =>
        instance.Length == 2 && char.IsAsciiLetter(instance[0]) && instance[1] == ':'
            ? char.ToUpperInvariant(instance[0]).ToString()
            : null;

    // Der Programmname aus dem Instanznamen: PDH zählt gleichnamige Prozesse mit "#n" – "chrome#3" → "chrome".
    public static string ProcessName(string instance)
    {
        int hash = instance.LastIndexOf('#');
        bool suffix = hash > 0 && hash < instance.Length - 1 && instance[(hash + 1)..].All(char.IsAsciiDigit);
        return suffix ? instance[..hash] : instance;
    }

    // Die Prozesse mit dem meisten Platten-E/A: je Instanz die PID (ID-Process-Zähler), Lese- und Schreibrate. Ausgelassen werden
    // „_Total“, der Leerlauf (PID 0) und Prozesse ohne Bewegung. Größte zuerst, höchstens count.
    public static IReadOnlyList<ProcessIoRow> ProcessRows(IReadOnlyDictionary<string, int> pidByInstance,
        IReadOnlyDictionary<string, double> readByInstance, IReadOnlyDictionary<string, double> writeByInstance, int count)
    {
        var rows = new List<ProcessIoRow>();
        foreach (var (instance, pid) in pidByInstance)
        {
            if (pid <= 0 || instance is "_Total" or "Idle") continue;
            double read = Finite(readByInstance.GetValueOrDefault(instance)) ?? 0;
            double write = Finite(writeByInstance.GetValueOrDefault(instance)) ?? 0;
            if (read + write <= 0) continue;
            rows.Add(new ProcessIoRow(pid, ProcessName(instance), read, write));
        }
        return rows.OrderByDescending(r => r.ReadBytesPerSec + r.WriteBytesPerSec).ThenBy(r => r.Pid).Take(Math.Max(0, count)).ToList();
    }

    // Eine Zahl, die gemessen wurde: endlich und nicht negativ. Sonst null (nie eine erfundene 0).
    public static double? Finite(double? value) => value is double d && double.IsFinite(d) && d >= 0 ? d : null;

    // Auslastung in Prozent, begrenzt auf 0 bis 100; null bleibt null.
    public static double? Percent(double? value) => Finite(value) is double d ? Math.Min(100, d) : null;

    // Die Werte eines Volumes aus den Rohdaten, die Windows liefert (Größe und Belegung in Byte).
    public readonly record struct VolumeFact(string Letter, string Label, string FileSystem, double TotalBytes, double FreeBytes, int? DiskNumber);

    // Eine komplette Messung aus den Rohdaten. physical und logical: je Instanzname die Werte [Lesen, Schreiben, Auslastung,
    // Warteschlange] (Bytes/s, Prozent, Länge). identity: die Art und das Modell je Laufwerksnummer. rates = false heißt
    // Grundwert (erste Abtastung): die Takt-Werte bleiben dann null, die Liste der Laufwerke und Volumes steht trotzdem.
    public static DiskSample BuildSample(DateTime at, IReadOnlyDictionary<string, double[]> physical,
        IReadOnlyDictionary<string, double[]> logical, IReadOnlyList<VolumeFact> volumes,
        IReadOnlyDictionary<int, (DiskKind Kind, string Model)> identity, bool rates)
    {
        // Die Laufwerke: alle, die die Zähler nennen, und alle, die ein Volume nennt (auch im Grundwert, wenn die Zähler
        // ihre Namen noch nicht liefern). Ohne Zählerwert bleiben die Takt-Werte null.
        var rateByNumber = new Dictionary<int, double[]>();
        foreach (var (instance, values) in physical)
            if (PhysicalNumber(instance) is int n) rateByNumber[n] = values;
        var numbers = rateByNumber.Keys.Concat(volumes.Where(v => v.DiskNumber is int).Select(v => v.DiskNumber!.Value)).Distinct().OrderBy(n => n);

        var disks = new List<PhysicalDiskRow>();
        foreach (int number in numbers)
        {
            var (kind, model) = identity.TryGetValue(number, out var known) ? known : (DiskKind.Unknown, "");
            var rate = rates && rateByNumber.TryGetValue(number, out var values) ? RateOf(values) : null;
            disks.Add(new PhysicalDiskRow(number, kind, model, Finite(rate?.Read), Finite(rate?.Write), Percent(rate?.Busy), Finite(rate?.Queue)));
        }

        var byLetter = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var (instance, values) in logical)
            if (VolumeLetter(instance) is { } letter) byLetter[letter] = values;

        var rows = new List<VolumeRow>();
        foreach (var fact in volumes)
        {
            var kind = fact.DiskNumber is int n && identity.TryGetValue(n, out var known) ? known.Kind : DiskKind.Unknown;
            var rate = rates && byLetter.TryGetValue(fact.Letter, out var values) ? RateOf(values) : null;
            rows.Add(new VolumeRow(fact.Letter, fact.Label, fact.FileSystem, Finite(fact.TotalBytes) ?? 0, Finite(fact.FreeBytes) ?? 0,
                fact.DiskNumber, kind, Finite(rate?.Read), Finite(rate?.Write), Percent(rate?.Busy), Finite(rate?.Queue)));
        }
        return new DiskSample(at, disks, rows);
    }

    // Die vier Werte einer Zeile als Takt; null, wenn die Zeile zu kurz ist
    private static DiskRate? RateOf(double[] values) =>
        values.Length >= 4 ? new DiskRate(values[0], values[1], values[2], values[3]) : null;
}
