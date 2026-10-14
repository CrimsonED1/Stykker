using Stykker.Shared.Web;

namespace Stykker.Shared.Tests;

public class ServiceProxyRulesTests
{
    private const string Admin = "admin", Viewer = "viewer";
    private const string Target = "http://127.0.0.1:41999";

    // Die Zeilen der Tabelle in 4.6, je mit einer Anfrage ohne Origin (dazu weiter unten die Origin-Fälle)
    [Theory]
    [InlineData(true, false, null, ServiceAccess.Allow)]          // von dieser Maschine
    [InlineData(true, true, Viewer, ServiceAccess.Allow)]
    [InlineData(false, false, Admin, ServiceAccess.Forbidden)]    // aus dem Netz, Netzschalter aus
    [InlineData(false, true, null, ServiceAccess.Unauthorized)]   // aus dem Netz, kein gültiges Gerät
    [InlineData(false, true, Viewer, ServiceAccess.NotAdmin)]     // Rolle viewer für diese id
    [InlineData(false, true, Admin, ServiceAccess.Allow)]         // Rolle admin für diese id
    public void Each_row_of_the_table_gives_its_answer(bool loopback, bool remote, string? role, ServiceAccess expected) =>
        Assert.Equal(expected, ServiceProxyRules.Decide(loopback, remote, role, null, "http", "pc:8091"));

    [Fact]
    public void The_answers_carry_the_status_and_the_text_from_the_table()
    {
        Assert.Equal(401, ServiceProxyRules.StatusOf(ServiceAccess.Unauthorized));
        Assert.Equal("This device is not paired. Pair it in the Stykker hub.", ServiceProxyRules.MessageOf(ServiceAccess.Unauthorized));
        Assert.Equal(403, ServiceProxyRules.StatusOf(ServiceAccess.NotAdmin));
        Assert.Equal("Only an admin of this module may open it. The PC can change the role in the Stykker hub.", ServiceProxyRules.MessageOf(ServiceAccess.NotAdmin));
        Assert.Equal(403, ServiceProxyRules.StatusOf(ServiceAccess.Forbidden));
        Assert.Equal(403, ServiceProxyRules.StatusOf(ServiceAccess.ForeignOrigin));
        Assert.Equal(200, ServiceProxyRules.StatusOf(ServiceAccess.Allow));
        Assert.Null(ServiceProxyRules.MessageOf(ServiceAccess.Allow));
    }

    [Theory]
    [InlineData("http://pc:8091", "http", "pc:8091", true)]         // die Seite der Weiterleitung selbst
    [InlineData("http://PC:8091", "http", "pc:8091", true)]         // Groß- und Kleinschreibung gleich
    [InlineData("http://evil.example", "http", "pc:8091", false)]
    [InlineData("http://pc:8092", "http", "pc:8091", false)]        // anderer Port ist eine andere Seite
    [InlineData("https://pc:8091", "http", "pc:8091", false)]
    [InlineData("null", "http", "pc:8091", false)]
    [InlineData("", "http", "pc:8091", true)]
    public void A_foreign_origin_is_refused_even_from_this_machine_or_a_paired_device(string origin, string scheme, string host, bool allowed)
    {
        var fromPc = ServiceProxyRules.Decide(true, false, null, origin, scheme, host);
        var fromPhone = ServiceProxyRules.Decide(false, true, Admin, origin, scheme, host);

        Assert.Equal(allowed, fromPc == ServiceAccess.Allow);
        Assert.Equal(allowed, fromPhone == ServiceAccess.Allow);
        if (!allowed) Assert.Equal(ServiceAccess.ForeignOrigin, fromPhone);
    }

    [Fact]
    public void A_request_with_a_foreign_origin_gets_403_whatever_the_role()
    {
        Assert.Equal(ServiceAccess.ForeignOrigin, ServiceProxyRules.Decide(false, true, null, "http://evil", "http", "pc:8091"));
        Assert.Equal(ServiceAccess.ForeignOrigin, ServiceProxyRules.Decide(false, false, Admin, "http://evil", "http", "pc:8091"));
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
        Assert.Equal(Target, ServiceProxyRules.OriginFor("http://pc:8091", Target));
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
