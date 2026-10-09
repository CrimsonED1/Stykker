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
    public const string HostNotConnected = "The host is not connected.", HostNoAnswer = "The host did not answer in time.";
    public static string HostGone(string name) => $"The connection to host {name} was lost.";
    public static string HostCommandUnknown(string name) => $"Unknown command: {name}";
    public static string HostModelsFound(int n) => n == 1 ? "1 model file" : $"{n} model files";
    public const string HostNoSuchServer = "No such server on the host.";
    public static string HostStarted(string name) => $"{name} is starting on the host.";
    public static string HostStopped(string name) => $"{name} stopped.";
    public static string HostUnloaded(string model) => $"{model} unloaded.";
    public static string HostStartedOn(string name, int port) => $"{name} is starting on the host (port {port}).";
    public static string HostNoModelFile(string path) => $"The host has no file {path}.";
    public const string HostsModelsButton = "Model files", HostsModelsLoading = "Reading the model folders on the host …";
    public const string HostsUnload = "Unload", SectionOnHosts = "On model hosts", HostBadgeTip = "Runs on this model host";
    public const string HostsNothingRunning = "No model server running on this host.";
    public const string HostsNoModels = "No GGUF files in the host's model folders (host-settings.json → ModelRoots).";
    public static string HostNoVramFor(string model, double needGb, double freeGb) =>
        $"No model host has enough free graphics memory for {model} (needs about {needGb.ToString("0.0", Inv)} GB, at most {freeGb.ToString("0.0", Inv)} GB free).";
    public static string HostStartTimeout(string model, string host) => $"{model} was started on {host} but is not ready yet. Try again in a moment.";
    public const string HostTunnelOnlyLocal = "The host only forwards requests to model servers on its own PC.";
    public static string HostProgramNotAllowed(string program) => $"The host does not start '{program}' (not a model server; allow it in the host settings).";
    // Windows-Dienst (P8)
    public const string HostServiceDescription = "Runs local model servers for a StykkerLLM server (model host, no window).";
    public const string HostServiceNeedsAdmin = "This needs administrator rights: open a terminal as administrator and run the command again.";
    public const string HostServiceUsage = "StykkerHost install-service | uninstall-service | pair-service <server> <code> | unpair-service";
    public const string HostServiceInstalled = "StykkerHost runs as a Windows service now (starts with Windows, restarts after a crash). Pair it with: StykkerHost pair-service <server> <code>";
    public const string HostServiceTrayHint = "Quit the StykkerHost tray icon and turn off its \"Start with Windows\" – otherwise this PC shows up twice on the server.";
    public const string HostServiceRemoved = "The StykkerHost service is removed. Its data folder stays: ";
    public static string HostServiceStepFailed(string step, int code, string output) => $"sc {step} failed ({code}): {output}";
    public const string HostServiceNoAnswer = "The service did not answer. Is it installed and running? (services.msc → StykkerHost)";
    public const string HostServiceUnpaired = "The service is unpaired.";
    // Kopplung (Host-Seite)
    public const string HostPairMenu = "Pair with a server …", HostUnpairMenu = "Unpair";
    public const string HostPairTitle = "Pair StykkerHost";
    public const string HostPairHint = "On the server open ☰ → Hosts. It shows a six-digit code. Pick the server (or type its address) and enter the code.";
    public const string HostPairFound = "Servers in this network", HostPairSearching = "Searching …";
    public const string HostPairNoneFound = "None found – type the address (Home/VPN must be on at the server).";
    public const string HostPairRemoteOff = "Home/VPN off";
    public const string HostPairServer = "Server address", HostPairCode = "Code from the server", HostPairButton = "Pair";
    public const string HostPairBadUrl = "That is not a server address (http://name:8078).";
    public static string HostPairDone(string server) => $"Paired with {server}. The host connects now.";
    public static string HostPairFailed(string status) => $"Pairing failed ({status}).";
    public static string HostPairUnreachable(string detail) => $"The server is not reachable: {detail}";
    public const string HostPairWrongCode = "That code does not fit or has expired. Look at ☰ → Hosts on the server.";
    public const string HostPairClose = "You can close this page.", HostPairAgain = "Try again";
    // Kopplung (Server-Seite, Seite Hosts)
    public const string HostsTitle = "Hosts";
    public const string HostsHint = "Model hosts are PCs that only run models for this server (StykkerHost). On the host: tray icon → Pair with a server …, then enter this code (as a Windows service: StykkerHost pair-service <server> <code>).";
    public const string HostsCodeLabel = "Code for a new host", HostsNewCode = "New code";
    public const string HostsNone = "No host paired yet.", HostsThisPc = "(this PC)";
    public const string HostsRemoteHint = "Hosts in the network need Home/VPN on (☰ → Phone access).";
    public const string HostOnline = "connected", HostOffline = "not connected";
    public static string HostServers(int n) => n == 1 ? "1 server" : $"{n} servers";
}
