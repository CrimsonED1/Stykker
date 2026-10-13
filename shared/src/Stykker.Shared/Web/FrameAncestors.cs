using Microsoft.AspNetCore.Builder;

namespace Stykker.Shared.Web;

// Wer eine Seite in einem Frame zeigen darf: sie selbst und die Zentrale unter demselben Rechnernamen. Ohne diesen
// Kopf könnte jede fremde Seite ein Modul einbetten und Klicks darauf lenken – sobald das Modul im Netz erreichbar
// ist, zählt das. Dieselbe Regel wie in StykkerLLM (FramePolicy).
public static class FrameAncestors
{
    public const string Self = "'self'";

    // host ist der Host-Kopf der Anfrage und kommt vom Aufrufer: alles außer einem einfachen Namen oder einer
    // Adresse fällt auf 'self' zurück, statt im Kopf der Antwort zu landen.
    public static string Policy(string scheme, string? host, int hubPort)
    {
        if (hubPort is <= 0 or > 65535 || scheme is not ("http" or "https")) return Self;
        var name = HostName(host);
        return name == null ? Self : $"{Self} {scheme}://{name}:{hubPort}";
    }

    public static void Use(WebApplication app, int hubPort) => app.Use(async (context, next) =>
    {
        context.Response.Headers.ContentSecurityPolicy =
            "frame-ancestors " + Policy(context.Request.Scheme, context.Request.Host.Value, hubPort);
        await next();
    });

    // "pc:8077" -> "pc", "[::1]:8077" -> "[::1]"; null bei allem, was kein Name und keine Adresse ist
    private static string? HostName(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 255) return null;
        if (host[0] == '[')
        {
            int end = host.IndexOf(']');
            if (end < 2) return null;
            var literal = host[..(end + 1)];
            return literal[1..^1].All(c => char.IsAsciiHexDigit(c) || c is ':' or '.') ? literal : null;
        }
        int colon = host.IndexOf(':');
        var name = colon < 0 ? host : host[..colon];
        return name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ? name : null;
    }
}
