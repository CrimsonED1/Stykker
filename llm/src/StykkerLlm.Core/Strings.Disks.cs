namespace StykkerLlm.Core;

// Die Texte der Laufwerke: die Karte „Disks“ im Monitor (English, wie alle Oberflächentexte; die Laufwerke im Detail zeigt /hardware)
public static partial class Strings
{
    public const string DisksCard = "Disks", DisksDetails = "details", DisksMeasuring = "measuring …", DisksVolume = "Drive",
        DisksFree = "free", DisksRead = "read", DisksWrite = "write", DisksBusy = "busy", DisksSize = "Size",
        DisksVolumes = "Drives", DisksNoRates = "Rates appear from the second measurement on.";

    // Hinweis auf der Karte: ein Modellserver liest viel, mit Laufwerk und ohne
    public static string DiskServerReads(string server, string rate, string drive) => $"{server} reads {rate} from {drive}";
    public static string DiskServerReadsAnywhere(string server, string rate) => $"{server} reads {rate}";
}
