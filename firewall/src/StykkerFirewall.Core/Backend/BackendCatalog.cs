namespace StykkerFirewall.Core.Backend;

// Liste der verfügbaren Backends. Der Dienst trägt sie beim Start ein und wählt eines über die Einstellung
// „Backend“ (z. B. "windows-firewall" oder "simulator"). Ein neues Backend braucht nur einen weiteren Eintrag.
public sealed class BackendCatalog
{
    private readonly Dictionary<string, Func<IFirewallBackend>> _factories = new(StringComparer.OrdinalIgnoreCase);

    public BackendCatalog Add(string id, Func<IFirewallBackend> factory)
    {
        _factories[id] = factory;
        return this;
    }

    public IReadOnlyCollection<string> Ids => _factories.Keys;

    public IFirewallBackend Create(string id) =>
        _factories.TryGetValue(id, out var factory)
            ? factory()
            : throw new ArgumentException($"Unknown firewall backend '{id}'. Known: {string.Join(", ", _factories.Keys)}", nameof(id));
}
