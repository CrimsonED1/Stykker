using System.Text;
using System.Text.Json;

namespace Stykker.Shared.Web;

// Qwens Local Control (docs/plan-stykkernode.md, Abschnitt 10): die URL aus Qwen trägt ein gemeinsames Geheimnis im
// Fragment (#token=…). Ein Gerät tauscht es bei seiner ersten Anfrage gegen ein eigenes Geräte-Token; die Zentrale macht
// das einmal beim Einfügen, speichert das Geräte-Token (nie das Geheimnis) und erscheint in Qwens Geräteliste.
//
// Der Tausch ist eine Anfrage: POST {Adresse}/workspace/local-control/devices mit "Authorization: Bearer <Geheimnis>" und
// dem Körper {} (oder {"previousToken": …}, wenn schon ein Geräte-Token da ist; dann kommt "reused" statt eines neuen
// Tokens). Der Name in Qwens Geräteliste kommt aus dem User-Agent, deshalb schickt die Zentrale "Stykker hub".
//
// Keine Meldung enthält das Geheimnis, ein Geräte-Token oder die URL mit dem Fragment.
public static class LocalControlExchange
{
    public const string DevicesPath = "/workspace/local-control/devices";
    public const string DeviceLabel = "Stykker hub";
    public const int MaxTokenLength = 512;

    // Ein Ergebnis: Ok mit dem Token zum Speichern, oder Ok false mit einem Text für den Benutzer (ohne Geheimnis).
    public sealed record Result(bool Ok, string? Token, string Message);

    // Die URL aus Qwen: http(s)://<Adresse>:<Port>/#token=<Geheimnis>. Ergibt die Adresse ohne Pfad (scheme://host:port)
    // und das Geheimnis. Der Port ist Pflicht; ein Pfad oder eine Suche außer dem Fragment gehört nicht dazu.
    public static bool TryParseUrl(string? url, out string baseUrl, out string secret)
    {
        baseUrl = "";
        secret = "";
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https") || !HasExplicitPort(url.Trim())) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query)) return false;

        // Das Fragment ist genau ein Wert: #token=<Geheimnis>
        var fragment = uri.Fragment.TrimStart('#');
        const string prefix = "token=";
        if (!fragment.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var value = Uri.UnescapeDataString(fragment[prefix.Length..]);
        if (!IsSecret(value)) return false;

        baseUrl = $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        secret = value;
        return true;
    }

    // Tauscht das Geheimnis gegen ein Geräte-Token. previousToken ist das Geräte-Token, das die Zentrale schon hat (beim
    // Ersetzen der URL), oder null. Der Aufrufer legt den HttpClient an (ohne Weiterleitung, mit Zeitlimit).
    public static async Task<Result> ExchangeAsync(HttpClient client, string baseUrl, string secret, string? previousToken, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + DevicesPath);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
        request.Headers.TryAddWithoutValidation("User-Agent", DeviceLabel);
        var body = previousToken != null && IsPlainToken(previousToken)
            ? JsonSerializer.Serialize(new { previousToken })
            : "{}";
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancel);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Fail("the address does not answer. Turn on Local Control in Qwen and copy the URL again.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode) return Fail(MessageFor((int)response.StatusCode));

            string text;
            try { text = await response.Content.ReadAsStringAsync(cancel); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                return Fail("the answer could not be read.");
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                var token = root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (token != null && IsPlainToken(token)) return new Result(true, token, "Paired with a device token of its own.");

                // Ein bekanntes Gerät: Qwen gibt kein neues Token, das alte gilt weiter
                bool reused = root.TryGetProperty("reused", out var r) && r.ValueKind == JsonValueKind.True;
                if (reused && previousToken != null && IsPlainToken(previousToken)) return new Result(true, previousToken, "Still paired with the device token it had.");
            }
            catch (JsonException)
            {
                // unten als Fehler
            }
            return Fail("the answer had no device token.");
        }
    }

    // Die Meldung zu einer Antwort ohne Erfolg. Ohne Text aus der Antwort, nur die Art des Fehlers.
    private static string MessageFor(int status) => status switch
    {
        401 or 403 => "Qwen does not accept this URL. Copy the URL again from Qwen's Local Control.",
        404 => "Qwen answers, but Local Control is not on. Turn it on in Qwen and copy the URL again.",
        409 => "Qwen did not pair the device: its device limit is reached, or the address is not Local Control's. Remove a device in Qwen, then try again.",
        _ => $"Qwen answered with status {status}.",
    };

    private static Result Fail(string message) => new(false, null, message);

    // Sichtbare ASCII-Zeichen ohne Leerraum (es geht in einen HTTP-Kopf), in der Länge begrenzt
    private static bool IsPlainToken(string token) =>
        token.Length is > 0 and <= MaxTokenLength && token.All(c => c is >= '!' and <= '~');

    // Das Geheimnis aus der URL: nur die Zeichen, die in einem Fragment unverändert bleiben (RFC 3986, unreserved). Das
    // ist das Format, das Qwen erzeugt (base64url); ein "#" oder ein Prozentzeichen im Wert ist ein Fehler der URL.
    private static bool IsSecret(string secret) =>
        secret.Length is > 0 and <= MaxTokenLength && secret.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~');

    // Nennt der Text den Port ausdrücklich (auch den der Vorgabe)? Der Host-Kopf des Tauschs muss der Adresse gleichen,
    // mit der Qwen seinen Zugang angelegt hat, und die steht mit Port in der URL.
    private static bool HasExplicitPort(string url)
    {
        int start = url.IndexOf("//", StringComparison.Ordinal);
        if (start < 0) return false;
        var authority = url[(start + 2)..].Split('/', '?', '#')[0];
        int colon = authority.LastIndexOf(':');
        return colon > authority.LastIndexOf(']') && colon < authority.Length - 1;
    }
}
