using Microsoft.AspNetCore.Http;

namespace Stykker.Shared.Web;

// Lokale Anfragen: eine Aktion darf nur die eigene Seite auslösen. Ein POST von einer fremden Seite trägt einen anderen
// Origin (oder Sec-Fetch-Site) und fällt hier heraus. Fehlt beides (kein Browser, z. B. curl), bleibt der JSON-Zwang
// als Schutz – ein fremdes Formular kann den ohne CORS-Prüfung nicht senden.
public static class LocalRequests
{
    public static bool SameOrigin(HttpContext ctx)
    {
        string origin = ctx.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin)) return origin == $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        string site = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
        return site.Length == 0 || site == "same-origin";
    }

    // Fremde Herkunft: 403, und die Aktion läuft nicht. Ergebnisse: 200, oder 409, wenn sie nicht geklappt hat.
    public static IResult Guarded(HttpContext ctx, Func<ActionResult> action)
    {
        if (!SameOrigin(ctx))
            return Results.Json(new ActionResult(false, "Refused: that request did not come from this page."), statusCode: StatusCodes.Status403Forbidden);
        var result = action();
        return Results.Json(result, statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
    }
}
