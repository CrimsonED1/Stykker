using System.Text.Json.Serialization;

namespace Stykker.Shared.Disk;

// Die Art eines physischen Laufwerks. Unknown, wenn Windows die Art nicht nennt: dann steht „unknown“, nie eine geratene Art.
// Als Text in JSON („Nvme“, nicht 1), damit die Seiten die Art lesen können.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiskKind { Unknown, Nvme, Ssd, Hdd, Usb }

public static class DiskKinds
{
    // Das Kürzel, wie es auf den Karten steht
    public static string Label(DiskKind kind) => kind switch
    {
        DiskKind.Nvme => "NVMe",
        DiskKind.Ssd => "SSD",
        DiskKind.Hdd => "HDD",
        DiskKind.Usb => "USB",
        _ => "unknown",
    };
}

// Die Takt-Werte eines Laufwerks zu einem Zeitpunkt: Lesen und Schreiben in Bytes je Sekunde, Auslastung in Prozent und
// die mittlere Warteschlange. Busy ist „% Disk Time“ (bei mehreren Warteschlangen kann der Zähler über 100 gehen; er wird begrenzt).
public readonly record struct DiskRate(double Read, double Write, double Busy, double Queue);

// Ein physisches Laufwerk. Werte null heißt „nicht gemessen“ (noch keine zweite Abtastung, oder der Zähler fehlt), nie 0.
public sealed record PhysicalDiskRow(int Number, DiskKind Kind, string Model, double? ReadBytesPerSec, double? WriteBytesPerSec,
    double? BusyPercent, double? QueueLength);

// Ein Volume mit Laufwerksbuchstaben. DiskNumber: das physische Laufwerk dahinter; null, wenn Windows es nicht nennt.
// Größen in Byte.
public sealed record VolumeRow(string Letter, string Label, string FileSystem, double TotalBytes, double FreeBytes, int? DiskNumber,
    DiskKind Kind, double? ReadBytesPerSec, double? WriteBytesPerSec, double? BusyPercent, double? QueueLength)
{
    public double UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
}

// Ein Prozess mit Platten-E/A: Lese- und Schreibbytes je Sekunde aus den IO-Zählern des Prozesses. Die Zähler zählen alle
// E/A-Vorgänge des Prozesses, nicht nur die Platte.
public sealed record ProcessIoRow(int Pid, string Name, double ReadBytesPerSec, double WriteBytesPerSec);

// Eine Messung der Laufwerke: die physischen Laufwerke und die Volumes, zu einem Zeitpunkt.
public sealed record DiskSample(DateTime At, IReadOnlyList<PhysicalDiskRow> Disks, IReadOnlyList<VolumeRow> Volumes);
