using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Yarp.ReverseProxy.Forwarder;

// Versuch: Hülle der Zentrale (Leiste + Frame) auf einem Port, auf einem zweiten Port eine Weiterleitung zu einem
// fremden Web-Dienst, der sich von sich aus nicht einbetten lässt.
//
//   FrameProxy --target http://127.0.0.1:41999 [--hub 8075] [--frame 8076] [--token <Token des Dienstes>]
//
// Mit --token setzt die Weiterleitung das Token des Dienstes selbst ein: der Browser braucht es dann nicht.
// Der Versuch hört nur auf 127.0.0.1 und prüft keine Kopplung – das käme im Knoten davor.

string Arg(string name, string fallback) =>
    Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;

var hubPort = int.Parse(Arg("--hub", "8075"));
var framePort = int.Parse(Arg("--frame", "8076"));
var target = Arg("--target", "http://127.0.0.1:41999").TrimEnd('/');
var token = Arg("--token", "");
var hubOrigin = $"http://127.0.0.1:{hubPort}";
var frameOrigin = $"http://127.0.0.1:{framePort}";

var builder = WebApplication.CreateBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(IPAddress.Loopback, hubPort);
    kestrel.Listen(IPAddress.Loopback, framePort);
});
builder.Services.AddHttpForwarder();
var app = builder.Build();
app.UseWebSockets();

var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
var client = new HttpMessageInvoker(new SocketsHttpHandler
{
    UseProxy = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.None,
    UseCookies = false,
    ConnectTimeout = TimeSpan.FromSeconds(5),
});
var transformer = new FrameTransformer(target, hubOrigin, token);

app.Run(async context =>
{
    if (context.Connection.LocalPort == framePort)
    {
        // Die Weiterleitung schreibt Origin um; die Prüfung, die der Dienst damit selbst nicht mehr machen kann,
        // steht deshalb hier: eine fremde Seite darf über die Weiterleitung nichts auslösen.
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && origin != frameOrigin)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await forwarder.SendAsync(context, target, client, ForwarderRequestConfig.Empty, transformer);
        return;
    }

    if (context.Request.Path != "/")
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(Shell.Html(frameOrigin, target));
});

Console.WriteLine($"hub   {hubOrigin}");
Console.WriteLine($"frame {frameOrigin} -> {target}{(token.Length > 0 ? " (token injected)" : "")}");
app.Run();

// Was die Weiterleitung an Anfrage und Antwort ändert.
sealed class FrameTransformer(string target, string hubOrigin, string token) : HttpTransformer
{
    private static readonly Regex FrameAncestors = new(@"frame-ancestors[^;]*", RegexOptions.IgnoreCase);

    public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage request, string destinationPrefix, CancellationToken cancel)
    {
        await base.TransformRequestAsync(context, request, destinationPrefix, cancel);

        // Der Dienst lässt nur seinen eigenen Host-Namen zu: also den des Ziels senden, nicht den der Weiterleitung.
        request.Headers.Host = null;
        if (request.Headers.Contains("Origin"))
        {
            request.Headers.Remove("Origin");
            request.Headers.TryAddWithoutValidation("Origin", target);
        }

        // Token des Dienstes einsetzen, wenn der Browser keins mitbringt. Für WebSockets nimmt Qwen den Kopf
        // Authorization auf der Upgrade-Anfrage an – ein Browser kann ihn nicht setzen, die Weiterleitung schon.
        if (token.Length > 0 && request.Headers.Authorization == null && !OffersBearerSubprotocol(request))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public override async ValueTask<bool> TransformResponseAsync(HttpContext context, HttpResponseMessage? response, CancellationToken cancel)
    {
        var ok = await base.TransformResponseAsync(context, response, cancel);

        // Das Verbot, eingebettet zu werden, gilt weiter – nur eben für alle außer der Zentrale.
        context.Response.Headers.Remove("X-Frame-Options");
        var csp = context.Response.Headers.ContentSecurityPolicy.ToString();
        if (csp.Length > 0)
            context.Response.Headers.ContentSecurityPolicy = FrameAncestors.Replace(csp, $"frame-ancestors {hubOrigin}");
        return ok;
    }

    private static bool OffersBearerSubprotocol(HttpRequestMessage request) =>
        request.Headers.TryGetValues("Sec-WebSocket-Protocol", out var values) && values.Any(v => v.Contains("qwen-bearer."));
}

// Die Hülle: Leiste oben, das Modul im Frame darunter. ?src=… setzt die Adresse im Frame (für den Vergleich ohne Weiterleitung).
static class Shell
{
    public static string Html(string frameOrigin, string target) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Stykker Hub – frame spike</title>
        <style>
          html, body { height: 100%; margin: 0; background: #060916; color: #e8eeff; font: 13px/1.45 "Segoe UI", system-ui, sans-serif; }
          body { display: flex; flex-direction: column; }
          header { display: flex; align-items: center; gap: 18px; padding: 0 16px; min-height: 48px; background: #0e1430; border-bottom: 1px solid #24325a; }
          .brand { font-family: "Cascadia Mono", Consolas, monospace; font-weight: 700; letter-spacing: .14em; }
          .brand b { color: #4fe3ff; }
          nav { display: flex; gap: 2px; flex: 1; }
          nav a { padding: 14px 10px 12px; color: #8292b8; text-decoration: none; border-bottom: 2px solid transparent; }
          nav a.on { color: #e8eeff; border-bottom-color: #4fe3ff; }
          code { font-family: "Cascadia Mono", Consolas, monospace; color: #8292b8; }
          iframe { flex: 1; width: 100%; border: 0; background: #060916; }
        </style>
        </head>
        <body>
        <header>
          <div class="brand"><b>◆</b> STYKKER <b>HUB</b></div>
          <nav>
            <a id="via" href="/?src=proxy">Qwen · through the proxy</a>
            <a id="direct" href="/?src=direct">Qwen · direct</a>
          </nav>
          <code id="where"></code>
        </header>
        <iframe id="frame" title="Module" allow="clipboard-read; clipboard-write; microphone"></iframe>
        <script>
          var query = new URLSearchParams(location.search);
          var direct = query.get('src') === 'direct';
          var src = (direct ? '{{target}}' : '{{frameOrigin}}') + '/' + location.hash;
          document.getElementById(direct ? 'direct' : 'via').className = 'on';
          document.getElementById('where').textContent = src.replace(/#.*/, '');
          document.getElementById('frame').src = src;
        </script>
        </body>
        </html>
        """;
}
