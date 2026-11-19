namespace StykkerMeshShare.Core;

// Versionsvektor: je PC ein Zähler. Ein Vektor ist neuer, wenn er überall mindestens so groß und irgendwo größer ist.
// Ist keiner neuer, wurde die Datei auf zwei PCs unabhängig geändert – ein Konflikt.
public enum Order { Equal, LocalNewer, RemoteNewer, Concurrent }

public static class VersionVector
{
    public static Order Compare(IReadOnlyDictionary<string, long> local, IReadOnlyDictionary<string, long> remote)
    {
        bool localBigger = false, remoteBigger = false;
        foreach (var key in local.Keys.Union(remote.Keys))
        {
            local.TryGetValue(key, out var l);
            remote.TryGetValue(key, out var r);
            if (l > r) localBigger = true;
            else if (r > l) remoteBigger = true;
        }
        return (localBigger, remoteBigger) switch
        {
            (false, false) => Order.Equal,
            (true, false) => Order.LocalNewer,
            (false, true) => Order.RemoteNewer,
            _ => Order.Concurrent,
        };
    }

    public static Dictionary<string, long> Merge(IReadOnlyDictionary<string, long> a, IReadOnlyDictionary<string, long> b)
    {
        var merged = new Dictionary<string, long>(a);
        foreach (var (key, value) in b)
            if (!merged.TryGetValue(key, out var existing) || value > existing) merged[key] = value;
        return merged;
    }

    public static Dictionary<string, long> Bump(IReadOnlyDictionary<string, long> version, string nodeId)
    {
        var next = new Dictionary<string, long>(version);
        next[nodeId] = next.GetValueOrDefault(nodeId) + 1;
        return next;
    }
}
