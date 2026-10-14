using Stykker.Shared.Config;

namespace Stykker.Shared.Tests;

// Die Regel für das Ziel einer Weiterleitung: nur dieser PC. Die Adressen und Namen sind Platzhalter aus den
// Dokumentationsnetzen (192.0.2.0/24, example.*), die Regel selbst macht keinen Netzwerkzugriff.
public class ServiceTargetRuleTests
{
    private static readonly string[] ThisPc = ["192.0.2.10", "100.64.0.7"];
    private static readonly string[] Names = ["pc.example.ts.net"];

    [Theory]
    [InlineData("http://127.0.0.1:41999", "http://127.0.0.1:41999")]
    [InlineData("http://127.0.0.1:41999/", "http://127.0.0.1:41999")]
    [InlineData("  http://127.0.0.1:41999  ", "http://127.0.0.1:41999")]
    [InlineData("http://localhost:41999", "http://localhost:41999")]
    [InlineData("HTTP://LocalHost:41999", "http://localhost:41999")]
    [InlineData("https://127.0.0.1:41999", "https://127.0.0.1:41999")]
    [InlineData("https://localhost:443", "https://localhost:443")]
    public void Loopback_is_allowed_over_http_and_https(string target, string expected) =>
        Assert.Equal(expected, ServiceTargetRule.Allowed(target, ThisPc, Names));

    [Theory]
    [InlineData("http://192.0.2.10:16001", "http://192.0.2.10:16001")]
    [InlineData("https://192.0.2.10:16001/", "https://192.0.2.10:16001")]
    [InlineData("http://100.64.0.7:16001", "http://100.64.0.7:16001")]
    public void An_address_of_this_pc_is_allowed(string target, string expected) =>
        Assert.Equal(expected, ServiceTargetRule.Allowed(target, ThisPc, Names));

    [Theory]
    [InlineData("https://pc.example.ts.net:16001", "https://pc.example.ts.net:16001")]
    [InlineData("https://PC.example.ts.net:16001/", "https://pc.example.ts.net:16001")]
    public void A_name_from_hostnames_is_allowed(string target, string expected) =>
        Assert.Equal(expected, ServiceTargetRule.Allowed(target, ThisPc, Names));

    [Theory]
    [InlineData("http://192.0.2.99:16001")]          // ein fremder Rechner
    [InlineData("https://192.0.2.99:16001")]
    [InlineData("http://192.0.2.100:16001")]
    [InlineData("http://192.0.2.1:16001")]
    [InlineData("http://127.0.0.2:16001")]           // Loopback, aber nicht die festgelegte Adresse
    [InlineData("http://0.0.0.0:16001")]
    [InlineData("http://192.0.2.010:16001")]         // nicht in kanonischer Form
    [InlineData("http://192.0.2.10.nip.io:16001")]   // ein Name, der nur so aussieht
    [InlineData("http://pc.example.ts.net.evil.example:16001")]
    [InlineData("http://pc.evil.example:16001")]     // der Rechnername mit fremder Domain
    [InlineData("http://[::1]:16001")]               // IPv6 gehört nicht zur Regel
    [InlineData("http://[2001:db8::10]:16001")]
    public void Any_other_machine_is_refused(string target) =>
        Assert.Null(ServiceTargetRule.Allowed(target, ThisPc, Names));

    [Theory]
    [InlineData("http://127.0.0.1")]                 // ohne Port
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://127.0.0.1:70000")]
    [InlineData("http://127.0.0.1:41999/path")]
    [InlineData("http://127.0.0.1:41999?x=1")]
    [InlineData("http://127.0.0.1:41999#frag")]
    [InlineData("http://user@127.0.0.1:41999")]
    [InlineData("ftp://127.0.0.1:41999")]
    [InlineData("javascript:alert(1)")]
    [InlineData("127.0.0.1:41999")]
    [InlineData("")]
    [InlineData("   ")]
    public void Malformed_targets_are_refused(string target) =>
        Assert.Null(ServiceTargetRule.Allowed(target, ThisPc, Names));

    [Fact]
    public void A_missing_target_is_refused() =>
        Assert.Null(ServiceTargetRule.Allowed(null, ThisPc, Names));

    [Fact]
    public void Without_addresses_or_names_only_loopback_is_allowed()
    {
        Assert.Null(ServiceTargetRule.Allowed("http://192.0.2.10:16001", [], []));
        Assert.Null(ServiceTargetRule.Allowed("https://pc.example.ts.net:16001", [], []));
        Assert.Equal("http://127.0.0.1:16001", ServiceTargetRule.Allowed("http://127.0.0.1:16001", [], []));
    }

    [Fact]
    public void A_placeholder_name_in_hostnames_matches_nothing()
    {
        Assert.Null(ServiceTargetRule.Allowed("https://anything.example.ts.net:16001", [], ["*.example.ts.net"]));
        Assert.Null(ServiceTargetRule.Allowed("https://anything.example.ts.net:16001", [], ["192.0.2.10"]));
    }

    [Fact]
    public void Reading_this_pcs_addresses_gives_only_canonical_ipv4_and_no_loopback()
    {
        var addresses = ServiceTargetRule.ReadThisPcAddresses();

        Assert.All(addresses, a => Assert.Equal(a, System.Net.IPAddress.Parse(a).ToString()));
        Assert.DoesNotContain("127.0.0.1", addresses);
    }
}
