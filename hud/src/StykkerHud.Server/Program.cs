using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Stykker.Pairing;
using Stykker.Shared.Config;
using Stykker.Shared.Web;
using Stykker.Shared.Windows;
using StykkerHud.Core;
using StykkerHud.Platform.Windows;
using StykkerHud.Server;
using StykkerHud.Server.Components;

// StykkerHUD-Server: die Messwerte dieser Maschine als Web-Oberfläche auf http://127.0.0.1:17400.
// Nur auf der eigenen Maschine erreichbar – ein Ressourcenmonitor hat im Netz nichts zu suchen. Das Fenster
// (StykkerHUD.exe) zeigt dieselben Seiten.
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        StykkerHUD-Server - CPU, memory, GPU, storage and network of this machine

          --port <number>   web port (default 17400)
          --no-tray         no tray icon (the window starts the server this way: it has an icon itself)
          --no-browser      do not open a browser on start
          --basic           no system access: every value shows "-" (for trying that mode on Windows)
          --design-system <folder>
                            the shared design system served under /ds
                            (default: shared/design-system of the repository this program is in)
          --hub-config <file>
                            the hub's modules instead of %APPDATA%\Stykker\stykker.json (trial runs)
          --pairing-file <file>
                            the pairing store instead of %APPDATA%\Stykker\pairing.dat (trial runs)
          --help            this text

        Endpoints: / (the monitor), /hub (the hub: every Stykker module under one bar, each in a frame),
        /api/snapshot (the current reading and the history as JSON), /api/hub (the modules and whether they run).

        The hub lists the modules from %APPDATA%\Stykker\stykker.json; without that file SYS (17402) and LLM (17401).
        A foreign service with "external", "port" and "target" (this PC only: 127.0.0.1, localhost, one of its IP
        addresses, or a name from "hostNames") is forwarded on its port. Its token is the "token" field of the entry.
        The hub starts no service: "start" is ignored. Only this PC and paired devices with the admin role for that
        module get through.
        stykker.json may also list "hostNames" (top level): further names this PC answers to, such as its Tailscale name.

        The server binds the port immediately and reads nothing until a viewer appears: the measuring loop
        starts with the first request and stops again 15 seconds after the last one. A closed window therefore
        costs nothing.
        """);
    return;
}

int port = int.TryParse(args.SkipWhile(a => a != "--port").Skip(1).FirstOrDefault(), out var parsedPort) ? parsedPort : StykkerPorts.Hub;
bool noBrowser = args.Contains("--no-browser");
bool basic = args.Contains("--basic");
string url = $"http://127.0.0.1:{port}/";
// Die gemeinsame Kopplung aller Module (docs/plan-stykkernode.md). --pairing-file nimmt eine andere Ablage (Versuche).
string pairingFile = args.SkipWhile(a => a != "--pairing-file").Skip(1).FirstOrDefault() ?? PairingStore.DefaultFile();
var pairing = new PairingService(new PairingStore(pairingFile));
// Die Module aus stykker.json. --hub-config nimmt eine andere Datei (Probeläufe). Ohne Datei gilt die Vorgabe.
string? hubConfig = args.SkipWhile(a => a != "--hub-config").Skip(1).FirstOrDefault();
// Ein Ziel darf nur dieser PC sein: seine IPv4-Adressen werden hier gelesen (ServiceTargetRule)
var hubModules = HubModules.Load(hubConfig, ServiceTargetRule.ReadThisPcAddresses());
// Zusatznamen für den Host-Kopf aus derselben Datei ("hostNames"), etwa der Name des PCs im Tailscale-Netz
var extraHostNames = HostNames.LoadExtra(hubConfig ?? HubModules.DefaultFile());
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
// Die Weiterleitungen fremder Dienste (docs/plan-stykkernode.md, Abschnitt 4.5): je Dienst ein eigener Port, neben dem der Zentrale.
// Ziel und Token der Weiterleitungen aus derselben Datei, jede Anfrage liest sie neu, wenn sie sich geändert hat
var hubFile = new HubConfigFile(hubConfig ?? HubModules.DefaultFile(), () => ServiceTargetRule.ReadThisPcAddresses());
var routes = ServiceForwarding.Plan(hubModules, port, hubFile, Console.WriteLine);
// Ohne Netzschalter nur die eigene Maschine. Mit ihm auch das Netz – dort prüft PairingGate jede Anfrage.
builder.WebHost.UseUrls(ServiceForwarding.Urls(port, network, routes));
// Aus dem Build-Ordner (nicht veröffentlicht): CSS und die Seiten aus den Quellen statt aus wwwroot
if (!Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))) builder.WebHost.UseStaticWebAssets();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton<ISystemProbe>(_ => CreateProbe(basic));
builder.Services.AddSingleton(sp => new MetricsSampler(sp.GetRequiredService<ISystemProbe>()));
builder.Services.AddSingleton<HudService>();
builder.Services.AddRazorComponents();
// Alle Zahlen der Antworten auf eine Nachkommastelle (siehe OneDecimalJson).
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new OneDecimalJson()));
builder.Services.AddHttpForwarder();

var app = builder.Build();

// Die Messschleife wird hier absichtlich NICHT angefasst: sie entsteht beim ersten Abruf und liest nur, solange
// jemand zusieht (siehe HudService). So ist der Server nach dem Start sofort da, auch wenn noch kein Fenster offen ist.

// WebSockets gehen über die Weiterleitung (Qwen nutzt /acp).
app.UseWebSockets();
// Die Weiterleitungen stehen vor der Zugangsprüfung der Zentrale; sie prüfen jede Anfrage auf ihrem Port selbst.
ServiceForwarding.Use(app, pairing, routes, port, extraHostNames);
// Zugang: diese Maschine ohne Anmeldung, das Netz nur mit Netzschalter und gekoppeltem Gerät. Steht vor allem anderen.
PairingGate.Use(app, pairing, new PairingGateOptions("hud", "StykkerHUD", Home: "/hub", ExtraHostNames: extraHostNames));
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
    var host = context.Request.Host.Host;
    bool loopback = PairingGate.IsLocal(context);
    string? token = ServiceProxyRules.DeviceToken(context.Request.Headers.Cookie.ToString());
    string? remoteAddress = PairingGate.AddressText(context.Connection.RemoteIpAddress);
    // Ein fremder Dienst steht hier nur, wer ihn öffnen darf: diese Maschine immer, ein Gerät mit Rolle admin dafür (Plan 4.8)
    var visible = hubModules.Where(m => !m.External
        || ServiceProxyRules.ListsForeignService(loopback, loopback ? null : pairing.RoleOf(token, m.Id, remoteAddress))).ToList();
    var answers = await Task.WhenAll(visible.Select(async module =>
    {
        var address = module.AddressFor(context.Request.Scheme, host);
        // Eine Weiterleitung zeigt, ob der Dienst läuft: ihr Ziel antwortet und das Token ist lesbar (sonst 503).
        // Ohne eigene Adresse ist der Eintrag die Zentrale selbst: sie antwortet gerade, also läuft sie.
        bool up;
        if (module.External && module.Target != null)
        {
            var route = routes.FirstOrDefault(r => r.Id == module.Id);
            up = route != null && (await route.ResolveAsync(context.RequestAborted)).Target != null;
        }
        else up = address == null || await HubProbe.AnswersAsync(module, address, context.RequestAborted);
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
    // Die Spalten der Geräteliste: Monitor, SYS, LLM und jedes weitere Modul aus stykker.json. Rollen je Spalte.
    var columns = AccessColumns(hubModules);
    return Results.Json(new
    {
        local = true,
        remote = pairing.RemoteEnabled,
        listening = network,
        code = PairingCodes.Pretty(pairing.Code),
        expiresIn = (int)Math.Max(0, (pairing.CodeExpires - DateTime.UtcNow).TotalSeconds),
        addresses = LanAddresses().Select(address => $"http://{address}:{port}/hub"),
        // Die Adressen, die der QR-Code wählen darf (die Adressen dieses PCs und die Zusatznamen aus stykker.json)
        qrAddresses = ReachableNames(extraHostNames),
        modules = columns.Select(c => new { id = c.Id, name = c.Name }),
        devices = pairing.Devices.Select(d => new
        {
            id = d.Id, name = d.Name, address = d.Address, lastSeen = d.LastSeen,
            roles = columns.ToDictionary(c => c.Id, c => d.RoleIn(c.Id)),
        }),
        // Geräte, die sich ohne Anmeldung gemeldet haben und auf Freigabe warten (Ziffern nur hier an der Maschine)
        requests = pairing.PendingRequests.Select(r => new
        {
            id = r.Id,
            name = r.Name,
            module = r.Module,
            moduleName = columns.Where(c => c.Id == r.Module).Select(c => c.Name).FirstOrDefault() ?? r.Module,
            code = PairingCodes.Pretty(r.Code),
            expiresIn = (int)Math.Max(0, (r.Expires - DateTime.UtcNow).TotalSeconds),
        }),
    });
});
// Der QR-Code für das Handy: die Adresse der Zentrale mit dem Code, den sie gerade zeigt. Nur an dieser Maschine. Die
// Adresse muss eine der Adressen sein, die /api/hub/access nennt, oder ein Name aus hostNames (sonst 400). Ohne
// Netzschalter, oder wenn der Port nicht im Netz steht, gibt es keinen QR-Code (409).
app.MapGet("/api/hub/qr", (HttpContext context, string? address) =>
{
    if (!PairingGate.IsLocal(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (string.IsNullOrEmpty(address) || !ReachableNames(extraHostNames).Contains(address, StringComparer.OrdinalIgnoreCase))
        return Results.Json(new { ok = false, message = "That address is not one of this PC's addresses." }, statusCode: StatusCodes.Status400BadRequest);
    if (!network || !pairing.RemoteEnabled)
        return Results.Json(new { ok = false, message = "Network access is off." }, statusCode: StatusCodes.Status409Conflict);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(QrCode.Encode($"http://{address}:{port}/pair?code={pairing.Code}").ToSvg(6, 3), "image/svg+xml; charset=utf-8");
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
// Kopplung andersherum: die sechs Ziffern eines wartenden Geräts freigeben. Rolle wie bei PairingService.Approve: die
// Vorgabe ist admin in dem Modul, an dem das Gerät angefragt hat. Die Ziffern stehen im Körper, nicht in der Adresse.
app.MapPost("/api/hub/requests/approve", (HttpContext context, JsonElement body) =>
{
    if (!Local(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    string role = JsonText(body, "role") ?? PairingRoles.Admin;
    if (!PairingRoles.IsValid(role))
        return Results.Json(new { ok = false, message = "Role must be admin or viewer." }, statusCode: StatusCodes.Status400BadRequest);
    var approved = pairing.Approve(JsonText(body, "code"), role);
    if (approved == null)
        return Results.Json(new { ok = false, message = "No device is waiting with these six digits." }, statusCode: StatusCodes.Status404NotFound);
    return Results.Json(new { ok = true, name = approved.Name, module = approved.Module });
});
app.MapPost("/api/hub/requests/{id}/deny", (HttpContext context, string id) =>
    !Local(context) ? Results.StatusCode(StatusCodes.Status403Forbidden)
    : pairing.Deny(id) ? Results.Json(new { ok = true }) : Results.NotFound());

// ── Fremde Dienste („Services“ in den Geräten): eintragen, URL ersetzen, entfernen. Nur an dieser Maschine und von der
// eigenen Seite. Das Token geht nie in eine Antwort, nie in /api/hub und nie ins Log (ServiceTargetRule, LocalControlExchange).
// Der Tausch ist eine Anfrage an den Dienst; sein Ziel muss dieser PC sein (ServiceTargetRule). ──
var exchangeClient = new HttpClient(new SocketsHttpHandler
{
    UseProxy = false, AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5),
}) { Timeout = TimeSpan.FromSeconds(10) };
app.MapGet("/api/hub/services", (HttpContext context) =>
{
    if (!Local(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var services = HubConfigWriter.List(hubFile.FilePath);
    return Results.Json(new
    {
        hubPort = port,
        // Ein Eintrag wirkt erst nach dem Neustart, wenn seine Weiterleitung noch nicht läuft (neuer Port)
        services = services.Select(s => new
        {
            id = s.Id, name = s.Name, port = s.Port, target = s.Target, tokenSet = s.TokenSet,
            running = routes.Any(r => r.Id == s.Id && r.Port == s.Port),
        }),
        suggestedPort = Enumerable.Range(17450, 200).First(p => !services.Any(s => s.Port == p) && !hubModules.Any(m => m.Port == p)
            && p != port && p != StykkerPorts.Llm && p != StykkerPorts.Sys),
    });
});
app.MapPost("/api/hub/services", async (HttpContext context, JsonElement body) =>
{
    if (!Local(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var name = (JsonText(body, "name") ?? "").Trim();
    var id = (JsonText(body, "id") ?? "").Trim().ToLowerInvariant();
    var servicePort = JsonInt(body, "port") ?? 0;
    // Name, Id und Port zuerst prüfen: ein Gerät soll nur gekoppelt werden, wenn der Eintrag danach auch geschrieben werden kann
    var check = HubConfigWriter.Check(hubFile.FilePath, id, name, servicePort, port);
    if (!check.Ok) return ServiceAnswer(false, check.Message, StatusCodes.Status400BadRequest);
    var target = TargetFromUrl(JsonText(body, "url"));
    if (target.Error != null) return ServiceAnswer(false, target.Error, StatusCodes.Status400BadRequest);
    var exchange = await LocalControlExchange.ExchangeAsync(exchangeClient, target.Address!, target.Secret!, null, context.RequestAborted);
    if (!exchange.Ok) return ServiceAnswer(false, exchange.Message, StatusCodes.Status400BadRequest);
    var written = HubConfigWriter.Add(hubFile.FilePath, new ServiceEntry(id, name, servicePort, target.Address!, exchange.Token!), port);
    if (!written.Ok)
        return ServiceAnswer(false, "The device was paired in Qwen, but stykker.json could not be written. Remove the device \"Stykker hub\" in Qwen. " + written.Message, StatusCodes.Status409Conflict);
    Console.WriteLine($"{name}: added on port {servicePort}; it forwards after a restart of the hub.");
    return ServiceAnswer(true, $"{name} is added. Restart the hub to open port {servicePort}.", StatusCodes.Status200OK, restart: true);
});
app.MapPost("/api/hub/services/{id}/url", async (HttpContext context, string id, JsonElement body) =>
{
    if (!Local(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var target = TargetFromUrl(JsonText(body, "url"));
    if (target.Error != null) return ServiceAnswer(false, target.Error, StatusCodes.Status400BadRequest);
    // Das Geräte-Token des Dienstes, falls es noch gilt, wird wiederverwendet (Qwen legt dann kein zweites Gerät an)
    var previous = HubConfigWriter.StoredToken(hubFile.FilePath, id);
    var exchange = await LocalControlExchange.ExchangeAsync(exchangeClient, target.Address!, target.Secret!, previous, context.RequestAborted);
    if (!exchange.Ok) return ServiceAnswer(false, exchange.Message, StatusCodes.Status400BadRequest);
    var written = HubConfigWriter.Replace(hubFile.FilePath, id, target.Address!, exchange.Token);
    if (!written.Ok) return ServiceAnswer(false, written.Message, StatusCodes.Status404NotFound);
    Console.WriteLine($"{id}: address replaced; it works at once.");
    return ServiceAnswer(true, "The address is replaced. It works at once.", StatusCodes.Status200OK);
});
app.MapDelete("/api/hub/services/{id}", (HttpContext context, string id) =>
{
    if (!Local(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var removed = HubConfigWriter.Remove(hubFile.FilePath, id);
    if (!removed.Ok) return ServiceAnswer(false, removed.Message, StatusCodes.Status404NotFound);
    return ServiceAnswer(true, "Removed. Its port closes at the next start of the hub. Remove the device \"Stykker hub\" in Qwen too.", StatusCodes.Status200OK);
});

// Die Adresse aus der Local-Control-URL: ergibt das Ziel (nur dieser PC, ServiceTargetRule) und das Geheimnis. Die Meldung
// nennt nie die URL oder das Geheimnis.
(string? Address, string? Secret, string? Error) TargetFromUrl(string? url)
{
    if (!LocalControlExchange.TryParseUrl(url, out var address, out var secret))
        return (null, null, "That is not a Local Control URL. Copy the URL Qwen shows under Local Control: it looks like http://<address>:<port>/#token=…");
    var allowed = ServiceTargetRule.Allowed(address, ServiceTargetRule.ReadThisPcAddresses(), HostNames.LoadExtra(hubFile.FilePath));
    if (allowed == null)
        return (null, null, "The address is not one of this PC's addresses, or a name listed under hostNames in stykker.json.");
    return (allowed, secret, null);
}

static IResult ServiceAnswer(bool ok, string message, int status, bool restart = false) =>
    Results.Json(new { ok, message, restart }, statusCode: status);

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

// Die Spalten der Geräteliste (docs/plan-stykkernode.md, Abschnitt 4.9): Monitor (Rollenschlüssel "hud"), SYS, LLM und
// die übrigen Module aus stykker.json, ohne den Monitor-Eintrag selbst.
static List<(string Id, string Name)> AccessColumns(IEnumerable<HubModule> modules)
{
    var columns = new List<(string Id, string Name)> { ("hud", "Monitor"), ("sys", "SYS"), ("llm", "LLM") };
    foreach (var module in modules)
        if (module.Id != HubModules.MonitorId && columns.All(c => c.Id != module.Id)) columns.Add((module.Id, module.Name));
    return columns;
}

// Die Namen, unter denen die Zentrale im Netz erreichbar ist: die IPv4-Adressen dieser Maschine und die Zusatznamen
// aus stykker.json ("hostNames"). Der QR-Code darf nur eines davon nehmen.
static List<string> ReachableNames(IReadOnlyList<string> extraNames) =>
    LanAddresses().Concat(extraNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

// Ein Textfeld aus dem JSON-Körper einer Anfrage (null, wenn es fehlt oder kein Text ist)
static string? JsonText(JsonElement body, string name) =>
    body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;

// Eine ganze Zahl aus dem JSON-Körper (null, wenn es fehlt oder keine ganze Zahl ist)
static int? JsonInt(JsonElement body, string name) =>
    body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
        ? number
        : null;

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
