using System.Security.Cryptography;
using System.Text;

namespace StykkerLlm.Core;

// Die API-Schlüssel der Cloud-Anbieter, die der eine Stykker-Proxy bedient (Nr. 46). Je Anbieter eine Datei im Ordner
// <Datenordner>/providers, an den Windows-Benutzer gebunden (DPAPI CurrentUser, wie confirm.key): der Schlüssel steht damit
// weder im Klartext in settings.json noch in der Anzeige, im Protokoll oder im Zustands-JSON. Lässt sich ein Blob nicht
// entschlüsseln (anderer Benutzer/Rechner), gilt der Anbieter als ohne Schlüssel – ohne Schlüssel fragt der Proxy ihn nicht ab.
public sealed class ProviderKeys
{
    public const string DirName = "providers";

    private readonly string _dir;
    private readonly IPlatform _platform;
    private readonly Dictionary<string, string> _memory = new(StringComparer.OrdinalIgnoreCase);

    public ProviderKeys(string root, IPlatform platform)
    {
        _platform = platform;
        _dir = Path.Combine(root, DirName);
    }

    // Der Dateiname kommt aus der Basis-URL: derselbe Anbieter findet denselben Schlüssel wieder, ein anderer nicht.
    private string FileFor(string baseUrl) => Path.Combine(_dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(baseUrl))))[..24] + ".key");

    public static string Normalize(string baseUrl) => (baseUrl ?? "").Trim().TrimEnd('/');

    // Nur Lesezugriff für den Proxy: der Wert bleibt in dieser Schicht und geht nur in die Authorization-Kopfzeile
    public string? Get(string baseUrl)
    {
        var key = Normalize(baseUrl);
        if (key.Length == 0) return null;
        lock (_memory)
            if (_memory.TryGetValue(key, out var cached)) return cached.Length > 0 ? cached : null;
        try
        {
            var file = FileFor(key);
            if (!File.Exists(file)) return null;
            var plain = _platform.UnprotectForCurrentUser(Convert.FromBase64String(File.ReadAllText(file).Trim()));
            if (plain == null || plain.Length == 0) return null;
            var value = Encoding.UTF8.GetString(plain);
            lock (_memory) _memory[key] = value;
            return value;
        }
        catch { return null; }
    }

    // Leerer Schlüssel = den Anbieter wieder ohne Schlüssel (er wird dann nicht abgefragt)
    public void Set(string baseUrl, string key)
    {
        var k = Normalize(baseUrl);
        if (k.Length == 0) return;
        key = (key ?? "").Trim();
        lock (_memory) _memory[k] = key;
        var file = FileFor(k);
        if (key.Length == 0)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
            return;
        }
        try
        {
            var plain = Encoding.UTF8.GetBytes(key);
            var blob = _platform.ProtectForCurrentUser(plain);
            // Ohne Benutzerbindung (Linux, Simulator) bleibt der Schlüssel nur in diesem Programm; nichts wird ungeschützt abgelegt
            if (blob == null) return;
            Directory.CreateDirectory(_dir);
            AtomicFile.WriteAllText(file, Convert.ToBase64String(blob));
        }
        catch { /* nicht schreibbar: der Schlüssel gilt dann nur für diese Sitzung */ }
    }

    public void Remove(string baseUrl) => Set(baseUrl, "");

    // Steht für diesen Anbieter ein Schlüssel bereit? (Anzeige ja/nein, nie der Wert selbst)
    public bool Has(string baseUrl) => Get(baseUrl) is { Length: > 0 };
}