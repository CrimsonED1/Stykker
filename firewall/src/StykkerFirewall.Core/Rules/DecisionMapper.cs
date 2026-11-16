namespace StykkerFirewall.Core.Rules;

public static class DecisionMapper
{
    // Antwort im Dialog → Regel. BlockOnce ergibt keine Regel.
    // „Nur dieses Ziel“: ausgehend Gegenstelle + Zielport, eingehend Gegenstelle + eigener Port (der Port der
    // Gegenstelle wechselt bei eingehenden Verbindungen mit jedem Versuch).
    public static FirewallRule? ToRule(ConnectionAttempt attempt, PromptDecision decision, DateTimeOffset now) => decision switch
    {
        PromptDecision.AllowApp => AppWide(attempt, RuleAction.Allow, now),
        PromptDecision.BlockAlways => AppWide(attempt, RuleAction.Block, now),
        PromptDecision.AllowTarget => Target(attempt, now),
        PromptDecision.BlockOnce => null,
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, null),
    };

    private static FirewallRule AppWide(ConnectionAttempt a, RuleAction action, DateTimeOffset now) =>
        new(Guid.NewGuid(), a.AppPath, a.Direction, action, null, null, null, null, now);

    private static FirewallRule Target(ConnectionAttempt a, DateTimeOffset now)
    {
        bool ports = IpProtocol.HasPorts(a.Protocol);
        int? remotePort = ports && a.Direction == TrafficDirection.Outbound ? a.RemotePort : null;
        int? localPort = ports && a.Direction == TrafficDirection.Inbound ? a.LocalPort : null;
        return new FirewallRule(Guid.NewGuid(), a.AppPath, a.Direction, RuleAction.Allow,
            a.RemoteAddress, remotePort, localPort, a.Protocol, now);
    }
}
