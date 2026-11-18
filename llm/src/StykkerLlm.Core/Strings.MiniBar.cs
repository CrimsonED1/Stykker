namespace StykkerLlm.Core;

// Die Texte der Hardware-Minibar (MiniBar.cs) und ihres Schalters im ☰ → Ansicht
public static partial class Strings
{
    public const string MiniVram = "VRAM", MiniGpu = "GPU", MiniCpu = "CPU", MiniRam = "RAM", MiniDisk = "Disk",
        MiniIo = "Disk I/O", MiniNet = "Network", MiniTemp = "Temp",
        MiniBarLabel = "Hardware bar", MiniBarTip = "Show VRAM, GPU, CPU, RAM, disks, network and GPU temperature at the top of every page except the monitor",
        MiniBarAria = "Hardware", MiniNoGpu = "No graphics card measured", MiniNoSystem = "No system counters",
        MiniNoDisks = "No disk counters on this system", MiniNoNet = "Network rates appear from the second measurement on";

    public static string MiniVramTip(double used, double total, string gpu) => $"{gpu}: {N1(used)} of {N1(total)} GB used";
    public static string MiniGbFree(double gb) => $"{N1(gb)} GB free";
    public static string MiniRamTip(double used, double total) => $"{N1(used)} of {N1(total)} GB used";
    public static string MiniDiskTip(string letter, string free, string total) => $"Fullest drive {letter}: {free} free of {total}";
    public static string MiniIoTip(string read, string write, double? busy) =>
        $"All disks: read {read}, write {write}" + (busy is double b ? $" · busiest disk {b.ToString("0", Inv)}% busy" : "");
    public static string MiniNetTip(string rx, string tx, string? link) =>
        $"All network adapters: received {rx}, sent {tx}" + (link != null ? $" · link {link}" : "");

    // Verlauf hinter jedem Feld (MiniHistory) und das Auf- und Zuklappen auf dem Telefon
    public static string MiniPeak(double fraction, int readings) => $"Highest of the last {readings} readings: {(fraction * 100).ToString("0", Inv)}%";
    public static string MiniMore(int hidden) => $"+{hidden}";
    public const string MiniMoreTip = "Show all hardware values", MiniLess = "Less", MiniLessTip = "Show only VRAM, GPU and temperature";

    // Ereignisse als Meldung auf den anderen Seiten (der Monitor hat die Liste selbst)
    public const string ToastShowActivity = "Show";
    public static string CmdPrompt(string name) => $"Prompt: {name}";
}
