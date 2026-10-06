using System.Security.Cryptography;

namespace StykkerLlm.Core;

// Geheimschlüssel für die Startbestätigung (Library.Fingerprint). Liegt als confirm.key im Datenordner, geschützt an den Windows-Benutzer
// (DPAPI CurrentUser): wer nur library.json oder den Ordner kopiert bzw. bearbeitet, kann keine gültige Bestätigung erzeugen.
// Fehlt die Datei oder lässt sie sich nicht entschlüsseln (anderer Benutzer/Rechner, beschädigt), entsteht ein neuer Schlüssel; damit
// gelten alle bisherigen Bestätigungen als ungültig und der Nutzer bestätigt die Kommandozeilen erneut.
public static class ConfirmKey
{
    public const int Size = 32;

    // create = false (nur lesen, z. B. zweite Oberfläche neben der GUI): vorhandenen Schlüssel laden, sonst Zufallsschlüssel nur im Speicher.
    public static byte[] LoadOrCreate(string path, IPlatform platform, bool create = true)
    {
        try
        {
            if (File.Exists(path))
            {
                var blob = Convert.FromBase64String(File.ReadAllText(path).Trim());
                var key = platform.UnprotectForCurrentUser(blob);
                if (key is { Length: Size }) return key;
            }
        }
        catch { /* ungültig: neu anlegen */ }

        var fresh = RandomNumberGenerator.GetBytes(Size);
        if (!create) return fresh;
        try
        {
            var protectedKey = platform.ProtectForCurrentUser(fresh);
            if (protectedKey == null) return fresh;   // keine Benutzerbindung möglich: Schlüssel gilt nur bis zum Programmende
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(path, Convert.ToBase64String(protectedKey));
        }
        catch { /* nicht schreibbar: der Schlüssel gilt dann nur für diese Sitzung */ }
        return fresh;
    }
}
