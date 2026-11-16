namespace StykkerFirewall.Core.Backend;

// Die einzige Stelle, an der StykkerFirewall mit einer echten Firewall spricht. Alles darüber (Regelspeicher,
// Nachfrage, Oberfläche) kennt nur diese Schnittstelle; ein Backend lässt sich damit austauschen, ohne dass sich
// Dienst oder Oberfläche ändern.
//
// Vertrag:
// - Die Regeln von StykkerFirewall (RuleStore) sind die Wahrheit. Das Backend setzt sie nur um. Beim Start und nach
//   Änderungen gleicht der Dienst ab (Reconciler): fehlende Regeln anlegen, fremde verwaiste Regeln entfernen.
// - ListManagedRuleIdsAsync liefert nur Regeln, die dieses Backend für StykkerFirewall angelegt hat – nie Regeln von
//   Windows oder anderen Programmen.
// - WatchBlockedAsync liefert jeden blockierten Verbindungsversuch, auch solche, die eine eigene Block-Regel trifft;
//   das Filtern ist Sache des Kerns (PromptGate).
public interface IFirewallBackend : IAsyncDisposable
{
    string Id { get; }

    string DisplayName { get; }

    Task<BackendStatus> GetStatusAsync(CancellationToken ct);

    // Ask: ausgehend standardmäßig sperren und Blockierungen melden. Monitor: Standard des Systems wiederherstellen.
    Task SetModeAsync(ProtectionMode mode, CancellationToken ct);

    Task<IReadOnlyCollection<Guid>> ListManagedRuleIdsAsync(CancellationToken ct);

    // Legt die Regel an oder ersetzt eine vorhandene mit derselben Id.
    Task ApplyRuleAsync(FirewallRule rule, CancellationToken ct);

    Task RemoveRuleAsync(Guid ruleId, CancellationToken ct);

    IAsyncEnumerable<ConnectionAttempt> WatchBlockedAsync(CancellationToken ct);
}
