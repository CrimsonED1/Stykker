namespace StykkerLlm.Core;

// Texte des Model-Hosts (StykkerHost): Tray-Menü, Tooltip, Protokoll
public static partial class Strings
{
    public const string HostName = "StykkerHost";
    public static string HostRunning(int n) => n == 1 ? "1 running" : $"{n} running";
    public const string HostNoGpu = "No NVIDIA GPU";
    public const string HostNotPaired = "Not paired with a server yet";
    public const string HostAutostart = "Start with Windows", HostAutostartOn = "✓ Start with Windows";
    public const string HostOpenLog = "Open log", HostQuit = "Quit StykkerHost";
    public const string HostAlreadyRunning = "StykkerHost already runs for this data folder.";
    public const string HostTrayFailed = "StykkerHost runs without a tray icon";
}
