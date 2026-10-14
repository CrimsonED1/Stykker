using Stykker.Pairing;
using Stykker.Shared.Web;

namespace Stykker.Shared.Tests;

// Die Zusatznamen aus stykker.json ("hostNames") an der Zugangsprüfung und an der Weiterleitung. Der Name dieses PCs und
// die Namen sind Platzhalter.
public class ExtraHostNamesTests
{
    private const string Admin = "admin", Viewer = "viewer";
    private const string Machine = "testhost", Domain = "example.lan";
    private const string Extra = "pc.example.ts.net";
    private const string ExtraHost = "pc.example.ts.net:8091";
    private static readonly string[] Extras = [Extra];

    [Fact]
    public void A_service_answers_an_extra_name_from_a_paired_admin()
    {
        Assert.Equal(ServiceAccess.Allow, ServiceProxyRules.Decide(false, true, Admin, null, "http", ExtraHost, Machine, Domain, Extras));
    }

    [Fact]
    public void A_service_refuses_the_extra_name_without_the_extra_names()
    {
        Assert.Equal(ServiceAccess.WrongHost, ServiceProxyRules.Decide(false, true, Admin, null, "http", ExtraHost, Machine, Domain));
        Assert.Equal(ServiceAccess.WrongHost, ServiceProxyRules.Decide(false, true, Admin, null, "http", ExtraHost, Machine, Domain, []));
    }

    [Fact]
    public void The_extra_name_gives_no_more_than_the_role_of_the_device()
    {
        Assert.Equal(ServiceAccess.NotAdmin, ServiceProxyRules.Decide(false, true, Viewer, null, "http", ExtraHost, Machine, Domain, Extras));
    }

    [Fact]
    public void The_origin_is_still_checked_for_an_extra_name()
    {
        Assert.Equal(ServiceAccess.ForeignOrigin, ServiceProxyRules.Decide(false, true, Admin, "http://evil.example", "http", ExtraHost, Machine, Domain, Extras));
    }

    [Fact]
    public void Another_name_is_still_refused_with_the_extra_names()
    {
        Assert.Equal(ServiceAccess.WrongHost, ServiceProxyRules.Decide(false, true, Admin, null, "http", "pc.example.ts.net.evil.example:8091", Machine, Domain, Extras));
        Assert.Equal(ServiceAccess.WrongHost, ServiceProxyRules.Decide(true, false, null, null, "http", "testhost.evil.example:8091", Machine, Domain, Extras));
    }

    [Fact]
    public void HostAllowed_takes_the_extra_names_and_keeps_its_old_form()
    {
        Assert.False(ServiceProxyRules.HostAllowed(ExtraHost, Machine, Domain));
        Assert.True(ServiceProxyRules.HostAllowed(ExtraHost, Machine, Domain, Extras));
        Assert.True(ServiceProxyRules.HostAllowed("testhost:8091", Machine, Domain, Extras));
    }

    [Fact]
    public void The_gate_allows_an_extra_name_for_the_remote_paths_of_a_paired_device()
    {
        Assert.Equal(GateAction.Allow, PairingGate.Decide(ExtraHost, Machine, Domain, false, true, "/hub", true, Admin, Extras));
        Assert.Equal(GateAction.Pair, PairingGate.Decide(ExtraHost, Machine, Domain, false, true, "/pair", true, null, Extras));
        Assert.Equal(GateAction.Allow, PairingGate.Decide(ExtraHost, Machine, Domain, false, true, "/api/ping", false, null, Extras));
    }

    [Fact]
    public void The_gate_refuses_the_extra_name_without_it_in_the_list()
    {
        Assert.Equal(GateAction.WrongHost, PairingGate.Decide(ExtraHost, Machine, Domain, false, true, "/hub", true, Admin));
        Assert.Equal(GateAction.WrongHost, PairingGate.Decide(ExtraHost, Machine, Domain, false, true, "/api/ping", false, null, []));
    }

    [Fact]
    public void The_gate_refuses_a_foreign_name_even_with_the_extra_names_from_this_machine()
    {
        Assert.Equal(GateAction.WrongHost, PairingGate.Decide("evil.example:8090", Machine, Domain, true, false, "/pair", true, null, Extras));
        Assert.Equal(GateAction.WrongHost, PairingGate.Decide("pc.example.ts.net.evil.example", Machine, Domain, true, false, "/hub", true, Admin, Extras));
    }

    [Fact]
    public void The_pcs_own_name_still_works_with_the_extra_names()
    {
        Assert.Equal(GateAction.Allow, PairingGate.Decide("testhost:8090", Machine, Domain, true, false, "/hub", true, Admin, Extras));
        Assert.Equal(GateAction.Allow, PairingGate.Decide("127.0.0.1:8090", Machine, Domain, true, false, "/hub", true, Admin, Extras));
    }

    [Fact]
    public void The_options_carry_the_extra_names_and_default_to_none()
    {
        Assert.Null(new PairingGateOptions("hud", "StykkerHUD").ExtraHostNames);
        Assert.Equal(Extras, new PairingGateOptions("hud", "StykkerHUD", ExtraHostNames: Extras).ExtraHostNames);
    }
}
