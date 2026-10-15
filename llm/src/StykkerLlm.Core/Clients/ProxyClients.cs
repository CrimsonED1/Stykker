namespace StykkerLlm.Core;

// Alle Proxy-Clients dieses Servers. Jetzt nur Claude. Hier kommt Codex dazu: eine Klasse wie ClaudeClient
// (IProxyClient) und ein Eintrag in der Liste, die MonitorEngine übergibt.
public sealed class ProxyClients
{
    private readonly List<IProxyClient> _all;

    public ProxyClients(IEnumerable<IProxyClient> clients)
    {
        _all = clients.ToList();
        var dup = _all.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new InvalidOperationException($"duplicate client id '{dup.Key}'");
    }

    public IReadOnlyList<IProxyClient> All => _all;

    // Der Client mit dieser Kennung, sonst null (Groß-/Kleinschreibung egal)
    public IProxyClient? Get(string? id) =>
        string.IsNullOrEmpty(id) ? null : _all.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
