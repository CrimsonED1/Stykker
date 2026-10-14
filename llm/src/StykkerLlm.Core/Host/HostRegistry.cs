using Stykker.Pairing;

namespace StykkerLlm.Core;

// Ein gekoppelter Model-Host am Server. Vom Token steht nur der Hash in der gemeinsamen Ablage (wie bei den Geräten):
// wer die Datei liest, kann sich damit nicht als Host ausgeben.
public sealed class HostEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTime Added { get; set; }
}

// Die gekoppelten Hosts dieses Servers (docs/plan-hosts-gateway.md). Die Hosts liegen in der gemeinsamen Ablage
// (Stykker.Pairing); diese Klasse übersetzt nur. Add gibt das Token genau einmal heraus.
public sealed class HostRegistry
{
    public const string FileName = LegacyLlmImport.HostsFile;

    private readonly PairingService _pairing;

    public HostRegistry(AppPaths paths, IPlatform platform) : this(SharedPairing.Open(paths, platform)) { }

    // Ein fertiger Dienst: der Server gibt Hosts und Geräte denselben Dienst
    public HostRegistry(PairingService pairing) => _pairing = pairing;

    public IReadOnlyList<HostEntry> List() => _pairing.Hosts.Select(ToEntry).ToList();

    // Neuer Host: Eintrag anlegen, das Token einmal zurückgeben (der Host speichert es, die Ablage nur den Hash).
    // Die Zeit kommt aus der Uhr der Ablage; now bleibt für die Aufrufer, die sie schon übergeben.
    public (HostEntry Entry, string Token) Add(string name, DateTime now)
    {
        var (host, token) = _pairing.AddHost(name);
        return (ToEntry(host), token);
    }

    public HostEntry? Find(string? token) => _pairing.FindHost(token) is { } host ? ToEntry(host) : null;

    public bool Remove(string id)
    {
        try { return _pairing.RemoveHost(id); }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write("pairing store busy or unavailable: " + ex.GetType().Name);
            return false;
        }
    }

    private static HostEntry ToEntry(PairedDevice host) => new()
    {
        Id = host.Id,
        Name = host.Name,
        TokenHash = host.Hash,
        Added = SharedPairing.ToLocal(host.Created),
    };
}
