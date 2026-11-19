using System.Globalization;

namespace StykkerLlm.Core;

// Die Texte der Detailseite eines Servers (Pages/Server.razor): Kopf, Kennzahlen, Optionen, Prozess
public static partial class Strings
{
    public const string DetAria = "Server details",
        DetOnline = "online", DetOffline = "not reachable",
        DetNowTps = "Writing now", DetPrompt = "Reading the prompt", DetGenerated = "Generated", DetMemory = "Memory",
        DetIdleTile = "Idle", DetIdleNow = "working or only a short pause",
        DetOptions = "Options", DetNoOptions = "No options on the command line.",
        DetRecord = "Record", DetSave = "Save",
        DetFullCommand = "Full command line", DetCopy = "Copy", DetCopied = "Copied",
        DetServer = "Process and model";

    public static string DetPeakAvg(double peak, double avg) =>
        $"peak {peak.ToString("0.0", CultureInfo.InvariantCulture)} · avg {avg.ToString("0.0", CultureInfo.InvariantCulture)}";
    public const string DetTokensSince = "tokens since the start";
    public static string DetTtft(string seconds, bool measured) => $"first token {(measured ? "" : "≈ ")}{seconds} s";
    public static string DetCtx(int ctx) => $"{ctx.ToString("N0", CultureInfo.InvariantCulture)} context per slot";
    public static string DetSlots(int busy, int n) => n == 1 ? (busy > 0 ? "1 slot · busy" : "1 slot") : $"{n} slots · {busy} busy";
    public static string DetPort(int port) => $"port {port.ToString(CultureInfo.InvariantCulture)}";
    public static string DetPid(int pid) => $"PID {pid.ToString(CultureInfo.InvariantCulture)}";
    public static string DetRam(string ram) => $"RAM {ram}";
}
