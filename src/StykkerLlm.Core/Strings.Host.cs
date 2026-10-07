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
    public const string HostRefused = "The server refused this host (removed there, or Home/VPN is off)";
    public static string HostConnected(string server) => $"Connected to {server}";
    public static string HostConnecting(string server) => $"Connecting to {server} …";
    public static string HostWaiting(string error) => $"No connection: {error}";
    public const string HostNotFound = "No such host.";
}
