using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
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

        Access: this machine, the home network (private addresses) and Tailscale (100.64.0.0/10).
        There is no pairing yet: everyone in the home network can read and change the shared folder.
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
// Große Dateien: keine Grenze für den Rumpf (Hochladen); der Abgleich lädt über GET.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = null);
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
app.MapGet("/api/ping", () => Results.Json(new { app = "StykkerMeshShare", module = "meshshare", version }));

// ── Für die anderen PCs ──
app.MapGet("/mesh/hello", () => Results.Json(new Hello("StykkerMeshShare", node.NodeId, node.NodeName, node.Seq)));
app.MapGet("/mesh/changes", (long since) => Results.Json(node.GetChanges(since)));
app.MapGet("/mesh/file", (string path, string hash) =>
{
    var stream = node.OpenFile(path, hash);
    return stream == null ? Results.NotFound() : Results.Stream(stream, "application/octet-stream", enableRangeProcessing: true);
});
app.MapGet("/mesh/wait", async (long since, int? seconds, CancellationToken ct) =>
{
    var timeout = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 25, 1, 60));
    try
    {
        var seq = await node.WaitForChangeAsync(since, timeout, ct);
        return Results.Json(new Hello("StykkerMeshShare", node.NodeId, node.NodeName, seq));
    }
    catch (OperationCanceledException) { return Results.Empty; }
});

// ── Für die Oberfläche ──
app.MapGet("/api/status", (HttpContext ctx, PeerManager peers) =>
{
    var files = node.Files;
    return Results.Json(new
    {
        version,
        node = new
        {
            id = node.NodeId,
            name = node.NodeName,
            folder = node.Folder,
            files = files.Count,
            bytes = files.Sum(f => f.Size),
            seq = node.Seq,
            lastScan = node.LastScan,
            problem = node.FolderProblem,
            heldDeletes = node.HeldDeletes,
            transfer = node.Progress is { } t ? new { path = t.Path, from = t.From, size = t.Size, done = t.Done, resumed = t.Resumed, rate = t.BytesPerSecond, checking = t.Checking } : null,
            pending = node.PendingRescan,
            localOnly,
            // Den Ordner wechselt man nur am PC selbst: der Pfad gilt für diese Maschine.
            canChangeFolder = NetworkAccess.IsLocal(ctx),
        },
        peers = peers.Peers.Select(p => new
        {
            address = p.Address,
            name = p.Name,
            online = p.Online,
            syncing = p.Syncing,
            manual = p.Manual,
            duplicate = p.Duplicate,
            route = p.NodeId == null ? null : StykkerMeshShare.Core.NetworkRoute.Label(p.Route),
            lastSync = p.LastSync,
            error = p.LastError,
        }).ToArray(),
        // Was noch kommt (in dieser Reihenfolge) und was angefangen ist, aber gerade nicht dran.
        queue = node.Queue.Select(q => new { path = q.Path, size = q.Size, from = q.From, done = q.Done }),
        paused = node.Paused.Select(q => new { path = q.Path, size = q.Size, done = q.Done }),
        activity = node.RecentActivity.Take(30).Select(a => new { time = a.Time, kind = a.Kind.ToString().ToLowerInvariant(), path = a.Path, from = a.From }),
    });
});
app.MapGet("/api/files", () => Results.Json(node.Files.Select(f => new
{
    path = f.Path,
    size = f.Size,
    modified = new DateTime(f.MtimeTicks, DateTimeKind.Utc),
    from = f.OriginName,
})));
app.MapGet("/api/download", (string path) =>
{
    var full = PathRules.ToFull(node.Folder, path);
    if (full == null || !File.Exists(full)) return Results.NotFound();
    var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    return Results.File(stream, "application/octet-stream", Path.GetFileName(full), enableRangeProcessing: true);
});
// Hochladen: eine Datei je Anfrage, der Rumpf ist der Inhalt. path ist der Pfad im Ordner (mit Unterordnern beim
// Ablegen eines Ordners). Nur von der eigenen Seite (Origin), damit keine fremde Seite Dateien hineinlegt.
app.MapPost("/api/upload", async (HttpContext ctx, string path, FolderWatcher watcher, CancellationToken ct) =>
{
    if (!LocalRequests.SameOrigin(ctx)) return Results.Json(new { ok = false, message = "Refused: that request did not come from this page." }, statusCode: 403);
    var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = null;
    var name = await node.AddFileAsync(path, ctx.Request.Body, ct);
    if (name == null) return Results.Json(new { ok = false, message = "This name is not allowed in the shared folder." }, statusCode: 400);
    watcher.Soon(200);
    return Results.Json(new { ok = true, path = name });
});
// Ordner wechseln: nur am PC selbst. move = true zieht die Dateien mit um, sonst fängt der neue Ordner leer an und
// holt alles von den anderen PCs. Der neue Ordner wird in settings.json gemerkt.
app.MapPost("/api/folder", (HttpContext ctx, FolderRequest body) => !NetworkAccess.IsLocal(ctx)
    ? Results.Json(new Stykker.Shared.ActionResult(false, "The folder can only be changed on the PC itself."), statusCode: 403)
    : LocalRequests.Guarded(ctx, () =>
    {
        var problem = node.ChangeFolder(body.Path?.Trim() ?? "", body.Move);
        if (problem != null) return new Stykker.Shared.ActionResult(false, problem);
        settings.SetFolder(node.Folder);
        return new Stykker.Shared.ActionResult(true, body.Move ? $"Moved to {node.Folder}." : $"Now using {node.Folder}. The files come from the other PCs.");
    }));
