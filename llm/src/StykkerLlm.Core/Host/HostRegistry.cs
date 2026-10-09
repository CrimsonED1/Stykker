namespace StykkerLlm.Core;

// Ein gekoppelter Model-Host am Server. Vom Token steht nur der Hash in hosts.dat (wie bei den Geräten): wer die Datei
// liest, kann sich damit nicht als Host ausgeben.
public sealed class HostEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTime Added { get; set; }
}

public sealed class HostFile
{
    public List<HostEntry> Hosts { get; set; } = new();
}

// Die gekoppelten Hosts dieses Servers (docs/plan-hosts-gateway.md). Add gibt das Token genau einmal heraus.
public sealed class HostRegistry
{
    private readonly string _file;
    private readonly IPlatform _platform;
    private readonly object _gate = new();
    private readonly List<HostEntry> _hosts;

    public const string FileName = "hosts.dat";

    public HostRegistry(AppPaths paths, IPlatform platform)
    {
        _file = Path.Combine(paths.Root, FileName);
        _platform = platform;
        _hosts = ProtectedJson.Load<HostFile>(_file, platform).Hosts.Where(h => h.Id.Length > 0 && h.TokenHash.Length > 0).ToList();
    }

    public IReadOnlyList<HostEntry> List() { lock (_gate) return _hosts.ToList(); }

    // Neuer Host: Eintrag anlegen, das Token einmal zurückgeben (der Host speichert es, der Server nur den Hash)
    public (HostEntry Entry, string Token) Add(string name, DateTime now)
    {
        var token = AccessControl.NewToken();
        var entry = new HostEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = string.IsNullOrWhiteSpace(name) ? "host" : name.Trim()[..Math.Min(64, name.Trim().Length)],
            TokenHash = AccessControl.Hash(token),
            Added = now,
        };
        lock (_gate) _hosts.Add(entry);
        Save();
        return (entry, token);
    }

    public HostEntry? Find(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = AccessControl.Hash(token);
        lock (_gate) return _hosts.FirstOrDefault(h => h.TokenHash == hash);
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate) removed = _hosts.RemoveAll(h => h.Id == id) > 0;
        if (removed) Save();
        return removed;
    }

    private void Save()
    {
        HostFile file;
        lock (_gate) file = new HostFile { Hosts = _hosts.ToList() };
        ProtectedJson.Save(_file, file, _platform);
    }
}
