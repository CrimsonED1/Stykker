using StykkerFirewall.Core.Backend;
using StykkerFirewall.Core.Ipc;
using StykkerFirewall.Core.Rules;

namespace StykkerFirewall.Core;

// Das Herz des Dienstes: nimmt blockierte Versuche vom Backend, entscheidet über Nachfragen, setzt Antworten als
// Regeln um und hält Regelspeicher und Backend gleich. Kennt kein Windows und keine Pipe – der Dienst hängt die
// Pipe an die Ereignisse, Tests hängen sich direkt an.
public sealed class FirewallCoordinator
{
    private readonly IFirewallBackend _backend;
    private readonly RuleStore _store;
    private readonly PromptGate _gate;
    private readonly TimeProvider _time;
    private readonly int _recentCapacity;
    private readonly object _lock = new();
    private readonly List<PendingPrompt> _pending = [];
    private readonly LinkedList<ActivityEntry> _recent = new();

    public FirewallCoordinator(IFirewallBackend backend, RuleStore store, PromptGate gate, TimeProvider time, int recentCapacity = 500)
    {
        _backend = backend;
        _store = store;
        _gate = gate;
        _time = time;
        _recentCapacity = recentCapacity;
    }

    public event Action<PendingPrompt>? PromptRaised;

    public event Action<ActivityEntry>? ActivityAdded;

    // Regeln, offene Nachfragen oder Status haben sich geändert.
    public event Action? StateChanged;

    public IFirewallBackend Backend => _backend;

    public async Task StartAsync(CancellationToken ct)
    {
        await _backend.SetModeAsync(_store.Mode, ct).ConfigureAwait(false);
        await ReconcileAsync(ct).ConfigureAwait(false);
    }

    // Läuft, bis ct abbricht oder das Backend aufhört zu melden.
    public async Task RunAsync(CancellationToken ct)
    {
        await foreach (var attempt in _backend.WatchBlockedAsync(ct).ConfigureAwait(false))
            Handle(attempt);
    }

    public async Task ReconcileAsync(CancellationToken ct)
    {
        var inBackend = await _backend.ListManagedRuleIdsAsync(ct).ConfigureAwait(false);
        var plan = Reconciler.Plan(_store.Rules, inBackend, _time.GetUtcNow());
        foreach (var rule in plan.ToApply) await _backend.ApplyRuleAsync(rule, ct).ConfigureAwait(false);
        foreach (var id in plan.ToRemove) await _backend.RemoveRuleAsync(id, ct).ConfigureAwait(false);
        _store.RemoveMany(plan.Expired);
        if (plan.ToApply.Count + plan.ToRemove.Count + plan.Expired.Count > 0) StateChanged?.Invoke();
    }

    public void Handle(ConnectionAttempt attempt)
    {
        var now = _time.GetUtcNow();
        var verdict = _gate.Check(attempt, _store.Rules, now);
        var entry = new ActivityEntry(attempt, verdict);
        PendingPrompt? raised = null;
        bool changed = false;

        lock (_lock)
        {
            _recent.AddFirst(entry);
            while (_recent.Count > _recentCapacity) _recent.RemoveLast();

            if (verdict == GateVerdict.Prompt)
            {
                raised = new PendingPrompt(Guid.NewGuid(), attempt, 0);
                _pending.Add(raised);
                changed = true;
            }
            else if (verdict == GateVerdict.AlreadyPending)
            {
                int i = _pending.FindIndex(p => p.Attempt.Direction == attempt.Direction && AppPaths.Same(p.Attempt.AppPath, attempt.AppPath));
                if (i >= 0) { _pending[i] = _pending[i] with { Repeats = _pending[i].Repeats + 1 }; changed = true; }
            }
        }

        ActivityAdded?.Invoke(entry);
        if (raised is not null) PromptRaised?.Invoke(raised);
        if (changed) StateChanged?.Invoke();
    }

    // false = keine offene Nachfrage mit dieser Id (schon beantwortet).
    public async Task<bool> DecideAsync(Guid promptId, PromptDecision decision, CancellationToken ct)
    {
        PendingPrompt? prompt;
        lock (_lock)
        {
            prompt = _pending.Find(p => p.Id == promptId);
            if (prompt is null) return false;
            _pending.Remove(prompt);
        }

        var now = _time.GetUtcNow();
        _gate.Resolved(prompt.Attempt, decision, now);
        try
        {
            var rule = DecisionMapper.ToRule(prompt.Attempt, decision, now);
            if (rule is not null)
            {
                // Speicher zuerst: scheitert das Backend, holt der nächste Abgleich die Regel nach.
                foreach (var old in _store.Add(rule)) await _backend.RemoveRuleAsync(old, ct).ConfigureAwait(false);
                await _backend.ApplyRuleAsync(rule, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            StateChanged?.Invoke();
        }
        return true;
    }

    public async Task<bool> DeleteRuleAsync(Guid ruleId, CancellationToken ct)
    {
        if (!_store.Remove(ruleId)) return false;
        try { await _backend.RemoveRuleAsync(ruleId, ct).ConfigureAwait(false); }
        finally { StateChanged?.Invoke(); }
        return true;
    }

    // Schutz gegen Aussperren: Ask sperrt ausgehend alles ohne Regel. Wer Ask einschaltet, muss es innerhalb von
    // ConfirmWindow bestätigen (ConfirmMode); sonst stellt CheckModeRevertAsync auf Monitor zurück. So bleibt kein
    // gesperrtes System übrig, wenn die Oberfläche hängt oder die Verbindung zum Dienst abreißt.
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(60);

    private DateTimeOffset? _revertAt;

    public DateTimeOffset? ModeRevertAt { get { lock (_lock) return _revertAt; } }

    public async Task SetModeAsync(ProtectionMode mode, CancellationToken ct)
    {
        lock (_lock) _revertAt = mode == ProtectionMode.Ask ? _time.GetUtcNow() + ConfirmWindow : null;
        await _backend.SetModeAsync(mode, ct).ConfigureAwait(false);
        // Erst nach der Bestätigung dauerhaft speichern: ein Neustart mitten im Zeitfenster landet wieder in Monitor.
        if (mode == ProtectionMode.Monitor) _store.SetMode(mode);
        StateChanged?.Invoke();
    }

    public bool ConfirmMode()
    {
        lock (_lock)
        {
            if (_revertAt is null) return false;
            _revertAt = null;
        }
        _store.SetMode(ProtectionMode.Ask);
        StateChanged?.Invoke();
        return true;
    }

    // Vom Dienst regelmäßig aufgerufen. true = zurückgestellt.
    public async Task<bool> CheckModeRevertAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_revertAt is not { } at || _time.GetUtcNow() < at) return false;
            _revertAt = null;
        }
        await _backend.SetModeAsync(ProtectionMode.Monitor, ct).ConfigureAwait(false);
        _store.SetMode(ProtectionMode.Monitor);
        StateChanged?.Invoke();
        return true;
    }

    public async Task<FirewallSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var status = await _backend.GetStatusAsync(ct).ConfigureAwait(false);
        lock (_lock)
            return new FirewallSnapshot(status, _store.Rules, _pending.ToList(), _recent.Take(200).ToList(), _revertAt);
    }
}
