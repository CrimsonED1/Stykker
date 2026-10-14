using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Die Host-Prüfung von StykkerLLM (Auftrag Host-Regel): fremde Namen werden vor allem anderen abgewiesen, Zusatznamen
// gelten nur bei eingeschaltetem Netz, und die zwei Wege der Model-Hosts sind ausgenommen. Die Namen sind Platzhalter.
[TestClass]
public class N67_HostNameRuleTests
{
    private const string Machine = "testhost", Domain = "example.lan", Extra = "pc.example.ts.net";
    private static readonly HostNameRule WithExtra = new(Machine, Domain, new[] { Extra });
    private static readonly HostNameRule WithoutExtra = new(Machine, Domain, Array.Empty<string>());
    private const string Evil = "evil.example:8093";

    // ── Fremde Namen: auf keinem Weg durch, auch nicht bei eingeschaltetem Netz ──

    [TestMethod]
    public void Foreign_host_is_refused_on_pair_ping_state_and_metrics_with_remote_on_and_off()
    {
        foreach (var remote in new[] { true, false })
        {
            Assert.IsFalse(WithExtra.Allowed("/pair", "GET", Evil, remote), "/pair " + remote);
            Assert.IsFalse(WithExtra.Allowed("/pair/request", "POST", Evil, remote), "/pair/request " + remote);
            Assert.IsFalse(WithExtra.Allowed("/api/ping", "GET", Evil, remote), "/api/ping " + remote);
            Assert.IsFalse(WithExtra.Allowed("/api/state", "GET", Evil, remote), "/api/state " + remote);
            Assert.IsFalse(WithExtra.Allowed("/api/metrics", "GET", Evil, remote), "/api/metrics " + remote);
        }
    }

    [TestMethod]
    public void Foreign_host_is_refused_on_hosts_paths_other_than_the_two_model_host_ways()
    {
        Assert.IsFalse(WithExtra.Allowed("/hosts/pair", "GET", Evil, true));
        Assert.IsFalse(WithExtra.Allowed("/hosts/other", "POST", Evil, true));
        Assert.IsFalse(WithExtra.Allowed("/hosts/other", "POST", Evil, false));
    }

    [TestMethod]
    public void DNS_rebinding_forms_of_this_pcs_name_are_refused()
    {
        foreach (var remote in new[] { true, false })
        {
            Assert.IsFalse(WithExtra.Allowed("/pair", "GET", "testhost.evil.example:8093", remote));
            Assert.IsFalse(WithExtra.Allowed("/pair", "GET", "pc.example.ts.net.evil.example:8093", remote));
        }
    }

    // ── Die zwei Wege der Model-Hosts: ein fremder Name geht an der Host-Prüfung vorbei (Token bzw. Code prüft der Endpunkt) ──

    [TestMethod]
    public void Model_host_websocket_and_pairing_pass_a_foreign_host_with_remote_on_and_off()
    {
        foreach (var remote in new[] { true, false })
        {
            Assert.IsTrue(WithExtra.Allowed("/hosts/connect", "GET", Evil, remote), "connect " + remote);
            Assert.IsTrue(WithoutExtra.Allowed("/hosts/connect", "GET", Evil, remote), "connect without extra " + remote);
            Assert.IsTrue(WithExtra.Allowed("/hosts/pair", "POST", Evil, remote), "pair " + remote);
        }
    }

    [TestMethod]
    public void Model_host_exemption_is_only_for_the_exact_paths_and_for_post_on_pair()
    {
        Assert.IsTrue(HostNameRule.IsModelHostPath("/hosts/connect", "GET"));
        Assert.IsTrue(HostNameRule.IsModelHostPath("/HOSTS/CONNECT", "GET"));
        Assert.IsTrue(HostNameRule.IsModelHostPath("/hosts/pair", "POST"));
        Assert.IsFalse(HostNameRule.IsModelHostPath("/hosts/pair", "GET"));
        Assert.IsFalse(HostNameRule.IsModelHostPath("/hosts/connect/more", "GET"));
        Assert.IsFalse(HostNameRule.IsModelHostPath("/pair", "POST"));
    }

    // ── Netz aus: nur die Loopback-Namen, Zusatznamen nicht ──

    [TestMethod]
    public void With_remote_off_only_the_loopback_names_pass()
    {
        Assert.IsTrue(WithExtra.Allowed("/api/state", "GET", "127.0.0.1:8093", false));
        Assert.IsTrue(WithExtra.Allowed("/api/state", "GET", "localhost:8093", false));
        Assert.IsTrue(WithExtra.Allowed("/api/state", "GET", "localhost", false));
        Assert.IsTrue(WithExtra.Allowed("/api/state", "GET", "[::1]", false));
        Assert.IsFalse(WithExtra.Allowed("/api/state", "GET", Machine + ":8093", false));
        Assert.IsFalse(WithExtra.Allowed("/api/state", "GET", Machine + "." + Domain + ":8093", false));
        Assert.IsFalse(WithExtra.Allowed("/api/state", "GET", "192.0.2.10:8093", false));
    }

    [TestMethod]
    public void With_remote_off_an_extra_name_is_refused_but_with_remote_on_it_passes()
    {
        Assert.IsFalse(WithExtra.Allowed("/api/state", "GET", Extra + ":8093", false));
        Assert.IsTrue(WithExtra.Allowed("/api/state", "GET", Extra + ":8093", true));
    }

    [TestMethod]
    public void An_empty_host_header_passes_only_with_remote_off()
    {
        Assert.IsTrue(WithExtra.Allowed("/api/ping", "GET", null, false));
        Assert.IsTrue(WithExtra.Allowed("/api/ping", "GET", "", false));
        Assert.IsFalse(WithExtra.Allowed("/api/ping", "GET", null, true));
    }

    // ── Netz an: Rechnername, DNS-Endung, IP und Zusatznamen ──

    [TestMethod]
    public void With_remote_on_the_pcs_names_an_address_and_the_extra_name_pass()
    {
        Assert.IsTrue(WithExtra.Allowed("/api/state", "GET", Machine + ":8093", true));
        Assert.IsTrue(WithExtra.Allowed("/api/state", "GET", Machine + "." + Domain + ":8093", true));
        Assert.IsTrue(WithExtra.Allowed("/api/state", "GET", "192.0.2.10:8093", true));
        Assert.IsTrue(WithExtra.Allowed("/api/state", "GET", "127.0.0.1:8093", true));
        Assert.IsTrue(WithExtra.Allowed("/api/state", "GET", Extra + ":8093", true));
    }

    [TestMethod]
    public void Without_the_extra_names_the_extra_name_is_refused_with_remote_on()
    {
        Assert.IsFalse(WithoutExtra.Allowed("/api/state", "GET", Extra + ":8093", true));
    }

    [TestMethod]
    public void For_this_pc_the_rule_reads_the_machine_name_and_takes_no_extra_names_when_none_are_given()
    {
        var rule = HostNameRule.ForThisPc(Array.Empty<string>());

        Assert.AreEqual(Environment.MachineName, rule.MachineName);
        Assert.AreEqual(0, rule.ExtraNames.Count);
    }
}
