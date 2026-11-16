using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Channels;
using StykkerFirewall.Core;
using StykkerFirewall.Core.Backend;

namespace StykkerFirewall.Backend.WindowsFirewall;

// Setzt StykkerFirewall auf die eingebaute Windows-Firewall:
// - Regeln über HNetCfg.FwPolicy2 / HNetCfg.FWRule (COM, spät gebunden über IDispatch – kein Interop-Paket nötig).
//   Eigene Regeln tragen die Gruppe "StykkerFirewall" und in der Beschreibung "stykker-id:<guid>".
// - Ask: ausgehend standardmäßig sperren (alle drei Profile) und den Windows-Hinweis für eingehende Verbindungen
//   abschalten, damit nicht zwei Dialoge kommen. Monitor stellt beides auf den Windows-Standard zurück.
// - Blockierte Versuche: Security-Log, Ereignis 5157.
public sealed class WindowsFirewallBackend : IFirewallBackend
{
    public const string BackendId = "windows-firewall";
    public const string RuleGroup = "StykkerFirewall";
    private const string IdPrefix = "stykker-id:";

    // NET_FW_PROFILE2_*, NET_FW_RULE_DIRECTION_*, NET_FW_ACTION_*
    private static readonly int[] Profiles = [1, 2, 4];
    private const int ProfileAll = 0x7FFFFFFF;
    private const int DirIn = 1, DirOut = 2;
    private const int ActionBlock = 0, ActionAllow = 1;
    private const int ProtocolAny = 256;

    private readonly DevicePaths _devicePaths;
    private readonly SemaphoreSlim _comLock = new(1, 1);

    public WindowsFirewallBackend(DevicePaths? devicePaths = null) => _devicePaths = devicePaths ?? new DevicePaths();

    public string Id => BackendId;

    public string DisplayName => "Windows Firewall";

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public async Task<BackendStatus> GetStatusAsync(CancellationToken ct)
    {
        await _comLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            object policy;
            try { policy = CreatePolicy(); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                return new BackendStatus(Id, DisplayName, false, false, ProtectionMode.Monitor, false, "Windows Firewall is not reachable: " + ex.Message);
            }

            int current = Convert.ToInt32(Get(policy, "CurrentProfileTypes"), CultureInfo.InvariantCulture);
            var active = Profiles.Where(p => (current & p) != 0).DefaultIfEmpty(Profiles[^1]).ToArray();
            bool enabled = active.All(p => Convert.ToBoolean(Get(policy, "FirewallEnabled", p), CultureInfo.InvariantCulture));
            bool outBlocked = active.All(p => Convert.ToInt32(Get(policy, "DefaultOutboundAction", p), CultureInfo.InvariantCulture) == ActionBlock);
            bool admin = IsAdministrator();
            bool audit = false;
            string? problem = null;

            if (admin)
            {
                try { audit = AuditPolicy.IsFailureAuditingEnabled(); }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException) { problem = "Audit policy could not be read: " + ex.Message; }
            }

            if (!admin) problem = "The service is not running with administrator rights; rules and blocked connections are not available.";
            else if (!enabled) problem = "Windows Firewall is off for the current network. Another firewall may have taken over.";
            else if (!audit) problem = "Auditing of blocked connections is off; no prompts can appear.";

