using Stykker.Shared.Web;

namespace Stykker.Shared.Tests;

public class ServiceProxyRulesTests
{
    private const string Admin = "admin", Viewer = "viewer";
    private const string Target = "http://127.0.0.1:41999";
    // Der Name dieses PCs in den Tests (ein Platzhalter) und seine DNS-Endung
    private const string Machine = "testhost", Domain = "example.lan";
    private const string Host = "testhost:8091";

    // Die Zeilen der Tabelle in 4.6, je mit einer Anfrage ohne Origin (dazu weiter unten die Origin-Fälle)
    [Theory]
    [InlineData(true, false, null, ServiceAccess.Allow)]          // von dieser Maschine
    [InlineData(true, true, Viewer, ServiceAccess.Allow)]
    [InlineData(false, false, Admin, ServiceAccess.Forbidden)]    // aus dem Netz, Netzschalter aus
    [InlineData(false, true, null, ServiceAccess.Unauthorized)]   // aus dem Netz, kein gültiges Gerät
    [InlineData(false, true, Viewer, ServiceAccess.NotAdmin)]     // Rolle viewer für diese id
    [InlineData(false, true, Admin, ServiceAccess.Allow)]         // Rolle admin für diese id
    public void Each_row_of_the_table_gives_its_answer(bool loopback, bool remote, string? role, ServiceAccess expected) =>
        Assert.Equal(expected, ServiceProxyRules.Decide(loopback, remote, role, null, "http", Host, Machine, Domain));

    [Fact]
    public void The_answers_carry_the_status_and_the_text_from_the_table()
    {
        Assert.Equal(401, ServiceProxyRules.StatusOf(ServiceAccess.Unauthorized));
        Assert.Equal("This device is not paired. Pair it in the Stykker hub.", ServiceProxyRules.MessageOf(ServiceAccess.Unauthorized));
        Assert.Equal(403, ServiceProxyRules.StatusOf(ServiceAccess.NotAdmin));
        Assert.Equal("Only an admin of this module may open it. The PC can change the role in the Stykker hub.", ServiceProxyRules.MessageOf(ServiceAccess.NotAdmin));
        Assert.Equal(403, ServiceProxyRules.StatusOf(ServiceAccess.Forbidden));
        Assert.Equal(403, ServiceProxyRules.StatusOf(ServiceAccess.ForeignOrigin));
        Assert.Equal(403, ServiceProxyRules.StatusOf(ServiceAccess.WrongHost));
        Assert.Equal("Wrong host name. Open this service by the PC's address.", ServiceProxyRules.MessageOf(ServiceAccess.WrongHost));
        Assert.Equal(200, ServiceProxyRules.StatusOf(ServiceAccess.Allow));
        Assert.Null(ServiceProxyRules.MessageOf(ServiceAccess.Allow));
    }

    [Theory]
    [InlineData("http://testhost:8091", "http", true)]              // die Seite der Weiterleitung selbst
    [InlineData("http://TESTHOST:8091", "http", true)]              // Groß- und Kleinschreibung gleich
    [InlineData("http://evil.example", "http", false)]
    [InlineData("http://testhost:8092", "http", false)]             // anderer Port ist eine andere Seite
    [InlineData("https://testhost:8091", "http", false)]
    [InlineData("null", "http", false)]
    [InlineData("", "http", true)]
    public void A_foreign_origin_is_refused_even_from_this_machine_or_a_paired_device(string origin, string scheme, bool allowed)
    {
        var fromPc = ServiceProxyRules.Decide(true, false, null, origin, scheme, Host, Machine, Domain);
        var fromPhone = ServiceProxyRules.Decide(false, true, Admin, origin, scheme, Host, Machine, Domain);

        Assert.Equal(allowed, fromPc == ServiceAccess.Allow);
        Assert.Equal(allowed, fromPhone == ServiceAccess.Allow);
        if (!allowed) Assert.Equal(ServiceAccess.ForeignOrigin, fromPhone);
    }

    [Fact]
    public void A_request_with_a_foreign_origin_gets_403_whatever_the_role()
    {
        Assert.Equal(ServiceAccess.ForeignOrigin, ServiceProxyRules.Decide(false, true, null, "http://evil", "http", Host, Machine, Domain));
        Assert.Equal(ServiceAccess.ForeignOrigin, ServiceProxyRules.Decide(false, false, Admin, "http://evil", "http", Host, Machine, Domain));
    }

