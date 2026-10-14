using System.Net;
using Stykker.Shared.Web;

namespace Stykker.Shared.Tests;

public class PairingGateTests
{
    private const string Admin = "admin", Viewer = "viewer";
    // Der Name dieses PCs in den Tests (ein Platzhalter) und seine DNS-Endung
    private const string Machine = "testhost", Domain = "example.lan";

    [Theory]
    [InlineData("/")]
    [InlineData("/hub")]
    [InlineData("/api/snapshot")]
    [InlineData("/api/process/end")]
    public void This_machine_gets_in_without_pairing(string path) =>
        Assert.Equal(GateAction.Allow, PairingGate.Decide(loopback: true, remoteEnabled: false, path, wantsPage: true, role: Admin));

    [Theory]
    [InlineData("/")]
    [InlineData("/api/snapshot")]
    [InlineData("/api/ping")]
    [InlineData("/pair")]
    [InlineData("/manifest.webmanifest")]
    public void With_the_switch_off_nothing_answers_the_network(string path) =>
        Assert.Equal(GateAction.Forbidden, PairingGate.Decide(false, remoteEnabled: false, path, true, role: null));

    [Fact]
    public void A_paired_device_gets_in_whatever_its_role()
    {
        Assert.Equal(GateAction.Allow, PairingGate.Decide(false, true, "/hub", true, Viewer));
        Assert.Equal(GateAction.Allow, PairingGate.Decide(false, true, "/api/snapshot", false, Admin));
    }

    [Fact]
    public void An_unpaired_device_is_sent_to_the_sign_in_page_or_gets_401()
    {
        Assert.Equal(GateAction.RedirectToPair, PairingGate.Decide(false, true, "/hub", wantsPage: true, role: null));
        Assert.Equal(GateAction.Unauthorized, PairingGate.Decide(false, true, "/api/snapshot", wantsPage: false, role: null));
        Assert.Equal(GateAction.Unauthorized, PairingGate.Decide(false, true, "/api/process/end", wantsPage: false, role: null));
    }

    [Theory]
    [InlineData("/pair")]
    [InlineData("/PAIR")]
    [InlineData("/pair/request")]
    public void The_sign_in_itself_is_reachable_without_a_device(string path) =>
        Assert.Equal(GateAction.Pair, PairingGate.Decide(false, true, path, true, null));

    [Theory]
    [InlineData("/api/ping")]
    [InlineData("/manifest.webmanifest")]
    [InlineData("/hub-icon-192.png")]
    public void What_a_browser_fetches_without_cookies_is_open(string path) =>
        Assert.Equal(GateAction.Allow, PairingGate.Decide(false, true, path, false, null));

    [Theory]
    [InlineData("/pairing")]          // kein /pair: bleibt hinter der Prüfung
    [InlineData("/api/hub/access")]
    [InlineData("/ds/tokens.css")]
    [InlineData("/hub.js")]
    public void Everything_else_stays_behind_the_gate(string path) =>
        Assert.NotEqual(GateAction.Allow, PairingGate.Decide(false, true, path, false, null));

