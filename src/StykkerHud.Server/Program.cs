using Microsoft.Extensions.FileProviders;
using StykkerHud.Core;
using StykkerHud.Platform.Windows;
using StykkerHud.Server.Components;

// StykkerHUD-Server: die Messwerte dieser Maschine als Web-Oberfläche auf http://127.0.0.1:8079.
// Nur auf der eigenen Maschine erreichbar – ein Ressourcenmonitor hat im Netz nichts zu suchen. Das Fenster
// (StykkerHUD.exe) zeigt dieselben Seiten.
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        StykkerHUD-Server - processes, GPU, CPU and memory of this machine

          --port <number>   web port (default 8079)
          --no-tray         no tray icon (the window starts the server this way: it has an icon itself)
          --no-browser      do not open a browser on start
          --basic           no system access: every value shows "-" (for trying that mode on Windows)
          --design-system <folder>
                            the shared design system served under /ds
                            (default C:\_AI\Stykker\Design-System)
          --help            this text

        Endpoints: / (web interface), /api/snapshot (the current reading and the history as JSON).

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

// Ein Server je Port: läuft schon einer, öffnet ein zweiter Start nur die Oberfläche.
using var single = new Mutex(true, @"Local\StykkerHUD-server-" + port, out bool first);
if (!first)
{
    if (!noBrowser) OpenBrowser(url);
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
// Nur die eigene Maschine: kein Schalter, kein Zugangscode, keine Fernbedienung.
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
// Aus dem Build-Ordner (nicht veröffentlicht): CSS und die Seiten aus den Quellen statt aus wwwroot
if (!Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))) builder.WebHost.UseStaticWebAssets();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton<ISystemProbe>(_ => CreateProbe(basic));
builder.Services.AddSingleton(sp => new MetricsSampler(sp.GetRequiredService<ISystemProbe>()));
builder.Services.AddSingleton<HudService>();
builder.Services.AddRazorComponents();

var app = builder.Build();

// Die Messschleife wird hier absichtlich NICHT angefasst: sie entsteht beim ersten Abruf und liest nur, solange
// jemand zusieht (siehe HudService). So ist der Server nach dem Start sofort da, auch wenn noch kein Fenster offen ist.
app.UseStaticFiles();

// Das Design-System der Stykker-Familie liegt außerhalb dieses Projekts. Es wird nicht kopiert, sondern unter /ds
// aus seiner Quelle geliefert: eine Änderung dort wirkt beim nächsten Laden, und es gibt genau eine Fassung für
// die ganze Familie. --design-system <Ordner> oder STYKKERHUD_DESIGN_SYSTEM zeigen auf eine andere Stelle.
var designSystem = args.SkipWhile(a => a != "--design-system").Skip(1).FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("STYKKERHUD_DESIGN_SYSTEM")
    ?? @"C:\_AI\Stykker\Design-System";
if (Directory.Exists(designSystem))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(designSystem),
        RequestPath = "/ds",
    });
}
else
{
    Console.Error.WriteLine($"[design] not found: {designSystem} – the page will load without its styles. Use --design-system <folder>.");
}

// Die Seiten der Razor-Komponenten tragen Anti-Fälschungs-Metadaten: ohne diese Middleware antwortet "/" mit 500.
app.UseAntiforgery();
app.MapGet("/api/snapshot", (HudService service) => Results.Json(service.Snapshot()));

// ── Prozess-Aktionen ──
// Drei Türen, alle nur für diese Maschine und nur für die eigene Seite: der Rumpf muss JSON sein (ein fremdes
// Formular kann das ohne CORS-Prüfung nicht senden) und die Herkunft muss die eigene sein. Zusätzlich bleiben
// System-PIDs und dieser Server selbst gesperrt (siehe ProcessActions).
app.MapPost("/api/process/end", (HttpContext ctx, ProcessEndRequest body) => Guarded(ctx, () => ProcessActions.End(body.Pid, body.Tree)));
app.MapPost("/api/process/priority", (HttpContext ctx, ProcessPriorityRequest body) => Guarded(ctx, () => ProcessActions.SetPriority(body.Pid, body.Level ?? "")));
app.MapPost("/api/process/open", (HttpContext ctx, ProcessOpenRequest body) => Guarded(ctx, () =>
{
    string? path = ProcessActions.ExecutablePath(body.Pid);
    if (path == null) return new ActionResult(false, $"Windows gave no file path for PID {body.Pid} (protected process).");
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
if (!noBrowser) OpenBrowser(url);

// Tray-Symbol: ohne Fenster wäre der Server sonst nur über die Prozessliste erreichbar. Fehler sind kein Grund
// für einen Abbruch – dann läuft er eben ohne Symbol.
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
            if (id == 1) OpenBrowser(url);
            else if (id == 2) app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        },
        () => OpenBrowser(url));
    if (!tray.TryShow($"StykkerHUD – {url}", out var trayError)) Console.Error.WriteLine($"[tray] {trayError}");
}

await app.WaitForShutdownAsync();
if (tray != null && OperatingSystem.IsWindows()) tray.Dispose();
// Den Messdienst gibt es nur, wenn ihn jemand angefordert hat; er endet mit dem Prozess.

// Die Windows-Sonde nur auf Windows; sonst (oder mit --basic) antwortet die Anzeige überall „–".
static ISystemProbe CreateProbe(bool basic)
{
    if (basic) return new BasicProbe();
    if (!OperatingSystem.IsWindows()) return new BasicProbe();
    return new WindowsProbe();
}

// Nur die eigene Seite darf handeln: ein POST von einer fremden Seite trägt einen anderen Origin und fällt hier
// raus. Fehlt beides (kein Browser, z. B. curl), bleibt der JSON-Zwang als Schutz.
static bool SameOrigin(HttpContext ctx)
{
    string origin = ctx.Request.Headers.Origin.ToString();
    if (!string.IsNullOrEmpty(origin)) return origin == $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    string site = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
    return site.Length == 0 || site == "same-origin";
}

static IResult Guarded(HttpContext ctx, Func<ActionResult> action)
{
    if (!SameOrigin(ctx))
        return Results.Json(new ActionResult(false, "Refused: that request did not come from this page."), statusCode: StatusCodes.Status403Forbidden);
    var result = action();
    return Results.Json(result, statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
}

static void OpenBrowser(string target)
{
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PlatformNotSupportedException) { }
}

internal sealed record ProcessEndRequest(int Pid, bool Tree);
internal sealed record ProcessPriorityRequest(int Pid, string? Level);
internal sealed record ProcessOpenRequest(int Pid);