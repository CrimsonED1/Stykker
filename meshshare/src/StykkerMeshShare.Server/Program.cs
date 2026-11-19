using Stykker.Pairing;
using Stykker.Shared.Web;
using Stykker.Shared.Windows;
using StykkerMeshShare.Core;
using StykkerMeshShare.Server;
using StykkerMeshShare.Server.Components;

// StykkerMeshShare-Server: ein geteilter Ordner auf jedem PC. Jeder PC gleicht seinen Ordner mit allen anderen ab,
// die er kennt (von Hand eingetragen oder im Heimnetz gefunden). Es gibt keinen zentralen Server.
// Die Oberfläche (Dropzone, Sync-Status, Dateien) ist dieselbe im Fenster, im Browser und am Handy.
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        StykkerMeshShare-Server - a shared folder on every PC, synced with all others in the network

          --port <number>     web and sync port (default 17404); the LAN search uses the same number over UDP
          --folder <folder>   the shared folder (default: %USERPROFILE%\StykkerMeshShare, or settings.json)
          --data-dir <folder> index, trash and settings (default: %APPDATA%\Stykker\MeshShare)
          --name <text>       the name other PCs see (default: this computer's name)
          --peer <host[:port]>
                              another PC to sync with, in addition to settings.json (may repeat)
          --no-discovery      do not search the home network (only the peers you entered)
          --local-only        listen on this machine only and do not search the network
                              (trial runs: two servers on one PC with --peer 127.0.0.1:<port>)
          --no-tray           no tray icon (the window starts the server this way)
          --no-browser        do not open a browser on start
          --design-system <folder>
                              the shared design system served under /ds
          --hub-port <number> the hub's port (default 17400): only the hub may show this page in a frame
          --help              this text

        Access: this machine, the home network (private addresses) and Tailscale (100.64.0.0/10), and only under
        this PC's own names (IP address, computer name, <name>.<tailnet>.ts.net, or hostNames in stykker.json).
        There is no pairing yet: everyone in the home network can read and add files. Changing the folder, the list
        of PCs and confirming held deletions work only on the PC itself.
        """);
    return;
}

string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
IEnumerable<string> Args(string name)
{
    for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) yield return args[i + 1];
}

int port = int.TryParse(Arg("--port"), out var parsedPort) ? parsedPort : StykkerPorts.MeshShare;
int hubPort = int.TryParse(Arg("--hub-port"), out var parsedHub) ? parsedHub : StykkerPorts.Hub;
bool noBrowser = args.Contains("--no-browser");
bool localOnly = args.Contains("--local-only");
string url = $"http://127.0.0.1:{port}/";
string dataDir = Path.GetFullPath(Arg("--data-dir") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stykker", "MeshShare"));
Directory.CreateDirectory(dataDir);
var settings = new ServerSettings(dataDir);
string folder = Path.GetFullPath(Arg("--folder") ?? settings.Folder ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "StykkerMeshShare"));
if (Arg("--folder") != null) settings.SetFolder(folder);
string nodeName = Arg("--name") ?? Environment.MachineName;

// Ein Server je Port: läuft schon einer, öffnet ein zweiter Start nur die Oberfläche.
using var single = ToolHost.ClaimPort("StykkerMeshShare", port, out bool first);
if (!first)
{
    if (!noBrowser) ToolHost.OpenBrowser(url);
    return;
}

var node = new MeshNode(new MeshOptions { Folder = folder, DataDir = dataDir, NodeName = nodeName });

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseUrls(localOnly ? $"http://127.0.0.1:{port}" : $"http://*:{port}");
// Die Grenze für den Rumpf bleibt (30 MB); nur /api/upload hebt sie für sich auf.
if (!Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))) builder.WebHost.UseStaticWebAssets();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton(node);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<FolderWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FolderWatcher>());
builder.Services.AddSingleton(sp => new PeerManager(node, settings, port, !localOnly && !args.Contains("--no-discovery"), Args("--peer")));
builder.Services.AddHostedService(sp => sp.GetRequiredService<PeerManager>());
builder.Services.AddRazorComponents();

var app = builder.Build();

// Zugang: nur das eigene Netz (bis zur Kopplung). Steht vor allem anderen.
NetworkAccess.Use(app);
FrameAncestors.Use(app, hubPort);
app.UseStaticFiles();
DesignSystemHost.Map(app, DesignSystemHost.Resolve(args, "STYKKERMESHSHARE_DESIGN_SYSTEM"));
app.UseAntiforgery();

var version = typeof(MeshNode).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
MeshEndpoints.Map(app, node);
UiEndpoints.Map(app, node, settings, port, localOnly, version);

app.MapRazorComponents<App>();

try { await app.StartAsync(); }
catch (IOException ex)
{
    Console.Error.WriteLine($"port {port} is busy: {ex.Message}");
    Environment.ExitCode = 1;
    return;
}
Console.WriteLine($"StykkerMeshShare: {url} – folder {folder}");
if (!noBrowser) ToolHost.OpenBrowser(url);

TrayIcon? tray = null;
if (OperatingSystem.IsWindows() && !args.Contains("--no-tray") && TrayIcon.Possible)
{
    tray = new TrayIcon(
        new[]
        {
            new TrayIcon.Item(1, "Open StykkerMeshShare"),
            new TrayIcon.Item(2, "Open the shared folder"),
            new TrayIcon.Item(3, "Shut down"),
        },
        id =>
        {
            if (id == 1) ToolHost.OpenBrowser(url);
            else if (id == 2) ToolHost.OpenBrowser(node.Folder);
            else if (id == 3) app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        },
        () => ToolHost.OpenBrowser(url));
    if (!tray.TryShow($"StykkerMeshShare – {url}", out var trayError)) Console.Error.WriteLine($"[tray] {trayError}");
    else TrayNotices.Start(node, tray, app.Lifetime.ApplicationStopping);
}

await app.WaitForShutdownAsync();
if (tray != null && OperatingSystem.IsWindows()) tray.Dispose();

