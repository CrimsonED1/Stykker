using StykkerFirewall.Core;
using StykkerFirewall.Core.Rules;
using static StykkerFirewall.Tests.TestData;

namespace StykkerFirewall.Tests;

public class DecisionMapperTests
{
    [Fact]
    public void AllowApp_gives_app_wide_allow()
    {
        var rule = DecisionMapper.ToRule(Out(), PromptDecision.AllowApp, Now)!;
        Assert.Equal(RuleAction.Allow, rule.Action);
        Assert.True(rule.IsAppWide);
        Assert.Equal(Browser, rule.AppPath);
        Assert.Equal(TrafficDirection.Outbound, rule.Direction);
    }

    [Fact]
    public void BlockAlways_gives_app_wide_block()
    {
        var rule = DecisionMapper.ToRule(Out(), PromptDecision.BlockAlways, Now)!;
        Assert.Equal(RuleAction.Block, rule.Action);
        Assert.True(rule.IsAppWide);
    }

    [Fact]
    public void BlockOnce_gives_no_rule() => Assert.Null(DecisionMapper.ToRule(Out(), PromptDecision.BlockOnce, Now));

    [Fact]
    public void AllowTarget_outbound_uses_remote_address_and_port()
    {
        var rule = DecisionMapper.ToRule(Out(remote: "192.0.2.77", port: 8443), PromptDecision.AllowTarget, Now)!;
        Assert.Equal("192.0.2.77", rule.RemoteAddress);
        Assert.Equal(8443, rule.RemotePort);
        Assert.Null(rule.LocalPort);
        Assert.Equal(IpProtocol.Tcp, rule.Protocol);
    }

    [Fact]
    public void AllowTarget_inbound_uses_local_port_not_the_changing_remote_port()
    {
        var rule = DecisionMapper.ToRule(In(localPort: 27015), PromptDecision.AllowTarget, Now)!;
        Assert.Equal(27015, rule.LocalPort);
        Assert.Null(rule.RemotePort);
        Assert.Equal("198.51.100.4", rule.RemoteAddress);
    }

    [Fact]
    public void AllowTarget_without_ports_for_icmp()
    {
        var rule = DecisionMapper.ToRule(Out(proto: IpProtocol.Icmp, port: 0), PromptDecision.AllowTarget, Now)!;
        Assert.Null(rule.RemotePort);
        Assert.Equal(IpProtocol.Icmp, rule.Protocol);
    }
}

public class RuleMatcherTests
{
    [Fact]
    public void Block_wins_over_allow()
    {
        var a = Out();
        var rules = new[]
        {
            DecisionMapper.ToRule(a, PromptDecision.AllowApp, Now)!,
            DecisionMapper.ToRule(a, PromptDecision.BlockAlways, Now)!,
        };
        Assert.Equal(RuleAction.Block, RuleMatcher.Evaluate(rules, a, Now));
    }

    [Fact]
    public void Target_rule_does_not_cover_other_targets()
    {
        var rule = DecisionMapper.ToRule(Out(remote: "192.0.2.10"), PromptDecision.AllowTarget, Now)!;
        Assert.True(RuleMatcher.Matches(rule, Out(remote: "192.0.2.10")));
        Assert.False(RuleMatcher.Matches(rule, Out(remote: "192.0.2.11")));
        Assert.False(RuleMatcher.Matches(rule, Out(remote: "192.0.2.10", port: 80)));
    }

    [Fact]
    public void Path_compare_ignores_case_and_direction_matters()
    {
        var rule = DecisionMapper.ToRule(Out(), PromptDecision.AllowApp, Now)!;
        Assert.True(RuleMatcher.Matches(rule, Out(app: Browser.ToUpperInvariant())));
        Assert.False(RuleMatcher.Matches(rule, In()));
    }

    [Fact]
    public void Expired_rule_is_ignored()
    {
        var rule = DecisionMapper.ToRule(Out(), PromptDecision.AllowApp, Now)! with { Expires = Now.AddMinutes(-1) };
        Assert.Null(RuleMatcher.Evaluate([rule], Out(), Now));
    }
}

public class ReconcilerTests
{
    [Fact]
    public void Applies_missing_removes_orphans_and_drops_expired()
    {
        var keep = DecisionMapper.ToRule(Out(), PromptDecision.AllowApp, Now)!;
        var missing = DecisionMapper.ToRule(In(), PromptDecision.AllowApp, Now)!;
        var expired = DecisionMapper.ToRule(Out(app: @"C:\x.exe"), PromptDecision.AllowApp, Now)! with { Expires = Now.AddSeconds(-1) };
        var orphan = Guid.NewGuid();

        var plan = Reconciler.Plan([keep, missing, expired], [keep.Id, expired.Id, orphan], Now);

        Assert.Equal([missing.Id], plan.ToApply.Select(r => r.Id));
        Assert.Equal(new[] { expired.Id, orphan }.Order(), plan.ToRemove.Order());
        Assert.Equal([expired.Id], plan.Expired);
    }
}

public class RuleStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stykker-fw-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Survives_reload_and_replaces_same_scope()
    {
        var path = Path.Combine(_dir, "rules.json");
        var store = new RuleStore(path);
        var block = DecisionMapper.ToRule(Out(), PromptDecision.BlockAlways, Now)!;
        store.Add(block);
        var allow = DecisionMapper.ToRule(Out(), PromptDecision.AllowApp, Now)!;
        var replaced = store.Add(allow);
        store.SetMode(ProtectionMode.Ask);

        Assert.Equal([block.Id], replaced);

        var again = new RuleStore(path);
        Assert.Equal(ProtectionMode.Ask, again.Mode);
        var only = Assert.Single(again.Rules);
        Assert.Equal(allow, only);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

public class PromptGateTests
{
    private readonly PromptGate _gate = new(TimeSpan.FromMinutes(2));

    [Fact]
    public void Second_attempt_while_pending_is_not_prompted()
    {
        Assert.Equal(GateVerdict.Prompt, _gate.Check(Out(), [], Now));
        Assert.Equal(GateVerdict.AlreadyPending, _gate.Check(Out(port: 80), [], Now));
        Assert.Equal(GateVerdict.Prompt, _gate.Check(In(), [], Now));   // andere Richtung = eigene Nachfrage
    }

    [Fact]
    public void Block_once_is_quiet_for_a_while_then_asks_again()
    {
        var a = Out();
        _gate.Check(a, [], Now);
        _gate.Resolved(a, PromptDecision.BlockOnce, Now);

        Assert.Equal(GateVerdict.QuietAfterBlock, _gate.Check(Out(), [], Now.AddSeconds(30)));
        Assert.Equal(GateVerdict.Prompt, _gate.Check(Out(), [], Now.AddMinutes(3)));
    }

    [Fact]
    public void Attempt_covered_by_own_rule_is_not_prompted()
    {
        var block = DecisionMapper.ToRule(Out(), PromptDecision.BlockAlways, Now)!;
        Assert.Equal(GateVerdict.CoveredByRule, _gate.Check(Out(), [block], Now));
    }

    [Fact]
    public void System_and_empty_path_are_not_prompted()
    {
        Assert.Equal(GateVerdict.NotPromptable, _gate.Check(Out(app: AppPaths.System), [], Now));
        Assert.Equal(GateVerdict.NotPromptable, _gate.Check(Out(app: ""), [], Now));
    }
}
