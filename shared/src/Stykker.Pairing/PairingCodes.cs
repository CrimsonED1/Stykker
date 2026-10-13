using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Stykker.Pairing;

// Codes, Token und ihr Hash. Verfahren und Präfix sind die von StykkerLLM (AccessControl): nur so bleibt ein Gerät
// angemeldet, das sich vor der zentralen Kopplung dort angemeldet hat, und ein Host behält sein Token.
public static class PairingCodes
{
    // Sechs Ziffern wie beim Fernseher: am Telefon schnell getippt. Kurz gültig, nach einer Anmeldung verbraucht,
    // nach MaxWrongTries Fehlversuchen gibt es einen neuen – so bleibt Raten aussichtslos (1 : 1 000 000 je Versuch).
    public const int CodeLength = 6;
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public const int MaxWrongTries = 5;
    public const int MaxPendingRequests = 8;

    // Nicht ändern: mit einem anderen Präfix passt kein gespeicherter Hash mehr.
    internal const string HashPrefix = "StykkerLLM.device.v1|";
    private const int TokenBytes = 32;

    public static string NewCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    public static bool IsCode(string? code) => code is { Length: CodeLength } && code.All(char.IsAsciiDigit);

    // Eingaben wie "482 913" oder "482-913" gelten wie "482913"
    public static string CleanCode(string? code) => new((code ?? "").Where(char.IsAsciiDigit).ToArray());

    // "482 913" zum Ablesen
    public static string Pretty(string? code) => code is { Length: CodeLength } ? code[..3] + " " + code[3..] : code ?? "";

    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

    public static string Hash(string token) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(HashPrefix + token)));

    // Vergleich in fester Zeit, damit sich nicht per Timing erraten lässt, ob ein Code oder Hash passt
    public static bool Same(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a.Trim());
        var y = Encoding.UTF8.GetBytes(b.Trim());
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
