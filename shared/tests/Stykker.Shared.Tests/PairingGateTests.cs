using System.Net;
using Stykker.Shared.Web;

namespace Stykker.Shared.Tests;

public class PairingGateTests
{
    private const string Admin = "admin", Viewer = "viewer";

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
}
