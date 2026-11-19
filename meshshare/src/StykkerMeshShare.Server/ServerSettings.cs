using System.Text.Json;

namespace StykkerMeshShare.Server;

// settings.json im Datenordner: der geteilte Ordner und die Gegenstellen, die man von Hand einträgt (zum Beispiel
// Tailscale-Namen – dort gibt es keine Suche per Rundruf). Startargumente haben Vorrang.
public sealed class ServerSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _file;
    private Stored _stored;

    private sealed record Stored(string? Folder, List<string> Peers);

    public ServerSettings(string dataDir)
    {
        _file = Path.Combine(dataDir, "settings.json");
        _stored = Load() ?? new Stored(null, new List<string>());
    }

    public string? Folder { get { lock (_lock) return _stored.Folder; } }

    public IReadOnlyList<string> Peers { get { lock (_lock) return _stored.Peers.ToList(); } }

    public void SetFolder(string folder)
    {
        lock (_lock) { _stored = _stored with { Folder = folder }; Save(); }
    }

    public bool AddPeer(string address)
    {
        lock (_lock)
        {
            if (_stored.Peers.Contains(address, StringComparer.OrdinalIgnoreCase)) return false;
            _stored.Peers.Add(address);
            Save();
            return true;
        }
    }

    // Verglichen wird normalisiert: ein früher als "pc" gespeicherter Eintrag ist derselbe wie "pc:17404".
    public bool RemovePeer(string address, int defaultPort)
    {
        var wanted = NormalizeAddress(address, defaultPort) ?? address;
        lock (_lock)
        {
            bool removed = _stored.Peers.RemoveAll(p => string.Equals(NormalizeAddress(p, defaultPort) ?? p, wanted, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Save();
            return removed;
        }
    }

    private Stored? Load()
    {
        try
        {
            if (!File.Exists(_file)) return null;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_file), Json);
            return stored == null ? null : stored with { Peers = stored.Peers ?? new List<string>() };
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private void Save()
    {
        StykkerMeshShare.Core.AtomicFile.WriteJson(_file, _stored, Json);
    }

    // "host", "host:port", "[v6]:port" -> "host:port" mit dem Standardport; null, wenn es keine Adresse ist.
    public static string? NormalizeAddress(string? input, int defaultPort)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 255) return null;
        if (text.Contains("://", StringComparison.Ordinal)) text = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/');
        string host;
        int port = defaultPort;
        if (text.StartsWith('['))
        {
            int end = text.IndexOf(']');
            if (end < 2) return null;
            host = text[..(end + 1)];
            var rest = text[(end + 1)..];
            if (rest.StartsWith(':') && !int.TryParse(rest[1..], out port)) return null;
        }
        else
        {
            int colon = text.LastIndexOf(':');
            if (colon > 0 && text.IndexOf(':') == colon)
            {
                host = text[..colon];
                if (!int.TryParse(text[(colon + 1)..], out port)) return null;
            }
            else host = text;
            if (!host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) return null;
        }
        if (port is <= 0 or > 65535 || host.Length == 0) return null;
        return $"{host}:{port}";
    }
}
