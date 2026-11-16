using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using StykkerFirewall.Core;
using StykkerFirewall.Core.Backend;
using StykkerFirewall.Core.Rules;

namespace StykkerFirewall.Backend.Simulator;

// Erfundene Apps versuchen in Abständen Verbindungen. Blockiert wird wie bei einer echten Firewall: eine passende
// Block-Regel gewinnt, sonst eine Allow-Regel; ohne Regel entscheidet der Modus (Ask: ausgehend gesperrt).
// Adressen nur aus Dokumentationsbereichen (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24).
public sealed class SimulatedBackend : IFirewallBackend
{
    public const string BackendId = "simulator";

    private static readonly (string Path, TrafficDirection Dir, int Proto, string Remote, int Port)[] Apps =
    [
        (@"C:\Program Files\Example Browser\browser.exe", TrafficDirection.Outbound, IpProtocol.Tcp, "192.0.2.10", 443),
        (@"C:\Program Files\Example Browser\browser.exe", TrafficDirection.Outbound, IpProtocol.Udp, "192.0.2.53", 53),
        (@"C:\Program Files\Chat Example\chat.exe", TrafficDirection.Outbound, IpProtocol.Tcp, "198.51.100.7", 5222),
        (@"C:\Program Files\Updater Example\update.exe", TrafficDirection.Outbound, IpProtocol.Tcp, "203.0.113.20", 443),
        (@"C:\Tools\GameServer\server.exe", TrafficDirection.Inbound, IpProtocol.Udp, "198.51.100.44", 27015),
        (@"C:\Users\Public\Downloads\unknown-tool.exe", TrafficDirection.Outbound, IpProtocol.Tcp, "203.0.113.99", 8080),
    ];

    private readonly ConcurrentDictionary<Guid, FirewallRule> _rules = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly Random _random;
    private ProtectionMode _mode = ProtectionMode.Monitor;

    public SimulatedBackend(TimeProvider? time = null, TimeSpan? interval = null, int seed = 1)
    {
        _time = time ?? TimeProvider.System;
        _interval = interval ?? TimeSpan.FromSeconds(4);
        _random = new Random(seed);
    }

    public string Id => BackendId;

    public string DisplayName => "Simulator";

    public Task<BackendStatus> GetStatusAsync(CancellationToken ct) =>
        Task.FromResult(new BackendStatus(Id, DisplayName, Available: true, FirewallEnabled: true, _mode, AuditEnabled: true,
            Problem: "Simulated backend: no real traffic is filtered."));

    public Task SetModeAsync(ProtectionMode mode, CancellationToken ct)
    {
        _mode = mode;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<Guid>> ListManagedRuleIdsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyCollection<Guid>>(_rules.Keys.ToArray());

    public Task ApplyRuleAsync(FirewallRule rule, CancellationToken ct)
    {
        _rules[rule.Id] = rule;
        return Task.CompletedTask;
    }

    public Task RemoveRuleAsync(Guid ruleId, CancellationToken ct)
    {
        _rules.TryRemove(ruleId, out _);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ConnectionAttempt> WatchBlockedAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_interval, _time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }

            var app = Apps[_random.Next(Apps.Length)];
            var attempt = new ConnectionAttempt(Guid.NewGuid(), _time.GetUtcNow(), app.Path, 1000 + _random.Next(9000), app.Dir,
                app.Proto, "192.0.2.2", app.Dir == TrafficDirection.Inbound ? app.Port : 49152 + _random.Next(16000),
                app.Remote, app.Dir == TrafficDirection.Inbound ? 49152 + _random.Next(16000) : app.Port);

            if (IsBlocked(attempt)) yield return attempt;
        }
    }

    private bool IsBlocked(ConnectionAttempt attempt)
    {
        var verdict = RuleMatcher.Evaluate(_rules.Values, attempt, _time.GetUtcNow());
        if (verdict is { } v) return v == RuleAction.Block;
        // ohne Regel: eingehend sperrt jede Windows-Firewall, ausgehend nur im Modus Ask
        return attempt.Direction == TrafficDirection.Inbound || _mode == ProtectionMode.Ask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
