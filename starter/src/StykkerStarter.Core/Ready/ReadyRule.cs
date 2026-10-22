namespace StykkerStarter.Core.Ready;

// Was „läuft heißt bereit“ prüft (ProgramEntry.ReadyCheck): eine Portnummer (geprüft wird 127.0.0.1:<Port>) oder eine
// http(s)-Adresse, deren Host localhost, 127.0.0.1 oder [::1] ist. Andere Adressen gibt es nicht: der Starter prüft nur
// dieselbe Maschine. Leer heißt: keine Prüfung.
public sealed record ReadyTarget(bool IsUrl, int Port, Uri? Url)
{
    public string Describe => IsUrl ? Url!.ToString() : $"127.0.0.1:{Port}";
}

public static class ReadyRule
{
    private static readonly string[] LocalHosts = { "localhost", "127.0.0.1", "[::1]", "::1" };

    // null, wenn der Text in Ordnung ist (leer ist in Ordnung: keine Prüfung). Sonst der Grund auf Englisch.
    public static string? Problem(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return Parse(text) == null
            ? "use a port number (for example 8080) or an http:// address on localhost"
            : null;
    }

    // Die Prüfung aus dem Text; null bei leerem oder ungültigem Text
    public static ReadyTarget? Parse(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;

        if (t.All(c => c is >= '0' and <= '9'))
            return int.TryParse(t, out var port) && port is >= 1 and <= 65535 ? new ReadyTarget(false, port, null) : null;

        bool web = t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (web && Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && LocalHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            return new ReadyTarget(true, 0, uri);

        return null;
    }
}
