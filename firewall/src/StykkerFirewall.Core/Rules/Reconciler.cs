namespace StykkerFirewall.Core.Rules;

public sealed record ReconcilePlan(IReadOnlyList<FirewallRule> ToApply, IReadOnlyList<Guid> ToRemove, IReadOnlyList<Guid> Expired);

public static class Reconciler
{
    // Vergleicht den eigenen Regelspeicher mit dem, was im Backend steht.
    // - im Speicher, gültig, im Backend fehlend → anlegen
    // - im Backend, aber nicht (mehr) gültig im Speicher → entfernen
    // - abgelaufen → aus Speicher und Backend entfernen (Expired)
    public static ReconcilePlan Plan(IEnumerable<FirewallRule> stored, IReadOnlyCollection<Guid> inBackend, DateTimeOffset now)
    {
        var backend = inBackend as IReadOnlySet<Guid> ?? new HashSet<Guid>(inBackend);
        var valid = new HashSet<Guid>();
        var apply = new List<FirewallRule>();
        var expired = new List<Guid>();

        foreach (var rule in stored)
        {
            if (rule.IsExpired(now)) { expired.Add(rule.Id); continue; }
            valid.Add(rule.Id);
            if (!backend.Contains(rule.Id)) apply.Add(rule);
        }

        var remove = backend.Where(id => !valid.Contains(id)).ToList();
        return new ReconcilePlan(apply, remove, expired);
    }
}
