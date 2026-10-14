using System.Text;

namespace Stykker.Pairing;

// Das Token eines fremden Dienstes aus einer Datei (docs/plan-stykkernode.md, Abschnitt 4.5). Die Desktop-App des Dienstes
// legt es ab; die Zentrale liest es und gibt es nur an ihre eigene Weiterleitung weiter. Zwei Arten, je nach "protection":
//   "none":                     die Datei enthält das Token als Text (Leerraum am Ende fällt weg)
//   "dpapi-user:<Zusatzwert>":  die Datei enthält den rohen DPAPI-Blob des Benutzers mit diesem Zusatzwert
// Alles andere wird abgelehnt. Ein Grund enthält nie den Inhalt der Datei und nie das Token.
public sealed record ServiceTokenRead(string? Token, string? Problem);

public sealed class ServiceTokenFile
{
    public const string NoProtection = "none";
    public const string DpapiPrefix = "dpapi-user:";
    // Ein Token ist kurz; alles darüber ist nicht die Datei, die gemeint ist
    public const int MaxFileBytes = 1024;

    private readonly string _protection;
    private readonly Func<string, IDataProtector>? _protectorFor;
    private readonly object _gate = new();
    private ServiceTokenRead? _cached;
    private DateTime _stamp;
    private long _length;

    // protectorFor bekommt den Zusatzwert und liefert den Schutz dafür (in den Tests ein Ersatz für DPAPI).
    public ServiceTokenFile(string file, string protection, Func<string, IDataProtector>? protectorFor = null)
    {
        FilePath = Path.GetFullPath(file);
        _protection = protection;
        _protectorFor = protectorFor;
    }

    public string FilePath { get; }

    // "none" oder "dpapi-user:" mit einem Zusatzwert; alles andere ist ungültig
    public static bool ProtectionKnown(string? protection) =>
        protection == NoProtection
        || (protection != null && protection.StartsWith(DpapiPrefix, StringComparison.Ordinal) && protection.Length > DpapiPrefix.Length);

    // Das Token im Augenblick. Die Datei wird nur neu gelesen, wenn sich Änderungszeit oder Länge geändert haben.
    public ServiceTokenRead Current()
    {
        lock (_gate)
        {
            var info = new FileInfo(FilePath);
            bool exists = info.Exists;
            var stamp = exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            var length = exists ? info.Length : -1;
            if (_cached == null || stamp != _stamp || length != _length)
            {
                _cached = Read(FilePath, _protection, _protectorFor);
                _stamp = stamp;
                _length = length;
            }
            return _cached;
        }
    }

    // Liest und prüft die Datei einmal. Der Schutz kommt aus protectorFor; ohne Schutz (Linux) ist eine DPAPI-Datei nicht lesbar.
    public static ServiceTokenRead Read(string file, string protection, Func<string, IDataProtector>? protectorFor = null)
    {
        if (!ProtectionKnown(protection)) return Refused("tokenProtection is neither none nor dpapi-user:<name>");
        if (!File.Exists(file)) return Refused("the token file is missing");

        byte[] bytes;
        try
        {
            if (new FileInfo(file).Length > MaxFileBytes) return Refused("the token file is too large");
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refused("the token file cannot be read");
        }

        string text;
        if (protection == NoProtection)
        {
            text = Encoding.UTF8.GetString(bytes);
        }
        else
        {
            var entropy = protection[DpapiPrefix.Length..];
            var protector = protectorFor?.Invoke(entropy) ?? DataProtectors.ForCurrentUser(entropy);
            var plain = protector.Unprotect(bytes);
            if (plain == null) return Refused("the token cannot be unprotected for this user");
            text = Encoding.UTF8.GetString(plain);
        }

        // Ein Byte-Order-Mark (Windows-Editoren) und Leerraum am Ende gehören nicht zum Token
        var token = text.TrimStart('﻿').TrimEnd();
        return ValidToken(token) ? new ServiceTokenRead(token, null) : Refused("the token has an unexpected form");
    }

    // Ein Token: sichtbare ASCII-Zeichen ohne Leerraum (es geht in einen HTTP-Kopf), in vernünftiger Länge
    private static bool ValidToken(string token) =>
        token.Length > 0 && token.Length <= 512 && token.All(c => c is >= '!' and <= '~');

    private static ServiceTokenRead Refused(string reason) => new(null, reason);
}
