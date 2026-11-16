using StykkerFirewall.Backend.WindowsFirewall;
using StykkerFirewall.Core;

namespace StykkerFirewall.Service;

// Für den Fall, dass man sich ausgesperrt hat: setzt die Windows-Firewall auf ihren Standard (ausgehend erlaubt,
// Windows-Hinweise an) und löscht alle Regeln der Gruppe "StykkerFirewall". Die eigene rules.json bleibt, steht aber
// auf Monitor, damit der nächste Dienststart nicht wieder sperrt.
public static class EmergencyReset
{
    public static async Task<int> RunAsync()
    {
        if (!WindowsFirewallBackend.IsAdministrator())
        {
            Console.Error.WriteLine("Run this from an administrator prompt.");
            return 2;
        }

        await using var backend = new WindowsFirewallBackend();
        await backend.SetModeAsync(ProtectionMode.Monitor, CancellationToken.None);
        Console.WriteLine("Outbound connections are allowed again (Windows default).");

        var ids = await backend.ListManagedRuleIdsAsync(CancellationToken.None);
        foreach (var id in ids) await backend.RemoveRuleAsync(id, CancellationToken.None);
        Console.WriteLine($"Removed {ids.Count} StykkerFirewall rule(s).");

        var store = new Core.Rules.RuleStore(Path.Combine(new ServiceOptions().ResolveDataDir(), "rules.json"));
        store.SetMode(ProtectionMode.Monitor);
        return 0;
    }
}
