namespace StykkerFirewall.Core.Rules;

public static class RuleMatcher
{
    // Ob die Regel den Versuch abdeckt. Leere Felder der Regel passen auf alles.
    public static bool Matches(FirewallRule rule, ConnectionAttempt attempt)
    {
        if (rule.Direction != attempt.Direction) return false;
        if (!AppPaths.Same(rule.AppPath, attempt.AppPath)) return false;
        if (rule.Protocol is { } p && p != attempt.Protocol) return false;
        if (rule.RemoteAddress is { } a && !string.Equals(a, attempt.RemoteAddress, StringComparison.OrdinalIgnoreCase)) return false;
        if (rule.RemotePort is { } rp && rp != attempt.RemotePort) return false;
        if (rule.LocalPort is { } lp && lp != attempt.LocalPort) return false;
        return true;
    }

    // Wie die Windows-Firewall: eine passende Block-Regel gewinnt immer gegen Allow.
    public static RuleAction? Evaluate(IEnumerable<FirewallRule> rules, ConnectionAttempt attempt, DateTimeOffset now)
    {
        RuleAction? result = null;
        foreach (var rule in rules)
        {
            if (rule.IsExpired(now) || !Matches(rule, attempt)) continue;
            if (rule.Action == RuleAction.Block) return RuleAction.Block;
            result = RuleAction.Allow;
        }
        return result;
    }
}
