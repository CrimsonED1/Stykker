namespace StykkerLlm.Core;

// Wer die Oberfläche in einem Frame zeigen darf. Ohne Zentrale nur sie selbst (der Schutz gegen Clickjacking, den
// Blazor von sich aus setzt). Mit Zentrale zusätzlich deren Adresse: derselbe Rechnername wie die Anfrage, der Port
// der Zentrale. Derselbe Name ist Bedingung, weil der Browser das Gerätecookie nur dann in den Frame mitgibt.
public static class FramePolicy
{
    public const string Self = "'self'";

    // Der Wert für "frame-ancestors". host ist der Host-Kopf der Anfrage (mit oder ohne Port) und kommt vom Aufrufer:
    // alles außer einem einfachen Namen oder einer Adresse fällt auf 'self' zurück, statt im Kopf zu landen.
    public static string Ancestors(string scheme, string? host, int hubPort)
    {
        if (hubPort is <= 0 or > 65535) return Self;
        if (scheme is not ("http" or "https")) return Self;
        var name = HostName(host);
        return name == null ? Self : $"{Self} {scheme}://{name}:{hubPort}";
    }

    // "pc:17401" -> "pc", "[::1]:17401" -> "[::1]"; null bei allem, was kein Name und keine Adresse ist
    private static string? HostName(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 255) return null;
        string name;
        if (host[0] == '[')
        {
            int end = host.IndexOf(']');
            if (end < 2) return null;
            name = host[..(end + 1)];
            if (!name[1..^1].All(c => char.IsAsciiHexDigit(c) || c == ':' || c == '.')) return null;
            return name;
        }
        int colon = host.IndexOf(':');
        name = colon < 0 ? host : host[..colon];
        return name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ? name : null;
    }
}