    // Der Host-Kopf: eine IP-Adresse, localhost oder der Name dieses PCs, mit oder ohne Port
    [Theory]
    [InlineData("127.0.0.1:8076", true)]
    [InlineData("[::1]:8076", true)]
    [InlineData("192.0.2.10:8076", true)]
    [InlineData("localhost:8076", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("testhost:8076", true)]
    [InlineData("TestHost:8076", true)]
    [InlineData("testhost", true)]
    [InlineData("testhost.example.lan:8076", true)]                  // Name mit der DNS-Endung dieses PCs
    [InlineData("testhost.evil.example:8076", false)]                // dieselbe Form, fremde Domain: DNS-Rebinding
    [InlineData("testhost.example.lan.evil.example:8076", false)]
    [InlineData("evil.example:8076", false)]
    [InlineData("evil.example", false)]
    [InlineData("testhostevil.example:8076", false)]                 // enthält den Namen nur
    [InlineData("evil-testhost:8076", false)]
    [InlineData("testhost.:8076", false)]                            // leere Endung
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    [InlineData("testhost:", false)]                                 // Port leer
    [InlineData("testhost:0", false)]
    [InlineData("testhost:99999999", false)]
    [InlineData("testhost:80x", false)]
    [InlineData("[::1", false)]
    [InlineData("::1:8076", false)]                                  // IPv6 ohne Klammern
    public void Only_this_pc_by_address_localhost_or_its_name_is_a_host(string? host, bool allowed) =>
        Assert.Equal(allowed, ServiceProxyRules.HostAllowed(host, Machine, Domain));

    [Fact]
    public void The_pcs_own_name_counts_only_when_it_has_no_dns_ending()
    {
        Assert.True(ServiceProxyRules.HostAllowed("testhost:8076", Machine, null));
        Assert.False(ServiceProxyRules.HostAllowed("testhost.example.lan:8076", Machine, null));
        Assert.False(ServiceProxyRules.HostAllowed("testhost.example.lan:8076", Machine, ""));
    }

    [Fact]
    public void Without_a_machine_name_only_addresses_and_localhost_are_hosts()
    {
        Assert.True(ServiceProxyRules.HostAllowed("127.0.0.1:8076", "", null));
        Assert.True(ServiceProxyRules.HostAllowed("localhost", "", null));
        Assert.False(ServiceProxyRules.HostAllowed("testhost:8076", "", null));
    }

    [Fact]
    public void A_dns_rebinding_request_is_refused_from_this_machine_and_from_a_paired_device()
    {
        // Der Name des Angreifers zeigt auf 127.0.0.1; Host und Origin stimmen überein, die Anfrage kommt von Loopback
        const string evil = "testhost.evil.example:8091";
        const string origin = "http://testhost.evil.example:8091";
        Assert.Equal(ServiceAccess.WrongHost, ServiceProxyRules.Decide(true, false, null, origin, "http", evil, Machine, Domain));
        Assert.Equal(ServiceAccess.WrongHost, ServiceProxyRules.Decide(false, true, Admin, origin, "http", evil, Machine, Domain));
        Assert.Equal(ServiceAccess.WrongHost, ServiceProxyRules.Decide(true, false, null, "http://evil.example:8091", "http", "evil.example:8091", Machine, Domain));
    }

    [Fact]
    public void The_host_is_checked_before_the_origin()
    {
        Assert.Equal(ServiceAccess.WrongHost, ServiceProxyRules.Decide(false, true, Admin, "http://evil.example", "http", "evil.example:8091", Machine, Domain));
    }

    [Fact]
    public void The_hub_lists_a_foreign_service_only_to_this_machine_and_to_admins_of_it()
    {
        Assert.True(ServiceProxyRules.ListsForeignService(loopback: true, role: null));
        Assert.True(ServiceProxyRules.ListsForeignService(loopback: false, role: Admin));
        Assert.False(ServiceProxyRules.ListsForeignService(loopback: false, role: Viewer));
        Assert.False(ServiceProxyRules.ListsForeignService(loopback: false, role: null));
    }

    [Fact]
    public void The_host_header_becomes_the_target_address()
    {
        Assert.Equal("127.0.0.1:41999", ServiceProxyRules.TargetHost(Target));
        Assert.Equal("localhost:41999", ServiceProxyRules.TargetHost("http://localhost:41999"));
    }

    [Fact]
    public void An_origin_is_rewritten_to_the_target_only_when_there_is_one()
    {
        Assert.Equal(Target, ServiceProxyRules.OriginFor("http://testhost:8091", Target));
        Assert.Null(ServiceProxyRules.OriginFor(null, Target));
        Assert.Null(ServiceProxyRules.OriginFor("", Target));
    }

    [Fact]
    public void The_device_cookie_is_taken_out_and_the_others_stay()
    {
        Assert.Equal("a=1; b=2", ServiceProxyRules.CookiesWithoutDevice("a=1; stykker_device=secret; b=2"));
        Assert.Equal("a=1", ServiceProxyRules.CookiesWithoutDevice("stykker_device=secret; a=1"));
        Assert.Equal("a=1", ServiceProxyRules.CookiesWithoutDevice("a=1; stykker_device=secret"));
        Assert.Null(ServiceProxyRules.CookiesWithoutDevice("stykker_device=secret"));
        Assert.Null(ServiceProxyRules.CookiesWithoutDevice(null));
    }

    [Fact]
    public void Only_the_exact_device_cookie_name_is_taken_out()
    {
        Assert.Equal("stykker_device_x=1; xstykker_device=2", ServiceProxyRules.CookiesWithoutDevice("stykker_device_x=1; xstykker_device=2"));
        // Cookie-Namen gelten genau, wie sie gesendet werden: ein anderer Schreibweise ist ein anderes Cookie
        Assert.Equal("STYKKER_DEVICE=1; b=2", ServiceProxyRules.CookiesWithoutDevice("STYKKER_DEVICE=1; b=2"));
    }

    [Fact]
    public void The_device_token_is_read_from_the_cookie_header()
    {
        Assert.Equal("abc", ServiceProxyRules.DeviceToken("a=1; stykker_device=abc; b=2"));
        Assert.Null(ServiceProxyRules.DeviceToken("a=1"));
        Assert.Null(ServiceProxyRules.DeviceToken("stykker_device="));
        Assert.Null(ServiceProxyRules.DeviceToken(null));
    }

    [Fact]
    public void The_bearer_token_is_set_only_when_nothing_else_carries_one()
    {
        Assert.Equal("Bearer tok", ServiceProxyRules.BearerFor(null, null, "tok"));
        Assert.Equal("Bearer tok", ServiceProxyRules.BearerFor("", "", "tok"));
        Assert.Null(ServiceProxyRules.BearerFor("Bearer mine", null, "tok"));
        Assert.Null(ServiceProxyRules.BearerFor(null, "qwen-bearer.abc", "tok"));
        Assert.Null(ServiceProxyRules.BearerFor(null, "chat, qwen-bearer.abc", "tok"));
        Assert.Null(ServiceProxyRules.BearerFor(null, null, ""));
        Assert.Null(ServiceProxyRules.BearerFor(null, null, null));
    }

    [Fact]
    public void Another_subprotocol_does_not_stop_the_token()
    {
        Assert.False(ServiceProxyRules.HasBearerSubprotocol("acp, something"));
        Assert.Equal("Bearer tok", ServiceProxyRules.BearerFor(null, "acp, something", "tok"));
    }

    [Fact]
    public void The_frame_ancestors_policy_replaces_the_one_of_the_service()
    {
        var csp = ServiceProxyRules.FrameAncestorsFor("default-src 'self'; frame-ancestors 'none'; script-src 'self'", "http", "pc:8091", 8090);

        Assert.Equal("default-src 'self'; frame-ancestors 'self' http://pc:8090; script-src 'self'", csp);
    }

    [Fact]
    public void Without_frame_ancestors_the_policy_is_appended()
    {
        Assert.Equal("default-src 'self'; frame-ancestors 'self' http://pc:8090",
            ServiceProxyRules.FrameAncestorsFor("default-src 'self'", "http", "pc:8091", 8090));
    }

    [Fact]
    public void Without_any_policy_a_frame_ancestors_policy_is_set()
    {
        Assert.Equal("frame-ancestors 'self' http://pc:8090", ServiceProxyRules.FrameAncestorsFor(null, "http", "pc:8091", 8090));
    }

    [Fact]
    public void A_host_that_is_not_a_name_gives_only_self()
    {
        Assert.Equal("frame-ancestors 'self'", ServiceProxyRules.FrameAncestorsFor(null, "http", "pc; script-src *", 8090));
        Assert.Equal("frame-ancestors 'self'", ServiceProxyRules.FrameAncestorsFor(null, "javascript", "pc", 8090));
    }

    [Fact]
    public void The_frame_options_header_is_one_of_the_headers_to_drop()
    {
        Assert.Contains("X-Frame-Options", ServiceProxyRules.ResponseHeadersToDrop);
    }
}