    [Fact]
    public void Loopback_is_recognised_in_both_address_families()
    {
        Assert.True(PairingGate.IsLoopback(IPAddress.Loopback));
        Assert.True(PairingGate.IsLoopback(IPAddress.IPv6Loopback));
        Assert.True(PairingGate.IsLoopback(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(PairingGate.IsLoopback(IPAddress.Parse("192.168.0.23")));
        Assert.False(PairingGate.IsLoopback(IPAddress.Parse("::ffff:192.168.0.23")));
        Assert.False(PairingGate.IsLoopback(null));
        Assert.Equal("192.168.0.23", PairingGate.AddressText(IPAddress.Parse("::ffff:192.168.0.23")));
        Assert.Equal("192.168.0.23", PairingGate.AddressText(IPAddress.Parse("192.168.0.23")));
        Assert.Null(PairingGate.AddressText(null));
    }

    [Fact]
    public void Device_names_tell_a_phone_from_a_tablet()
    {
        Assert.Equal("Chrome on Android", PairingGate.DeviceName("Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 Chrome/141.0 Mobile Safari/537.36"));
        Assert.Equal("Safari on iPad", PairingGate.DeviceName("Mozilla/5.0 (iPad; CPU OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1"));
        Assert.Equal("Edge on Windows", PairingGate.DeviceName("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/141.0 Safari/537.36 Edg/141.0"));
        Assert.Equal("Browser", PairingGate.DeviceName(""));
    }

    [Fact]
    public void Framing_is_allowed_for_the_hub_under_the_same_host_name_only()
    {
        Assert.Equal("'self' http://pc:8079", FrameAncestors.Policy("http", "pc:8077", 8079));
        Assert.Equal("'self' http://192.168.0.5:8079", FrameAncestors.Policy("http", "192.168.0.5:8077", 8079));
        Assert.Equal("'self'", FrameAncestors.Policy("http", "pc:8077", 0));
        Assert.Equal("'self'", FrameAncestors.Policy("http", "pc; script-src *", 8079));
        Assert.Equal("'self'", FrameAncestors.Policy("javascript", "pc", 8079));
    }

    [Theory]
    [InlineData("testhost.evil.example:8090")]                 // der Name eines Angreifers, auf 127.0.0.1 gelegt
    [InlineData("evil.example:8090")]
    [InlineData("testhost.example.lan.evil.example:8090")]
    public void A_dns_rebinding_name_is_refused_from_this_machine_too(string host)
    {
        Assert.Equal(GateAction.WrongHost, PairingGate.Decide(host, Machine, Domain, loopback: true, remoteEnabled: false, "/hub", wantsPage: true, role: Admin));
        Assert.Equal(GateAction.WrongHost, PairingGate.Decide(host, Machine, Domain, loopback: true, remoteEnabled: false, "/api/process/end", wantsPage: false, role: Admin));
    }

    [Theory]
    [InlineData("/api/ping")]
    [InlineData("/pair")]
    [InlineData("/manifest.webmanifest")]
    public void The_host_is_checked_before_the_paths_that_are_open_to_everyone(string path)
    {
        Assert.Equal(GateAction.WrongHost, PairingGate.Decide("testhost.evil.example:8090", Machine, Domain, loopback: false, remoteEnabled: true, path, wantsPage: false, role: null));
        Assert.Equal(GateAction.WrongHost, PairingGate.Decide("testhost.evil.example:8090", Machine, Domain, loopback: true, remoteEnabled: false, path, wantsPage: false, role: Admin));
    }

    [Theory]
    [InlineData("127.0.0.1:8090")]
    [InlineData("localhost:8090")]
    [InlineData("[::1]:8090")]
    [InlineData("testhost:8090")]
    [InlineData("TESTHOST.example.lan:8090")]
    [InlineData("testhost.example.lan")]
    public void Addresses_localhost_and_this_machines_name_get_in_from_this_machine(string host) =>
        Assert.Equal(GateAction.Allow, PairingGate.Decide(host, Machine, Domain, loopback: true, remoteEnabled: false, "/hub", wantsPage: true, role: Admin));

    [Theory]
    [InlineData("192.0.2.10:8090")]
    [InlineData("testhost.example.lan:8090")]
    public void A_paired_device_gets_in_under_an_address_or_this_machines_name(string host) =>
        Assert.Equal(GateAction.Allow, PairingGate.Decide(host, Machine, Domain, loopback: false, remoteEnabled: true, "/api/snapshot", wantsPage: false, role: Admin));

    [Theory]
    [InlineData("testhost.other.example:8090")]              // andere DNS-Endung
    [InlineData("testhost.example.lan.evil.example:8090")]   // Name und Endung, aber mit fremdem Rest
    [InlineData("mytesthost:8090")]                          // der Rechnername steckt nur darin
    [InlineData("testhost-old:8090")]
    [InlineData("example.lan:8090")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_names_and_an_empty_host_are_refused(string? host) =>
        Assert.Equal(GateAction.WrongHost, PairingGate.Decide(host, Machine, Domain, loopback: true, remoteEnabled: false, "/hub", wantsPage: true, role: Admin));

    [Fact]
    public void Without_a_dns_suffix_only_the_bare_machine_name_counts()
    {
        Assert.Equal(GateAction.Allow, PairingGate.Decide("testhost:8090", Machine, null, loopback: true, remoteEnabled: false, "/hub", wantsPage: true, role: Admin));
        Assert.Equal(GateAction.WrongHost, PairingGate.Decide("testhost.example.lan:8090", Machine, null, loopback: true, remoteEnabled: false, "/hub", wantsPage: true, role: Admin));
    }
}
