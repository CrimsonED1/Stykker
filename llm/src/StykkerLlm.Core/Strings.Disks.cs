namespace StykkerLlm.Core;

// Die Texte der Laufwerke: die Karte „Disks“ im Monitor und die Seite /disks (English, wie alle Oberflächentexte)
public static partial class Strings
{
    public const string DisksCard = "Disks", DisksDetails = "details", DisksPageTitle = "Disk details", DisksNoData = "No disk counters on this system.",
        DisksMeasuring = "measuring …", DisksVolume = "Drive", DisksType = "Type", DisksFree = "free", DisksUsed = "used",
        DisksRead = "read", DisksWrite = "write", DisksBusy = "busy", DisksQueue = "queue", DisksSize = "Size",
        DisksPhysical = "Physical disks", DisksVolumes = "Drives", DisksPrograms = "Programs with the most disk traffic",
        DisksProgramsNone = "No program reads or writes the disks right now.",
        DisksHistory = "History (last 5 minutes)", DisksModel = "Model", DisksNoRates = "Rates appear from the second measurement on.",
        DisksDetailsHint = "Every drive, its type and the programs with the most disk traffic. Refreshes every second. The traffic counts all reads and writes of a program, not only the disk.",
        DisksNoHistory = "The history starts with the first measurement.",
        DisksProgramsHint = "Reads and writes per second of every program. The counter counts all input and output of a program, not only the disk.";

    // Hinweis auf der Karte: ein Modellserver liest viel, mit Laufwerk und ohne
    public static string DiskServerReads(string server, string rate, string drive) => $"{server} reads {rate} from {drive}";
    public static string DiskServerReadsAnywhere(string server, string rate) => $"{server} reads {rate}";
}
