using System.Text;
using System.Text.Json;
using StykkerLlm.Core;
using StykkerLlm.Core.Eval;
using StykkerLlm.Platform.Windows;
using StykkerLlm.Server;
using StykkerLlm.Server.Components;

// StykkerLLM-Server: Web-Oberfläche auf http://127.0.0.1:8078, lokal immer, aus dem Heimnetz mit dem Schalter Home/VPN
// und dem Zugangscode. Der Server hält die Engine; Fenster, TUI und Telefon sind Clients (S3/S4/S5).
// Ein Server je Datenordner: läuft schon einer, öffnet ein zweiter Start nur den Browser.
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        StykkerLLM-Server - the web interface and the engine behind it

          --data-dir <folder>   other data folder (default: the one of the app)
          --port <number>       web port (default 8078; the Stykker-Proxy uses 17500)
          --no-browser          do not open a browser on start
          --no-tray             no tray icon (the window starts the server this way: it has an icon itself)
          --stay                keep running when no window, terminal or web page is open (also a setting)
          --sim                 simulated servers instead of real ones (demo, screenshots; nothing real is touched)
          --platform basic      no system access (like Linux): no auto-detection, no GPU/system values.
                                Useful to try that mode on Windows.
          --help                this text

        Start it from the window (it does that by itself), or with "stykker web".
        Without the window the tray icon opens the web interface and shuts the server down.
        Endpoints: / (web interface), /phone (sign-in), /api/state, /api/metrics (this machine only),
                   /api/whoami, /api/action. From the network only with Home/VPN on and the access code.
        """);
    return;
}

var dataDir = args.SkipWhile(a => a != "--data-dir").Skip(1).FirstOrDefault();
var paths = dataDir != null ? new AppPaths(Path.GetFullPath(dataDir)) : AppPaths.Default();
Directory.CreateDirectory(paths.Root);
int port = int.TryParse(args.SkipWhile(a => a != "--port").Skip(1).FirstOrDefault(), out var pp) ? pp : 8078;
bool noBrowser = args.Contains("--no-browser");
AppLog.Init("StykkerLLM-Server", paths);
AppLog.CatchUnhandled();
AppLog.Write($"start: port {port}, args {string.Join(' ', args.Where(a => a.StartsWith("--", StringComparison.Ordinal)))}");
// „--platform basic“ nimmt die Umsetzung ohne Betriebssystem-Zugriff (der Weg, auf dem der Server unter Linux
// läuft). Ohne den Schalter entscheidet das System selbst; siehe EngineHost.CreatePlatform.
var platformName = args.SkipWhile(a => a != "--platform").Skip(1).FirstOrDefault();
var platform = EngineHost.CreatePlatform(platformName);
string url = $"http://127.0.0.1:{port}";

// Name aus dem Datenordner (stabiler SHA-256-Kürzel). Früher stand hier zusätzlich string.GetHashCode() – das ist in
// .NET je Prozess zufällig, der Schutz gegen einen zweiten Server griff also nie.
using var serverLock = new System.Threading.Mutex(true, "Local\\" + SingleInstance.NameFor(paths.Root) + "-server", out bool first);
if (!first)
{
    AppLog.Write("start: a server for this data folder already runs");
    if (!noBrowser) ServerUi.OpenBrowser(PairUrl(paths, port, platform));
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
// Auf allen Adressen lauschen: der Schalter Home/VPN entscheidet im AccessGate, wer von außen durchkommt
// (sonst müsste der Prozess für jede Änderung neu starten). Ohne eingeschalteten Schalter bleibt nur 127.0.0.1 erreichbar.
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
// Aus dem Build-Ordner (nicht veröffentlicht): CSS und blazor.web.js aus den Quellen/Paketen statt aus wwwroot
if (!Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))) builder.WebHost.UseStaticWebAssets();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddProvider(new AppLogProvider());
builder.Services.AddSingleton(paths);
// --sim: simulierte Server (eigene Welt, gleicher Datenordner); sonst die echte Plattform
var sim = args.Contains("--sim") ? new SimHost(SimServerSpec.Defaults(), dataDir: paths.Root) : null;
sim?.World.Start();
builder.Services.AddSingleton<EngineHost>(sp => new EngineHost(paths, platform, sim));
builder.Services.AddSingleton(sp => new EvalQueue(paths));
builder.Services.AddSingleton(sp => new ActionContext
{
    Engine = sp.GetRequiredService<EngineHost>().Engine,
    Launcher = new LaunchCoordinator(sp.GetRequiredService<EngineHost>().Engine, new RemotePrompt()),
    Access = sp.GetRequiredService<EngineHost>().Access,
    Queue = sp.GetRequiredService<EvalQueue>(),
    Benchmarks = sp.GetRequiredService<EngineHost>().Benchmarks,
    Nodes = sp.GetRequiredService<EngineHost>().Nodes,
    Shutdown = () => { sp.GetRequiredService<IHostApplicationLifetime>().StopApplication(); return Task.CompletedTask; },
    ServerPort = port,
});
// je Browser-Sitzung: Rückfragen als Dialog, Start/Stop über den LaunchCoordinator des Core
builder.Services.AddScoped<WebPrompt>();
builder.Services.AddScoped(sp => new LaunchCoordinator(sp.GetRequiredService<EngineHost>().Engine, sp.GetRequiredService<WebPrompt>()));
builder.Services.AddScoped<ViewerSession>();
builder.Services.AddScoped<WebActions>();
builder.Services.AddHttpContextAccessor();   // für die Rolle beim Vorab-Rendern (ViewerSession)
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, HoldCircuits>();

var app = builder.Build();
var engineHost = app.Services.GetRequiredService<EngineHost>();
engineHost.Queue = app.Services.GetRequiredService<EvalQueue>();   // die Warteschlange gehört in den Zustand aller Oberflächen
// Neustart nach einem Absturz: der Server kann starten, also hängt er die Regel hier an
// der globale Launcher (ohne Browser-Sitzung); der Launcher je Sitzung ist scoped und darf hier nicht geholt werden
var launcher = app.Services.GetRequiredService<ActionContext>().Launcher;
engineHost.StartProfile = launcher.StartProfileAsync;
engineHost.UnloadServer = launcher.UnloadIfIdleAsync;   // Leerlauf-Entladen
var gate = new AccessGate(engineHost.Access, engineHost.Key, port, () => ThemeCatalog.Find(engineHost.Engine.Settings.Theme));

// ── Zugang: Schlüssel (Fenster/TUI) oder Gerätecookie (Browser); von außen nur mit Home/VPN ──
app.Use(async (ctx, next) =>
{
    if (await gate.TryHandleAsync(ctx)) return;
    // Ein Hub, der diesen PC als Node abfragt, hält den Server am Leben (seine Anfragen kommen regelmäßig)
    if (AccessGate.RoleOf(ctx) == AccessRole.Hub)
        engineHost.Holds.Touch("hub:" + ctx.Connection.RemoteIpAddress, DateTime.Now);
    await next();
});

// ── Lebensdauer: Fenster und TUI melden sich alle paar Sekunden (ServerHolds), beim Beenden ab ──
app.MapPost("/api/hold", (string? id) =>
{
    engineHost.Holds.Touch(id ?? "", DateTime.Now);
    return Results.Text("{\"ok\":true}", "application/json");
});
app.MapDelete("/api/hold", (string? id) =>
{
    engineHost.Holds.Release(id ?? "");
    AppLog.Write($"hold released: {id}");
    return Results.Text("{\"ok\":true}", "application/json");
});

// Fehlerbericht herunterladen (nur Dateien aus bug-reports\, nur Admin)
app.MapGet("/api/bugreport", (string? file, HttpContext ctx) =>
{
    if (!AccessRole.CanWrite(AccessGate.RoleOf(ctx)) || string.IsNullOrEmpty(file)) return Results.NotFound();
    var dir = Path.GetFullPath(Path.Combine(paths.Root, "bug-reports"));
    var full = Path.GetFullPath(Path.Combine(dir, Path.GetFileName(file)));
    return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && File.Exists(full)
        ? Results.File(full, "application/zip", Path.GetFileName(full)) : Results.NotFound();
});

// ── Steuer-API für Fenster, TUI, Telefon und Skripte (S3) ──
// Der Zustand nennt den Zugangscode nur für ein Gerät mit der Rolle Admin – sonst könnte sich ein Viewer
// mit dem gelesenen Code als Admin anmelden. Fenster und TUI kommen mit dem Schlüssel herein und sehen alles.
app.MapGet("/api/state", (HttpContext ctx) =>
    Results.Text(engineHost.StateJsonText(port, withCode: AccessRole.CanWrite(AccessGate.RoleOf(ctx))), "application/json"));

// Rolle dieses Geräts: einmal je Browser-Sitzung abfragen, weil die Blazor-Schleife keinen HttpContext hat
app.MapGet("/api/whoami", (HttpContext ctx) =>
    Results.Text($"{{\"role\":\"{AccessRole.Normalize(AccessGate.RoleOf(ctx))}\"}}", "application/json"));

// Eigene Messwerte für Starship, waybar, Grafana – nur von diesem Rechner, ohne Schlüssel:
// die Werte stehen auch in /api/state, aber hier in Prometheus-Text und ohne den Umweg über JSON.
// Kein Geheimnis: das Gate lässt von außen ohnehin nur hereinkommen, was ohnehin schon alles sieht.
app.MapGet("/api/metrics", (HttpContext ctx) =>
{
    if (!NetAddr.IsLoopback(ctx.Connection.RemoteIpAddress?.ToString() ?? ""))
        return Results.Text("metrics are only served on this machine (127.0.0.1)", "text/plain; charset=utf-8", statusCode: 403);
    var text = MetricsText.Write(engineHost.Engine, DateTimeOffset.Now, engineHost.Engine.VramTop, engineHost.Engine.RamTop, engineHost.Engine.GpuUtilTop);
    return Results.Text(text, MetricsText.ContentType);
});

app.MapGet("/api/stream", async (HttpContext ctx) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    var withCode = AccessRole.CanWrite(AccessGate.RoleOf(ctx));
    var cts = ctx.RequestAborted;
    try
    {
        // Zustand sofort schicken, danach bei jedem Takt des Servers (kein eigener Takt: die Engine misst)
        var last = -1;
        while (!cts.IsCancellationRequested)
        {
            var ticks = engineHost.Engine.Ticks;
            if (ticks != last)
            {
                last = ticks;
                var json = engineHost.StateJsonText(port, withCode: withCode);
                await ctx.Response.WriteAsync($"data: {json}\n\n", Encoding.UTF8, cts);
                await ctx.Response.Body.FlushAsync(cts);
            }
            await Task.Delay(250, cts);
        }
    }
    catch (OperationCanceledException) { /* Browser weg */ }
    catch (Exception ex)
    {
        // Der Client soll den Grund sehen statt nur eines abrupten Endes
        Console.Error.WriteLine($"[api/stream] {ex}");
        try
        {
            await ctx.Response.WriteAsync($"event: error\ndata: {JsonSerializer.Serialize(ex.Message)}\n\n", Encoding.UTF8, CancellationToken.None);
            await ctx.Response.Body.FlushAsync(CancellationToken.None);
        }
        catch { /* Verbindung schon weg */ }
    }
});

app.MapPost("/api/action", async (HttpContext ctx, ActionContext actions) =>
{
    string body;
    using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
        body = await reader.ReadToEndAsync(ctx.RequestAborted);
    var req = ActionRequest.Parse(body);
    // Rückfragen gibt es auf diesem Weg nicht: die Anfrage selbst ist die Bestätigung, ein Geheimnis kommt im Feld "secret".
    // Die Rolle entscheidet, ob überhaupt etwas ausgeführt wird (ActionApi).
    var result = await ActionApi.ExecuteAsync(req, actions, new RemotePrompt(req.Secret), ctx.RequestAborted, AccessGate.RoleOf(ctx));
    return Results.Text($"{{\"ok\":{(result.Ok ? "true" : "false")},\"message\":{JsonSerializer.Serialize(result.Message)}" +
        (result.Data == null ? "" : $",\"data\":{JsonSerializer.Serialize(result.Data)}") + "}", "application/json");
});

app.UseAntiforgery();

// Läufe der Modelltests für einen Hub (docs/nodes.md, N3): erst die Liste der Dateinamen, dann je Datei der Inhalt.
// Nur die eigenen Läufe (oberste Ebene), nicht die schon von anderen Nodes eingesammelten.
app.MapGet("/api/eval/runs", () =>
{
    var dir = paths.EvalResultsDir;
    var names = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Select(Path.GetFileName).ToList() : new List<string?>();
    return Results.Text(JsonSerializer.Serialize(names), "application/json");
});
app.MapGet("/api/eval/runs/{name}", (string name) =>
{
    if (!NodeRegistry.IsRunFileName(name)) return Results.NotFound();
    var file = Path.Combine(paths.EvalResultsDir, name);
    return File.Exists(file) ? Results.Text(File.ReadAllText(file), "application/json") : Results.NotFound();
});

// Benchmark als Markdown oder CSV (der Knopf im Fenster macht dasselbe)
app.MapGet("/api/bench/{id}", (string id, EngineHost host) =>
{
    var found = host.Engine.Library.Benchmarks.FirstOrDefault(b => b.Id.ToString("N") == id);
    if (found == null) return Results.NotFound();
    return Results.Text(BenchmarkExport.Markdown(new[] { found }), "text/markdown; charset=utf-8");
});

app.MapGet("/api/bench/{id}/csv", (string id, EngineHost host) =>
{
    var found = host.Engine.Library.Benchmarks.FirstOrDefault(b => b.Id.ToString("N") == id);
    if (found == null) return Results.NotFound();
    return Results.Text(BenchmarkExport.Csv(new[] { found }), "text/csv; charset=utf-8");
});

// Serverlog ansehen („Open log“ beim Hinweis, auf der Detailseite). Nur Dateien im Datenordner, sonst 404.
app.MapGet("/api/log", (string? path, AppPaths data) =>
{
    var text = LogTail.Read(data, path);
    return text == null ? Results.NotFound() : Results.Text(text, "text/plain; charset=utf-8");
});

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

engineHost.Start();
try { await app.StartAsync(); }
catch (IOException ex)
{
    // Port belegt (ein anderer Server mit anderem Datenordner, oder ein fremdes Programm): sagen statt abstürzen
    Console.Error.WriteLine(Strings.ServerPortBusy(port, ex.Message));
    AppLog.Write(Strings.ServerPortBusy(port, ex.Message));
    sim?.Dispose();
    Environment.ExitCode = 1;
    return;
}
// Antwort auf die Suche anderer Stykker (Nodes koppeln, docs/nodes.md) – nur solange Home/VPN an ist
using var discovery = new DiscoveryResponder(() => engineHost.Access.RemoteEnabled, () => NodeDiscovery.Reply(port, engineHost.Access.RemoteEnabled));
discovery.Start();
if (!noBrowser) ServerUi.OpenBrowser(PairUrl(paths, port, platform));

// Tray-Symbol (seit 2026-10-02): ohne Fenster bleibt der Server sonst nur über die Prozessliste
// erreichbar. Das Fenster startet den Server mit --no-tray, weil es selbst ein Symbol hat. Fehler sind kein Grund
// für einen Abbruch – dann läuft der Server eben ohne Symbol (unter Linux gibt es keins).
TrayIcon? tray = null;
if (!args.Contains("--no-tray") && TrayIcon.Possible)
{
    tray = new TrayIcon(
        new[]
        {
            new TrayIcon.Item(1, Strings.TrayServerWeb),
            new TrayIcon.Item(2, Strings.TrayServerStop),
        },
        id =>
        {
            if (id == 1) ServerUi.OpenBrowser(url);
            else if (id == 2) app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        },
        () => ServerUi.OpenBrowser(url));
    if (!tray.TryShow(Strings.TrayServerTip(url), out var trayError)) Console.Error.WriteLine($"[tray] {trayError}");
}

// Gebunden an Fenster/TUI/Webseite: ist niemand mehr da und läuft keine Arbeit, beendet sich der Server.
// Ausnahme: --stay oder die Einstellung „Keep the server running“ (ein Node-PC ohne Bildschirm).
var life = app.Services.GetRequiredService<IHostApplicationLifetime>();
bool stayArg = args.Contains("--stay");
using var lifeTimer = new Timer(_ =>
{
    try
    {
        if (stayArg || engineHost.Engine.Settings.KeepServerRunning) return;
        var now = DateTime.Now;
        bool busy = engineHost.Queue?.Running == true || engineHost.Benchmarks.Running;
        if (engineHost.Holds.ShouldStop(now, busy))
        {
            AppLog.Write(Strings.ServerAutoStop(engineHost.Holds.Describe(now)));
            life.StopApplication();
        }
    }
    catch (Exception ex) { AppLog.Error("life check", ex); }
}, null, 2000, 2000);

// Läuft, bis jemand „Shut down“ drückt (Web, Telefon, API), niemand den Server mehr braucht oder der Prozess endet
await app.WaitForShutdownAsync();
AppLog.Write("stop");
app.Services.GetRequiredService<EvalQueue>().Dispose();
await app.StopAsync();
tray?.Dispose();
engineHost.Dispose();
sim?.Dispose();

// Der Browser startet mit dem Zugangscode, damit er ohne Umweg angemeldet ist (der Schlüssel bleibt für Fenster und TUI)
static string PairUrl(AppPaths paths, int port, IPlatform platform)
{
    try
    {
        var access = new AccessControl(paths, platform);
        return $"{NetAddr.Url("127.0.0.1", port)}/pair?code={Uri.EscapeDataString(access.Code)}";
    }
    catch { return $"http://127.0.0.1:{port}/"; }
}
