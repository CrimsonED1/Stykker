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
                            (default C:\_AI\Stykker\MonoRepo\shared\design-system)
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
using var single = ToolHost.ClaimPort("StykkerHUD", port, out bool first);
if (!first)
{
    if (!noBrowser) ToolHost.OpenBrowser(url);
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
// Alle Zahlen der Antworten auf eine Nachkommastelle (siehe OneDecimalJson).
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new OneDecimalJson()));

var app = builder.Build();

// Die Messschleife wird hier absichtlich NICHT angefasst: sie entsteht beim ersten Abruf und liest nur, solange
// jemand zusieht (siehe HudService). So ist der Server nach dem Start sofort da, auch wenn noch kein Fenster offen ist.
app.UseStaticFiles();
DesignSystemHost.Map(app, DesignSystemHost.Resolve(args, "STYKKERHUD_DESIGN_SYSTEM"));

// Die Seiten der Razor-Komponenten tragen Anti-Fälschungs-Metadaten: ohne diese Middleware antwortet "/" mit 500.
app.UseAntiforgery();
app.MapGet("/api/snapshot", (HudService service) => Results.Json(service.Snapshot()));

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

// Die Windows-Sonde nur auf Windows; sonst (oder mit --basic) antwortet die Anzeige überall „–".
static ISystemProbe CreateProbe(bool basic)
{
    if (basic) return new BasicProbe();
    if (!OperatingSystem.IsWindows()) return new BasicProbe();
    return new WindowsProbe();
}
