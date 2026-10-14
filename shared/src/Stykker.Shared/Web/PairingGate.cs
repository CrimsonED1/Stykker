using System.Net;
using System.Net.NetworkInformation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Stykker.Pairing;

namespace Stykker.Shared.Web;

// Was die Zugangsprüfung mit einer Anfrage tut.
public enum GateAction
{
    Allow,            // durchlassen
    Forbidden,        // von außen, aber der Netzschalter ist aus
    Unauthorized,     // von außen ohne gültiges Gerät, kein Seitenaufruf (API): 401
    RedirectToPair,   // von außen ohne gültiges Gerät, Seitenaufruf: zur Anmeldeseite
    Pair,             // die Anmeldung selbst
    WrongHost,        // der Host-Kopf ist kein Name dieser Maschine (DNS-Rebinding): vor allem anderen abgewiesen
}

// ExtraHostNames: die Zusatznamen aus stykker.json ("hostNames"), die der Host-Kopf außer den Namen dieser Maschine haben darf.
public sealed record PairingGateOptions(string Module, string Product, string Home = "/", IReadOnlyCollection<string>? ExtraHostNames = null);

// Der Zugang zu einem Modul, für alle Module gleich (docs/plan-stykkernode.md, "gate: pairing-v1"):
//   - Diese Maschine (Loopback) kommt ohne Anmeldung herein und ist Admin – so war es bei HUD und SYS immer.
//   - Von außen nur mit eingeschaltetem Netzschalter und einem gekoppelten Gerät (Cookie). Die Geräte stehen in der
//     gemeinsamen Ablage (Stykker.Pairing): ein Gerät, das sich an einem Modul angemeldet hat, kennt auch dieses.
//   - Ohne alles von außen erreichbar: /api/ping, die Anmeldung unter /pair und das, was der Browser zum Installieren
//     als App ohne Cookie holt (Manifest, Symbole).
//   - Vor allem anderen: der Host-Kopf muss ein Name dieser Maschine sein (ServiceProxyRules.HostAllowed). Sonst
//     DNS-Rebinding: eine fremde Seite zeigt ihren Namen auf 127.0.0.1, und die Anfrage sähe wie Loopback aus.
// Hinter einer Weiterleitung auf derselben Maschine sähe jede Anfrage wie Loopback aus. Das Modul darf dann nur mit
// einem Schlüssel des Knotens der weitergereichten Herkunft trauen (Plan, Abschnitt 4.6) – das gibt es noch nicht.
public static class PairingGate
{
    // Name und Laufzeit wie bei StykkerLLM: ein dort angemeldetes Gerät bringt genau dieses Cookie mit.
    public const string CookieName = "stykker_device";
    public const string GateName = "pairing-v1";
    private const string RoleItem = "stykker.role";
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(730);

