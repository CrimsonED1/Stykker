using Stykker.Pairing;

namespace Stykker.Shared.Web;

// Was die Weiterleitung eines fremden Dienstes (docs/plan-stykkernode.md, Abschnitt 4.6 bis 4.8) entscheidet und ändert –
// alles ohne HTTP, damit es sich prüfen lässt. Der HUD-Server (hud/src/StykkerHud.Server) wendet es auf die Anfragen an.
public enum ServiceAccess
{
    Allow,           // weiter zum Dienst
    Forbidden,       // Netzschalter aus: nur diese Maschine
    Unauthorized,    // von außen ohne gekoppeltes Gerät
    NotAdmin,        // gekoppeltes Gerät mit Rolle viewer für diesen Dienst
    ForeignOrigin,   // Origin ist nicht die Seite dieser Anfrage (auch beim WebSocket)
}

public static class ServiceProxyRules
{
    // Name und Format wie bei der Zugangsprüfung: ein Gerät, das sich am Modul der Zentrale angemeldet hat, bringt es mit.
    public const string DeviceCookie = PairingGate.CookieName;

    // Ein Subprotokoll, mit dem der Browser das Token selbst mitschickt (Qwen nimmt es so an). Dann setzt die Weiterleitung keins.
    public const string BearerSubprotocol = "qwen-bearer.";

    // Kopfzeilen der Antwort, die die Weiterleitung entfernt: das Verbot, eingebettet zu werden, gilt dann über frame-ancestors.
    public static readonly IReadOnlyList<string> ResponseHeadersToDrop = ["X-Frame-Options"];

    // Die Entscheidung für eine Anfrage an der Weiterleitung. origin/scheme/host kommen aus der Anfrage; role ist die Rolle
    // des Geräts in diesem Dienst (null ohne gültiges Gerät). Die Origin-Prüfung kommt zuerst: eine fremde Seite darf
    // den Dienst auch dann nicht erreichen, wenn sie ein gekoppeltes Gerät benutzt.
    public static ServiceAccess Decide(bool loopback, bool remoteEnabled, string? role, string? origin, string scheme, string host)
    {
        if (!OriginAllowed(origin, scheme, host)) return ServiceAccess.ForeignOrigin;
        if (loopback) return ServiceAccess.Allow;
        if (!remoteEnabled) return ServiceAccess.Forbidden;
        if (role == null) return ServiceAccess.Unauthorized;
        return PairingRoles.CanWrite(role) ? ServiceAccess.Allow : ServiceAccess.NotAdmin;
    }

    public static int StatusOf(ServiceAccess access) => access switch
    {
        ServiceAccess.Allow => 200,
        ServiceAccess.Unauthorized => 401,
        _ => 403,
    };

    // Der Text, den der Browser sieht; null, wenn die Anfrage weitergeht
    public static string? MessageOf(ServiceAccess access) => access switch
    {
        ServiceAccess.Forbidden => "This service only answers this PC. Turn on network access in the Stykker hub on the PC.",
        ServiceAccess.Unauthorized => "This device is not paired. Pair it in the Stykker hub.",
        ServiceAccess.NotAdmin => "Only an admin of this module may open it. The PC can change the role in the Stykker hub.",
        ServiceAccess.ForeignOrigin => "Refused: that request did not come from this page.",
        _ => null,
    };

    // Ein fremder Dienst steht in /api/hub nur für diese Maschine und für ein Gerät mit Rolle admin dafür.
    public static bool ListsForeignService(bool loopback, string? role) => loopback || PairingRoles.CanWrite(role);

    // Gleich wie scheme://Host der Anfrage; ohne Origin (kein Browser, eine Seite der Weiterleitung selbst) gilt die Anfrage als eigene.
    public static bool OriginAllowed(string? origin, string scheme, string host) =>
        string.IsNullOrEmpty(origin) || origin.Equals($"{scheme}://{host}", StringComparison.OrdinalIgnoreCase);

    // Host-Kopf für das Ziel: "127.0.0.1:41999"
    public static string TargetHost(string target) => new Uri(target).Authority;

    // Origin auf das Ziel umschreiben; ohne Origin in der Anfrage bleibt es dabei, dass keiner gesendet wird.
    public static string? OriginFor(string? origin, string target) => string.IsNullOrEmpty(origin) ? null : target;

    // Der Wert von stykker_device aus dem Cookie-Kopf (der erste Treffer); null ohne Gerät
    public static string? DeviceToken(string? cookie)
    {
        var part = CookieParts(cookie).FirstOrDefault(IsDeviceCookie);
        if (part == null) return null;
        var value = part[(part.IndexOf('=') + 1)..].Trim();
        return value.Length > 0 ? value : null;
    }

    // Der Cookie-Kopf ohne stykker_device; die übrigen Cookies bleiben in ihrer Reihenfolge. null = nichts bleibt übrig.
    public static string? CookiesWithoutDevice(string? cookie)
    {
        var kept = CookieParts(cookie).Where(part => !IsDeviceCookie(part)).ToList();
        return kept.Count == 0 ? null : string.Join("; ", kept);
    }

    private static bool IsDeviceCookie(string part)
    {
        int eq = part.IndexOf('=');
        return eq > 0 && part[..eq] == DeviceCookie;
    }

    // Authorization: Bearer <Token>, wenn die Anfrage keins mitbringt und kein Subprotokoll das Token übernimmt.
    // null = nichts setzen.
    public static string? BearerFor(string? authorization, string? subprotocol, string? token)
    {
        if (string.IsNullOrEmpty(token) || !string.IsNullOrEmpty(authorization)) return null;
        if (HasBearerSubprotocol(subprotocol)) return null;
        return "Bearer " + token;
    }

    public static bool HasBearerSubprotocol(string? subprotocol) =>
        !string.IsNullOrEmpty(subprotocol) && subprotocol.Split(',').Any(p => p.Trim().StartsWith(BearerSubprotocol, StringComparison.Ordinal));

    // Die Content-Security-Policy der Antwort mit frame-ancestors = die Zentrale unter dem Rechnernamen der Anfrage.
    // Fehlt frame-ancestors, wird es angehängt; ohne jede CSP entsteht eine, die nur das regelt.
    public static string FrameAncestorsFor(string? csp, string scheme, string host, int hubPort)
    {
        var policy = "frame-ancestors " + FrameAncestors.Policy(scheme, host, hubPort);
        var directives = (csp ?? "").Split(';').Select(d => d.Trim()).Where(d => d.Length > 0).ToList();
        bool replaced = false;
        for (int i = 0; i < directives.Count; i++)
        {
            if (directives[i].Split(' ')[0].Equals("frame-ancestors", StringComparison.OrdinalIgnoreCase))
            {
                directives[i] = policy;
                replaced = true;
            }
        }
        if (!replaced) directives.Add(policy);
        return string.Join("; ", directives);
    }

    private static IEnumerable<string> CookieParts(string? cookie) =>
        (cookie ?? "").Split(';').Select(p => p.Trim()).Where(p => p.Length > 0);
}