// Reihenfolge der Warteschlange (Ziehen und Ablegen auf der Seite): die Pfade in der gewünschten Reihenfolge.
app.MapPost("/api/queue/order", (HttpContext ctx, OrderRequest body) => LocalRequests.Guarded(ctx, () =>
{
    node.SetOrder(body.Paths ?? []);
    return new Stykker.Shared.ActionResult(true, "Order saved.");
}));
app.MapPost("/api/allow-deletes", (HttpContext ctx) =>
    LocalRequests.Guarded(ctx, () => { node.AllowHeldDeletes(); return new Stykker.Shared.ActionResult(true, "Deletions sent to the other PCs."); }));
app.MapPost("/api/peers", (HttpContext ctx, PeerRequest body, PeerManager peers) => LocalRequests.Guarded(ctx, () =>
{
    var address = ServerSettings.NormalizeAddress(body.Address, port);
    if (address == null) return new Stykker.Shared.ActionResult(false, "Enter a name or an address, for example pc.example.ts.net or 192.0.2.10.");
    peers.AddManual(address);
    return new Stykker.Shared.ActionResult(true, $"Added {address}.");
}));
app.MapPost("/api/peers/remove", (HttpContext ctx, PeerRequest body, PeerManager peers) => LocalRequests.Guarded(ctx, () =>
    peers.Remove(body.Address ?? "") ? new Stykker.Shared.ActionResult(true, "Removed.") : new Stykker.Shared.ActionResult(false, "Not in the list.")));
// Den Ordner im Explorer öffnen: nur am PC selbst sinnvoll.
app.MapPost("/api/open-folder", (HttpContext ctx) => !NetworkAccess.IsLocal(ctx)
    ? Results.Json(new Stykker.Shared.ActionResult(false, "The Explorer opens on the PC. Use this from the PC itself."), statusCode: 403)
    : LocalRequests.Guarded(ctx, () =>
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{node.Folder}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return new Stykker.Shared.ActionResult(false, ex.Message);
        }
        return new Stykker.Shared.ActionResult(true, "Opened in the Explorer.");
    }));

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
}

await app.WaitForShutdownAsync();
if (tray != null && OperatingSystem.IsWindows()) tray.Dispose();

internal sealed record PeerRequest(string? Address);
internal sealed record OrderRequest(string[]? Paths);
internal sealed record FolderRequest(string? Path, bool Move);