    // Die Entscheidung ohne HTTP, damit sie sich prüfen lässt. role = Rolle des Geräts in diesem Modul, null ohne Gerät.
    public static GateAction Decide(bool loopback, bool remoteEnabled, string path, bool wantsPage, string? role)
    {
        bool pair = path.Equals("/pair", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/pair/", StringComparison.OrdinalIgnoreCase);
        if (loopback) return pair ? GateAction.Pair : GateAction.Allow;
        if (!remoteEnabled) return GateAction.Forbidden;
        if (pair) return GateAction.Pair;
        if (IsOpen(path) || role != null) return GateAction.Allow;
        return wantsPage ? GateAction.RedirectToPair : GateAction.Unauthorized;
    }

    // Mit dem Host-Kopf: der wird vor allem anderen geprüft, auch von dieser Maschine und auch für /api/ping, /pair und das
    // Manifest. machineName und domainName wie bei ServiceProxyRules.HostAllowed (domainName null, wenn keine DNS-Endung).
    // extraNames: die Zusatznamen aus stykker.json (null = keine).
    public static GateAction Decide(string? hostHeader, string machineName, string? domainName, bool loopback, bool remoteEnabled,
        string path, bool wantsPage, string? role, IReadOnlyCollection<string>? extraNames = null) =>
        ServiceProxyRules.HostAllowed(hostHeader, machineName, domainName, extraNames)
            ? Decide(loopback, remoteEnabled, path, wantsPage, role)
            : GateAction.WrongHost;

    // Ohne Anmeldung erreichbar: nichts davon verrät etwas über die Maschine.
    public static bool IsOpen(string path) =>
        path.Equals("/api/ping", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/manifest.webmanifest", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/hub-icon", StringComparison.OrdinalIgnoreCase);

    // Als erstes in die Kette: auch statische Dateien liegen dahinter.
    public static void Use(WebApplication app, PairingService pairing, PairingGateOptions options)
    {
        // Der Name, unter dem dieser PC erreichbar ist (für die Host-Prüfung)
        string machineName = Environment.MachineName;
        string? domainName = IPGlobalProperties.GetIPGlobalProperties().DomainName;
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var remote = context.Connection.RemoteIpAddress;
            bool loopback = IsLoopback(remote);
            bool remoteEnabled = !loopback && pairing.RemoteEnabled;
            string path = request.Path.Value ?? "/";
            string? role = loopback
                ? PairingRoles.Admin
                : remoteEnabled ? pairing.RoleOf(request.Cookies[CookieName], options.Module, AddressText(remote)) : null;
            bool wantsPage = HttpMethods.IsGet(request.Method) && !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
            string? hostHeader = request.Headers.Host.ToString();

            switch (Decide(hostHeader, machineName, domainName, loopback, remoteEnabled, path, wantsPage, role, options.ExtraHostNames))
            {
                case GateAction.WrongHost:
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync("Wrong host name. Open this page by the PC's address.");
                    return;
                case GateAction.Allow:
                    if (path.Equals("/api/ping", StringComparison.OrdinalIgnoreCase))
                    {
                        await context.Response.WriteAsJsonAsync(new { ok = true, module = options.Module, app = options.Product, gate = GateName });
                        return;
                    }
                    context.Items[RoleItem] = role;
                    await next();
                    return;
                case GateAction.Forbidden:
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync($"{options.Product} only answers this PC. Turn on network access in the Stykker hub on the PC.");
                    return;
                case GateAction.Unauthorized:
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { ok = false, message = "This device is not paired." });
                    return;
                case GateAction.RedirectToPair:
                    context.Response.Redirect("/pair");
                    return;
                default:
                    await PairAsync(context, pairing, options);
                    return;
            }
        });
    }

    // Die Rolle des Aufrufers in diesem Modul (nach der Prüfung): "admin", "viewer" oder null.
    public static string? Role(HttpContext context) => context.Items[RoleItem] as string;

    public static bool IsAdmin(HttpContext context) => PairingRoles.CanWrite(Role(context));

    public static bool IsLocal(HttpContext context) => IsLoopback(context.Connection.RemoteIpAddress);

    public static bool IsLoopback(IPAddress? address) =>
        address != null && (IPAddress.IsLoopback(address) || (address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4())));

    // Für die Geräteliste: "192.168.0.23" statt "::ffff:192.168.0.23" (so meldet ein Port, der beide Familien annimmt, IPv4)
    public static string? AddressText(IPAddress? address) =>
        address == null ? null : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    // /pair zeigt die Anmeldeseite; /pair?code=… meldet das Gerät an, legt das Cookie und führt zur Startseite.
    private static async Task PairAsync(HttpContext context, PairingService pairing, PairingGateOptions options)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.ContentType = "text/html; charset=utf-8";
        string code = context.Request.Query["code"].ToString();
        if (code.Length == 0)
        {
            await response.WriteAsync(PairPage(options, ""));
            return;
        }

        var paired = pairing.Pair(code, DeviceName(context.Request.Headers.UserAgent.ToString()), AddressText(context.Connection.RemoteIpAddress), options.Module);
        if (paired == null)
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            await response.WriteAsync(PairPage(options, "That code is wrong or no longer valid. The PC shows the current one."));
            return;
        }
        response.Cookies.Append(CookieName, paired.Value.Token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            MaxAge = CookieLifetime,
            Path = "/",
        });
        response.Redirect(options.Home);
    }

    // "Chrome on Android": genug, um das Handy in der Geräteliste vom Tablet zu unterscheiden.
    public static string DeviceName(string userAgent)
    {
        string browser = userAgent.Contains("Edg", StringComparison.Ordinal) ? "Edge"
            : userAgent.Contains("Firefox", StringComparison.Ordinal) || userAgent.Contains("FxiOS", StringComparison.Ordinal) ? "Firefox"
            : userAgent.Contains("Chrome", StringComparison.Ordinal) || userAgent.Contains("CriOS", StringComparison.Ordinal) ? "Chrome"
            : userAgent.Contains("Safari", StringComparison.Ordinal) ? "Safari" : "Browser";
        string system = userAgent.Contains("Android", StringComparison.Ordinal) ? "Android"
            : userAgent.Contains("iPhone", StringComparison.Ordinal) ? "iPhone"
            : userAgent.Contains("iPad", StringComparison.Ordinal) ? "iPad"
            : userAgent.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : userAgent.Contains("Mac OS X", StringComparison.Ordinal) ? "macOS"
            : userAgent.Contains("Linux", StringComparison.Ordinal) ? "Linux" : "";
        return system.Length > 0 ? $"{browser} on {system}" : browser;
    }

    // Die Anmeldeseite steht für sich: ohne Design-System und ohne Skript-Dateien, denn die liegen hinter der Prüfung.
    private static string PairPage(PairingGateOptions options, string error) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="theme-color" content="#0e1430">
        <title>Sign in – {{WebUtility.HtmlEncode(options.Product)}}</title>
        <style>
          body{margin:0;min-height:100vh;display:grid;place-items:center;background:#060916;color:#e8eeff;font:15px/1.45 "Segoe UI",system-ui,sans-serif}
          main{box-sizing:border-box;width:min(380px,calc(100vw - 32px));padding:22px;background:#111a33;border:1px solid #4fe3ff40;border-radius:12px}
          .brand{font:700 12px "Cascadia Mono",Consolas,monospace;letter-spacing:.14em}
          .brand b{color:#4fe3ff}
          h1{font-size:18px;margin:14px 0 6px}
          p{margin:0 0 14px;color:#8292b8}
          input{width:100%;box-sizing:border-box;padding:12px;font:600 26px "Cascadia Mono",Consolas,monospace;letter-spacing:.2em;text-align:center;color:#4fe3ff;background:#0e0f26;border:1px solid #24325a;border-radius:6px}
          input:focus{outline:2px solid #4fe3ff;outline-offset:2px}
          button{width:100%;margin-top:12px;padding:11px;font:600 15px inherit;color:#060916;background:#4fe3ff;border:0;border-radius:6px;cursor:pointer}
          .error{color:#ff5c7a}
          small{display:block;margin-top:14px;color:#8292b8}
        </style>
        </head>
        <body>
        <main>
          <div class="brand"><b>◆</b> STYKKER</div>
          <h1>Enter the code from your PC</h1>
          <p>Open the Stykker hub on the PC. It shows six digits under “Add a device”.</p>
          {{(error.Length > 0 ? $"<p class=\"error\" role=\"alert\">{WebUtility.HtmlEncode(error)}</p>" : "")}}
          <form method="get" action="/pair">
            <input name="code" inputmode="numeric" autocomplete="one-time-code" pattern="[0-9 \-]*" maxlength="9" aria-label="Code" autofocus required>
            <button type="submit">Sign in</button>
          </form>
          <small>The code works once and for ten minutes. After five wrong tries the PC shows a new one.</small>
        </main>
        </body>
        </html>
        """;
}
