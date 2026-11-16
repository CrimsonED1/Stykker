using System.Runtime.CompilerServices;
using StykkerFirewall.Core;
using StykkerFirewall.Core.Backend;
using StykkerFirewall.Core.Rules;
using static StykkerFirewall.Tests.TestData;

namespace StykkerFirewall.Tests;

// Backend, das nur mitschreibt. Belegt nebenbei, dass der Kern ohne echte Firewall auskommt.
internal sealed class RecordingBackend : IFirewallBackend
{
    public readonly Dictionary<Guid, FirewallRule> Rules = [];
    public readonly List<ProtectionMode> Modes = [];

    public string Id => "recording";
    public string DisplayName => "Recording";

    public Task<BackendStatus> GetStatusAsync(CancellationToken ct) =>
        Task.FromResult(new BackendStatus(Id, DisplayName, true, true, Modes.LastOrDefault(), true, null));

    public Task SetModeAsync(ProtectionMode mode, CancellationToken ct) { Modes.Add(mode); return Task.CompletedTask; }

    public Task<IReadOnlyCollection<Guid>> ListManagedRuleIdsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyCollection<Guid>>(Rules.Keys.ToList());

    public Task ApplyRuleAsync(FirewallRule rule, CancellationToken ct) { Rules[rule.Id] = rule; return Task.CompletedTask; }

    public Task RemoveRuleAsync(Guid ruleId, CancellationToken ct) { Rules.Remove(ruleId); return Task.CompletedTask; }

    public async IAsyncEnumerable<ConnectionAttempt> WatchBlockedAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class CoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stykker-fw-test-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingBackend _backend = new();
    private readonly FakeTime _time = new(Now);
    private readonly RuleStore _store;
    private readonly FirewallCoordinator _c;

    public CoordinatorTests()
    {
        _store = new RuleStore(Path.Combine(_dir, "rules.json"));
        _c = new FirewallCoordinator(_backend, _store, new PromptGate(TimeSpan.FromMinutes(2)), _time);
    }

    [Fact]
    public async Task Attempt_raises_one_prompt_and_decision_creates_rule_in_store_and_backend()
    {
        var raised = new List<Core.Ipc.PendingPrompt>();
        _c.PromptRaised += raised.Add;

        _c.Handle(Out());
        _c.Handle(Out());   // Wiederholung der App

        var prompt = Assert.Single(raised);
        var snap = await _c.GetSnapshotAsync(default);
        Assert.Equal(1, Assert.Single(snap.Pending).Repeats);

        Assert.True(await _c.DecideAsync(prompt.Id, PromptDecision.AllowApp, default));
        var rule = Assert.Single(_store.Rules);
        Assert.True(_backend.Rules.ContainsKey(rule.Id));
        Assert.Empty((await _c.GetSnapshotAsync(default)).Pending);

        // Danach deckt die Regel die App ab: keine neue Nachfrage
        _c.Handle(Out());
        Assert.Single(raised);
    }

    [Fact]
    public async Task Deciding_twice_is_harmless()
    {
        Core.Ipc.PendingPrompt? p = null;
        _c.PromptRaised += x => p = x;
        _c.Handle(Out());
        Assert.True(await _c.DecideAsync(p!.Id, PromptDecision.BlockOnce, default));
        Assert.False(await _c.DecideAsync(p.Id, PromptDecision.AllowApp, default));
        Assert.Empty(_store.Rules);
    }

    [Fact]
    public async Task Reconcile_restores_rule_deleted_in_backend()
    {
        var rule = DecisionMapper.ToRule(Out(), PromptDecision.AllowApp, Now)!;
        _store.Add(rule);
        await _c.ReconcileAsync(default);
        _backend.Rules.Clear();   // jemand löscht die Regel in der Windows-Firewall
        await _c.ReconcileAsync(default);
        Assert.True(_backend.Rules.ContainsKey(rule.Id));
    }

    [Fact]
    public async Task Unconfirmed_ask_mode_falls_back_to_monitor()
    {
        await _c.SetModeAsync(ProtectionMode.Ask, default);
        Assert.NotNull(_c.ModeRevertAt);
        Assert.Equal(ProtectionMode.Monitor, _store.Mode);   // noch nicht gespeichert

        _time.Value = Now + FirewallCoordinator.ConfirmWindow - TimeSpan.FromSeconds(1);
        Assert.False(await _c.CheckModeRevertAsync(default));

        _time.Value = Now + FirewallCoordinator.ConfirmWindow;
        Assert.True(await _c.CheckModeRevertAsync(default));
        Assert.Equal([ProtectionMode.Ask, ProtectionMode.Monitor], _backend.Modes);
        Assert.Equal(ProtectionMode.Monitor, _store.Mode);
        Assert.Null(_c.ModeRevertAt);
    }

    [Fact]
    public async Task Confirmed_ask_mode_stays_and_is_saved()
    {
        await _c.SetModeAsync(ProtectionMode.Ask, default);
        Assert.True(_c.ConfirmMode());
        _time.Value = Now.AddHours(1);
        Assert.False(await _c.CheckModeRevertAsync(default));
        Assert.Equal(ProtectionMode.Ask, _store.Mode);
        Assert.Equal([ProtectionMode.Ask], _backend.Modes);
    }

    [Fact]
    public async Task Delete_rule_removes_it_everywhere()
    {
        var rule = DecisionMapper.ToRule(Out(), PromptDecision.BlockAlways, Now)!;
        _store.Add(rule);
        await _c.ReconcileAsync(default);
        Assert.True(await _c.DeleteRuleAsync(rule.Id, default));
        Assert.Empty(_store.Rules);
        Assert.Empty(_backend.Rules);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
