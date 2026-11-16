namespace StykkerFirewall.Core.Rules;

public enum GateVerdict
{
    Prompt,          // neue Nachfrage zeigen
    AlreadyPending,  // für diese App und Richtung steht schon eine Nachfrage offen
    QuietAfterBlock, // „Einmalig blockieren“ liegt kurz zurück; Wiederholungen der App nicht erneut fragen
    CoveredByRule,   // eine eigene Regel entscheidet schon (z. B. „Immer blockieren“)
    NotPromptable,   // System/ohne Pfad: nur protokollieren
}

// Entscheidet, ob ein blockierter Versuch eine Nachfrage auslöst. Apps wiederholen einen gescheiterten
// Verbindungsaufbau oft sofort und mehrfach; ohne dieses Tor gäbe es eine Flut von Dialogen.
public sealed class PromptGate
{
    private readonly Dictionary<string, DateTimeOffset> _blockedOnce = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public PromptGate(TimeSpan quietAfterBlockOnce) => QuietAfterBlockOnce = quietAfterBlockOnce;

    public TimeSpan QuietAfterBlockOnce { get; }

    public static string Key(string appPath, TrafficDirection direction) => $"{direction}|{appPath}";

    public GateVerdict Check(ConnectionAttempt attempt, IEnumerable<FirewallRule> rules, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(attempt.AppPath) || AppPaths.Same(attempt.AppPath, AppPaths.System))
            return GateVerdict.NotPromptable;
        if (RuleMatcher.Evaluate(rules, attempt, now) is not null)
            return GateVerdict.CoveredByRule;

        var key = Key(attempt.AppPath, attempt.Direction);
        lock (_gate)
        {
            if (_pending.Contains(key)) return GateVerdict.AlreadyPending;
            if (_blockedOnce.TryGetValue(key, out var at) && now - at < QuietAfterBlockOnce)
                return GateVerdict.QuietAfterBlock;
            _blockedOnce.Remove(key);
            _pending.Add(key);
            return GateVerdict.Prompt;
        }
    }

    // Die Nachfrage ist beantwortet (oder verworfen).
    public void Resolved(ConnectionAttempt attempt, PromptDecision? decision, DateTimeOffset now)
    {
        var key = Key(attempt.AppPath, attempt.Direction);
        lock (_gate)
        {
            _pending.Remove(key);
            if (decision == PromptDecision.BlockOnce) _blockedOnce[key] = now;
        }
    }
}
