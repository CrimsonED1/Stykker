using System.Net.NetworkInformation;
using System.Net.Sockets;
using Stykker.Pairing;
using Stykker.Shared.Config;
using Stykker.Shared.Web;
using Stykker.Shared.Windows;
using StykkerHud.Core;
using StykkerHud.Platform.Windows;
using StykkerHud.Server.Components;

// StykkerHUD-Server: die Messwerte dieser Maschine als Web-Oberfläche auf http://127.0.0.1:8079.
// Nur auf der eigenen Maschine erreichbar – ein Ressourcenmonitor hat im Netz nichts zu suchen. Das Fenster
// (StykkerHUD.exe) zeigt dieselben Seiten.
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        StykkerHUD-Server - CPU, memory, GPU, storage and network of this machine

          --port <number>   web port (default 8079)
          --no-tray         no tray icon (the window starts the server this way: it has an icon itself)
          --no-browser      do not open a browser on start
          --basic           no system access: every value shows "-" (for trying that mode on Windows)
          --design-system <folder>
                            the shared design system served under /ds
                            (default: shared/design-system of the repository this program is in)
          --help            this text

        Endpoints: / (the monitor), /hub (the hub: every Stykker module under one bar, each in a frame),
        /api/snapshot (the current reading and the history as JSON), /api/hub (the modules and whether they run).

        The hub lists the modules from %APPDATA%\Stykker\stykker.json; without that file SYS (8077) and LLM (8078).

        The server binds the port immediately and reads nothing until a viewer appears: the measuring loop
        starts with the first request and stops again 15 seconds after the last one. A closed window therefore
        costs nothing.
        """);
    return;
}

int port = int.TryParse(args.SkipWhile(a => a != "--port").Skip(1).FirstOrDefault(), out var parsedPort) ? parsedPort : 8079;
bool noBrowser = args.Contains("--no-browser");
bool basic = args.Contains("--basic");
string url = $"http://127.0.0.1:{port}/";
// Die gemeinsame Kopplung aller Module (docs/plan-stykkernode.md). --pairing-file nimmt eine andere Ablage (Versuche).
var pairing = new PairingService(new PairingStore(args.SkipWhile(a => a != "--pairing-file").Skip(1).FirstOrDefault() ?? PairingStore.DefaultFile()));
// Der Port wird nur dann im Netz geöffnet, wenn der Netzschalter beim Start an ist. Ein Umschalten wirkt nach dem
// nächsten Start: so fragt die Windows-Firewall nicht bei jedem, der den Monitor nur lokal benutzt.
bool network = pairing.RemoteEnabled;

// Ein Server je Port: läuft schon einer, öffnet ein zweiter Start nur die Oberfläche.
using var single = ToolHost.ClaimPort("StykkerHUD", port, out bool first);
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
builder.Services.AddSingleton<ISystemProbe>(_ => CreateProbe(basic));
builder.Services.AddSingleton(sp => new MetricsSampler(sp.GetRequiredService<ISystemProbe>()));
builder.Services.AddSingleton<HudService>();
builder.Services.AddRazorComponents();
// Alle Zahlen der Antworten auf eine Nachkommastelle (siehe OneDecimalJson).
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new OneDecimalJson()));

var app = builder.Build();

// Die Messschleife wird hier absichtlich NICHT angefasst: sie entsteht beim ersten Abruf und liest nur, solange
// jemand zusieht (siehe HudService). So ist der Server nach dem Start sofort da, auch wenn noch kein Fenster offen ist.

// Zugang: diese Maschine ohne Anmeldung, das Netz nur mit Netzschalter und gekoppeltem Gerät. Steht vor allem anderen.
PairingGate.Use(app, pairing, new PairingGateOptions("hud", "StykkerHUD", Home: "/hub"));
FrameAncestors.Use(app, hubPort: 0);   // die Zentrale zeigt sich nur selbst in einem Frame (den Monitor)
app.UseStaticFiles();
DesignSystemHost.Map(app, DesignSystemHost.Resolve(args, "STYKKERHUD_DESIGN_SYSTEM"));

// Die Seiten der Razor-Komponenten tragen Anti-Fälschungs-Metadaten: ohne diese Middleware antwortet "/" mit 500.
app.UseAntiforgery();
app.MapGet("/api/snapshot", (HudService service) => Results.Json(service.Snapshot()));

// Die Zentrale: Leiste oben, das gewählte Modul im Frame darunter (docs/plan-stykkernode.md). Die Module stehen in
// stykker.json; ohne Datei gilt die Suite auf ihren festen Ports. Eigene Module hängen am Rechnernamen der Anfrage.
// Über den Dateianbieter statt über einen Pfad: aus dem Build-Ordner liegen die Dateien in den Quellen, nicht in wwwroot.
IResult WebFile(string name, string contentType)
{
    var file = app.Environment.WebRootFileProvider.GetFileInfo(name);
    return file.Exists ? Results.Stream(file.CreateReadStream(), contentType) : Results.NotFound();
}
app.MapGet("/hub", () => WebFile("hub.html", "text/html; charset=utf-8"));
app.MapGet("/manifest.webmanifest", () => WebFile("manifest.webmanifest", "application/manifest+json"));
app.MapGet("/api/hub", async (HttpContext context) =>
{
    var modules = HubModules.Load();
    var host = context.Request.Host.Host;
    var answers = await Task.WhenAll(modules.Select(async module =>
    {
        var address = module.AddressFor(context.Request.Scheme, host);
        // Ohne eigene Adresse ist der Eintrag die Zentrale selbst: sie antwortet gerade, also läuft sie.
        bool up = address == null || await HubProbe.AnswersAsync(module, address, context.RequestAborted);
        return new { id = module.Id, name = module.Name, url = address ?? "/", external = module.External, up };
    }));
    return Results.Json(new { modules = answers });
});

// ── Zugang verwalten: Netzschalter, Code, Geräte. Nur an dieser Maschine und nur von der eigenen Seite. ──
bool Local(HttpContext context) => PairingGate.IsLocal(context) && LocalRequests.SameOrigin(context);
app.MapGet("/api/hub/access", (HttpContext context) =>
{
    // Ein Gerät im Netz erfährt hier nur, dass es nicht an der Maschine sitzt: kein Code, keine Geräteliste.
    if (!PairingGate.IsLocal(context)) return Results.Json(new { local = false });
    pairing.Maintain();
    return Results.Json(new
    {
        local = true,
        remote = pairing.RemoteEnabled,
        listening = network,
        code = PairingCodes.Pretty(pairing.Code),
        expiresIn = (int)Math.Max(0, (pairing.CodeExpires - DateTime.UtcNow).TotalSeconds),
        addresses = LanAddresses().Select(address => $"http://{address}:{port}/hub"),
        devices = pairing.Devices.Select(d => new
        {
            id = d.Id, name = d.Name, address = d.Address, lastSeen = d.LastSeen,
            roles = new { hud = d.RoleIn("hud"), sys = d.RoleIn("sys"), llm = d.RoleIn("llm") },
        }),
    });
});
app.MapPost("/api/hub/remote", (HttpContext context, bool on) =>
{
    if (!Local(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    pairing.SetRemote(on);
    return Results.Json(new { remote = on });
});
app.MapPost("/api/hub/code", (HttpContext context) =>
{
    if (!Local(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    pairing.RotateCode();
    return Results.Json(new { ok = true });
});
app.MapPost("/api/hub/devices/{id}/role", (HttpContext context, string id, string module, string role) =>
    !Local(context) ? Results.StatusCode(StatusCodes.Status403Forbidden)
    : pairing.SetRole(id, module, role) ? Results.Json(new { ok = true }) : Results.NotFound());
app.MapDelete("/api/hub/devices/{id}", (HttpContext context, string id) =>
    !Local(context) ? Results.StatusCode(StatusCodes.Status403Forbidden)
    : pairing.RemoveDevice(id) ? Results.Json(new { ok = true }) : Results.NotFound());

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
            new TrayIcon.Item(1, "Open StykkerHUD"),
            new TrayIcon.Item(2, "Shut down"),
        },
        id =>
        {
            if (id == 1) ToolHost.OpenBrowser(url);
            else if (id == 2) app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        },
        () => ToolHost.OpenBrowser(url));
    if (!tray.TryShow($"StykkerHUD – {url}", out var trayError)) Console.Error.WriteLine($"[tray] {trayError}");
}

await app.WaitForShutdownAsync();
if (tray != null && OperatingSystem.IsWindows()) tray.Dispose();

// Die IPv4-Adressen dieser Maschine im Netz, unter denen ein Handy die Zentrale öffnen kann.
static IEnumerable<string> LanAddresses() =>
    NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
        .Select(a => a.Address.ToString())
        .Distinct();

// Die Windows-Sonde nur auf Windows; sonst (oder mit --basic) antwortet die Anzeige überall „–".
static ISystemProbe CreateProbe(bool basic)
{
    if (basic) return new BasicProbe();
    if (!OperatingSystem.IsWindows()) return new BasicProbe();
    return new WindowsProbe();
}