            return new BackendStatus(Id, DisplayName, true, enabled, outBlocked ? ProtectionMode.Ask : ProtectionMode.Monitor, audit, problem);
        }
        finally
        {
            _comLock.Release();
        }
    }

    public async Task SetModeAsync(ProtectionMode mode, CancellationToken ct)
    {
        await _comLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var policy = CreatePolicy();
            foreach (var p in Profiles)
            {
                Set(policy, "DefaultOutboundAction", p, mode == ProtectionMode.Ask ? ActionBlock : ActionAllow);
                Set(policy, "NotificationsDisabled", p, mode == ProtectionMode.Ask);
            }
        }
        finally
        {
            _comLock.Release();
        }
        if (mode == ProtectionMode.Ask) AuditPolicy.EnableFailureAuditing();
    }

    public async Task<IReadOnlyCollection<Guid>> ListManagedRuleIdsAsync(CancellationToken ct)
    {
        await _comLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ids = new HashSet<Guid>();
            foreach (var rule in ManagedRules(CreatePolicy()))
                if (ParseId(rule.Description) is { } id) ids.Add(id);
            return ids;
        }
        finally
        {
            _comLock.Release();
        }
    }

    public async Task ApplyRuleAsync(FirewallRule rule, CancellationToken ct)
    {
        await _comLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var policy = CreatePolicy();
            RemoveById(policy, rule.Id);

            var type = Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!;
            dynamic com = Activator.CreateInstance(type)!;
            com.Name = RuleName(rule);
            com.Description = IdPrefix + rule.Id.ToString("D");
            com.Grouping = RuleGroup;
            com.ApplicationName = rule.AppPath;
            com.Direction = rule.Direction == TrafficDirection.Inbound ? DirIn : DirOut;
            com.Action = rule.Action == RuleAction.Allow ? ActionAllow : ActionBlock;
            com.Protocol = rule.Protocol ?? ProtocolAny;   // vor den Ports setzen, sonst lehnt die API Ports ab
            if (IpProtocol.HasPorts(rule.Protocol))
            {
                if (rule.RemotePort is { } rp) com.RemotePorts = rp.ToString(CultureInfo.InvariantCulture);
                if (rule.LocalPort is { } lp) com.LocalPorts = lp.ToString(CultureInfo.InvariantCulture);
            }
            if (rule.RemoteAddress is { } ra) com.RemoteAddresses = ra;
            com.Profiles = ProfileAll;
            com.Enabled = true;

            dynamic rules = Get(policy, "Rules");
            rules.Add(com);
        }
        finally
        {
            _comLock.Release();
        }
    }

    public async Task RemoveRuleAsync(Guid ruleId, CancellationToken ct)
    {
        await _comLock.WaitAsync(ct).ConfigureAwait(false);
        try { RemoveById(CreatePolicy(), ruleId); }
        finally { _comLock.Release(); }
    }

    public async IAsyncEnumerable<ConnectionAttempt> WatchBlockedAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Gedrosselt: bei einem Sturm von Blockierungen gehen die ältesten verloren, nicht der Speicher.
        var channel = Channel.CreateBounded<ConnectionAttempt>(new BoundedChannelOptions(5000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        var query = new EventLogQuery("Security", PathType.LogName, $"*[System[(EventID={Audit5157Parser.EventId})]]");
        using var watcher = new EventLogWatcher(query);
        watcher.EventRecordWritten += (_, e) =>
        {
            using var record = e.EventRecord;
            if (record is null) return;
            var attempt = Audit5157Parser.Parse(record.ToXml(), _devicePaths.Resolve);
            if (attempt is not null) channel.Writer.TryWrite(attempt);
        };
        watcher.Enabled = true;
        try
        {
            await foreach (var attempt in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return attempt;
        }
        finally
        {
            watcher.Enabled = false;
        }
    }

    public static string RuleName(FirewallRule rule) =>
        $"Stykker: {rule.AppName} ({(rule.Action == RuleAction.Allow ? "allow" : "block")}, {(rule.Direction == TrafficDirection.Inbound ? "in" : "out")}) [{rule.Id.ToString("N")[..8]}]";

    public static Guid? ParseId(string? description) =>
        description is not null && description.StartsWith(IdPrefix, StringComparison.Ordinal) &&
        Guid.TryParse(description.AsSpan(IdPrefix.Length).Trim(), out var id) ? id : null;

    private static void RemoveById(object policy, Guid id)
    {
        dynamic rules = Get(policy, "Rules");
        foreach (var rule in ManagedRules(policy).Where(r => ParseId(r.Description) == id).ToList())
            rules.Remove(rule.Name);
    }

    private sealed record ComRule(string Name, string? Description);

    private static List<ComRule> ManagedRules(object policy)
    {
        var result = new List<ComRule>();
        dynamic rules = Get(policy, "Rules");
        foreach (dynamic rule in rules)
        {
            string? group = rule.Grouping;
            if (!string.Equals(group, RuleGroup, StringComparison.Ordinal)) continue;
            result.Add(new ComRule((string)rule.Name, (string?)rule.Description));
        }
        return result;
    }

    private static object CreatePolicy()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: false)
            ?? throw new InvalidOperationException("HNetCfg.FwPolicy2 is not registered");
        return Activator.CreateInstance(type) ?? throw new InvalidOperationException("HNetCfg.FwPolicy2 could not be created");
    }

    // Indizierte COM-Eigenschaften (FirewallEnabled[profile] usw.) über IDispatch.
    private static object Get(object com, string name, params object[] args) =>
        com.GetType().InvokeMember(name, BindingFlags.GetProperty, null, com, args, CultureInfo.InvariantCulture)!;

    private static void Set(object com, string name, int index, object value) =>
        com.GetType().InvokeMember(name, BindingFlags.SetProperty, null, com, [index, value], CultureInfo.InvariantCulture);

    public ValueTask DisposeAsync()
    {
        _comLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
