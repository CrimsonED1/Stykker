using Stykker.Pairing;
using Stykker.Shared;
using Stykker.Shared.Config;
using Stykker.Shared.Web;
using Stykker.Shared.Windows;
using StykkerSys.Core;
using StykkerSys.Platform.Windows;
using StykkerSys.Server.Components;

// StykkerSYS-Server: die Prozessliste dieser Maschine als Web-Oberfläche auf http://127.0.0.1:8077.
// Nur auf der eigenen Maschine erreichbar – eine Liste mit „Beenden" hat im Netz nichts zu suchen. Das Fenster
// (StykkerSYS.exe) zeigt dieselben Seiten.
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        StykkerSYS-Server - the processes of this machine: CPU, memory and GPU per process

          --port <number>   web port (default 8077)
          --no-tray         no tray icon (the window starts the server this way: it has an icon itself)
          --no-browser      do not open a browser on start
          --basic           no system access: GPU values and file paths stay empty
          --design-system <folder>
                            the shared design system served under /ds
                            (default: shared/design-system of the repository this program is in)
          --hub-config <file>
                            the hub's stykker.json instead of %APPDATA%\Stykker\stykker.json (trial runs);
                            only its "hostNames" are read here
          --pairing-file <file>
                            the pairing store instead of %APPDATA%\Stykker\pairing.dat (trial runs)
          --hub-port <number>
                            the hub's port (default 8079): only the hub may show this page in a frame
          --help            this text

        Endpoints: / (web interface), /api/snapshot (the process list as JSON), and the local-only
        POST /api/process/end, /api/process/priority, /api/process/open.

        The server binds the port immediately and reads nothing until a viewer appears: the list starts
        with the first request and stops again 15 seconds after the last one. A closed window therefore
        costs nothing.
        """);
    return;
}

int port = int.TryParse(args.SkipWhile(a => a != "--port").Skip(1).FirstOrDefault(), out var parsedPort) ? parsedPort : 8077;
bool noBrowser = args.Contains("--no-browser");
bool basic = args.Contains("--basic");
string url = $"http://127.0.0.1:{port}/";
// Die gemeinsame Kopplung aller Module (docs/plan-stykkernode.md). --pairing-file nimmt eine andere Ablage (Versuche).
var pairing = new PairingService(new PairingStore(args.SkipWhile(a => a != "--pairing-file").Skip(1).FirstOrDefault() ?? PairingStore.DefaultFile()));
// Im Netz nur, wenn der Netzschalter beim Start an ist (er wird in der Zentrale umgelegt und wirkt nach dem nächsten Start).
bool network = pairing.RemoteEnabled;
// Port der Zentrale, die diese Seite in einem Frame zeigen darf
int hubPort = int.TryParse(args.SkipWhile(a => a != "--hub-port").Skip(1).FirstOrDefault(), out var parsedHub) ? parsedHub : 8079;

// Ein Server je Port: läuft schon einer, öffnet ein zweiter Start nur die Oberfläche.
using var single = ToolHost.ClaimPort("StykkerSYS", port, out bool first);
if (!first)
{
    if (!noBrowser) ToolHost.OpenBrowser(url);
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
// Ohne Netzschalter nur die eigene Maschine. Mit ihm auch das Netz – dort prüft PairingGate jede Anfrage.
builder.WebHost.UseUrls(network ? $"http://*:{port}" : $"http://127.0.0.1:{port}");
// Aus dem Build-Ordner (nicht veröffentlicht): CSS und die Seiten aus den Quellen statt aus wwwroot
if (!Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))) builder.WebHost.UseStaticWebAssets();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton<IProcessProbe>(_ => CreateProbe(basic));
builder.Services.AddSingleton(sp => new ProcessSampler(sp.GetRequiredService<IProcessProbe>()));
builder.Services.AddSingleton<SysService>();
builder.Services.AddRazorComponents();
// Alle Zahlen der Antworten auf eine Nachkommastelle (siehe OneDecimalJson).
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new OneDecimalJson()));

var app = builder.Build();

// Die Liste wird hier absichtlich NICHT angefasst: sie entsteht beim ersten Abruf und liest nur, solange jemand
// zusieht (siehe SysService). So ist der Server nach dem Start sofort da, auch wenn noch kein Fenster offen ist.

// Zusatznamen für den Host-Kopf aus stykker.json ("hostNames"). --hub-config nimmt eine andere Datei, wie bei der Zentrale.
var extraHostNames = HostNames.LoadExtra(args.SkipWhile(a => a != "--hub-config").Skip(1).FirstOrDefault() ?? HubModules.DefaultFile());
// Zugang: diese Maschine ohne Anmeldung, das Netz nur mit Netzschalter und gekoppeltem Gerät. Steht vor allem anderen.
PairingGate.Use(app, pairing, new PairingGateOptions("sys", "StykkerSYS", ExtraHostNames: extraHostNames));
FrameAncestors.Use(app, hubPort);
app.UseStaticFiles();
DesignSystemHost.Map(app, DesignSystemHost.Resolve(args, "STYKKERSYS_DESIGN_SYSTEM"));

// Die Seiten der Razor-Komponenten tragen Anti-Fälschungs-Metadaten: ohne diese Middleware antwortet "/" mit 500.
app.UseAntiforgery();
app.MapGet("/api/snapshot", (SysService service) => Results.Json(service.Snapshot()));

// ── Aktionen ──
// Drei Türen, alle nur für diese Maschine und nur für die eigene Seite: der Rumpf muss JSON sein (ein fremdes
// Formular kann das ohne CORS-Prüfung nicht senden) und die Herkunft muss die eigene sein. Zusätzlich bleiben
// System-PIDs und dieses Werkzeug selbst gesperrt (siehe ProcessActions).
// Ein Gerät aus dem Netz braucht dafür die Rolle admin in SYS; ein neu gekoppeltes Gerät ist Viewer und sieht nur zu.
IResult AdminOnly(HttpContext ctx, Func<ActionResult> action) => PairingGate.IsAdmin(ctx)
    ? LocalRequests.Guarded(ctx, action)
    : Results.Json(new ActionResult(false, "This device may only watch. The PC can make it an admin of SYS in the Stykker hub."), statusCode: StatusCodes.Status403Forbidden);
app.MapPost("/api/process/end", (HttpContext ctx, ProcessEndRequest body) => AdminOnly(ctx, () => ProcessActions.End(body.Pid, body.Tree)));
app.MapPost("/api/process/priority", (HttpContext ctx, ProcessPriorityRequest body) => AdminOnly(ctx, () => ProcessActions.SetPriority(body.Pid, body.Level ?? "")));
// Der Pfad kommt von der Sonde, wie in der Liste: so findet „Ordner öffnen" jeden Pfad, den die Liste zeigt.
// Der Explorer öffnet sich am PC: das hat nur von dort aus Sinn.
app.MapPost("/api/process/open", (HttpContext ctx, ProcessOpenRequest body, IProcessProbe probe) => !PairingGate.IsLocal(ctx)
    ? Results.Json(new ActionResult(false, "The Explorer opens on the PC. Use this from the PC itself."), statusCode: StatusCodes.Status403Forbidden)
    : LocalRequests.Guarded(ctx, () =>
{
    string? path = probe.PathOf(body.Pid);
    if (path == null) return new ActionResult(false, $"No file path for PID {body.Pid}: Windows gave none, or the process is protected.");
    if (!OperatingSystem.IsWindows()) return new ActionResult(false, "Opening a folder is a Windows-only helper.");
    return Shell.RevealFile(path) ? new ActionResult(true, $"Shown in the Explorer: {path}") : new ActionResult(false, "The Explorer did not take the request.");
}));

app.MapRazorComponents<App>();

try { await app.StartAsync(); }
catch (IOException ex)
{
    // Port belegt: sagen statt abstürzen
    Console.Error.WriteLine($"port {port} is busy: {ex.Message}");
    Environment.ExitCode = 1;
    return;
}
if (!noBrowser) ToolHost.OpenBrowser(url);

// Tray-Symbol: ohne Fenster wäre der Server sonst nur über seine Adresse zu finden. Fehler sind kein Grund für
// einen Abbruch – dann läuft er eben ohne Symbol.
TrayIcon? tray = null;
if (OperatingSystem.IsWindows() && !args.Contains("--no-tray") && TrayIcon.Possible)
{
    tray = new TrayIcon(
        new[]
        {
            new TrayIcon.Item(1, "Open StykkerSYS"),
            new TrayIcon.Item(2, "Shut down"),
        },
        id =>
        {
            if (id == 1) ToolHost.OpenBrowser(url);
            else if (id == 2) app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        },
        () => ToolHost.OpenBrowser(url));
    if (!tray.TryShow($"StykkerSYS – {url}", out var trayError)) Console.Error.WriteLine($"[tray] {trayError}");
}

await app.WaitForShutdownAsync();
if (tray != null && OperatingSystem.IsWindows()) tray.Dispose();

// Die Windows-Sonde nur auf Windows; sonst (oder mit --basic) bleiben GPU-Werte und Pfade leer.
static IProcessProbe CreateProbe(bool basic)
{
    if (basic) return new BasicProcessProbe();
    if (!OperatingSystem.IsWindows()) return new BasicProcessProbe();
    return new WindowsProcessProbe();
}

internal sealed record ProcessEndRequest(int Pid, bool Tree);
internal sealed record ProcessPriorityRequest(int Pid, string? Level);
internal sealed record ProcessOpenRequest(int Pid);
