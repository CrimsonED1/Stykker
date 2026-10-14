using System.Text.Json;
using Stykker.Pairing;

namespace Stykker.Pairing.Tests;

// Der Host-Kopf: dieselben Fälle wie die Host-Tests in Stykker.Shared (ServiceProxyRulesTests), dazu die Zusatznamen aus
// stykker.json. Der Name dieses PCs ist ein Platzhalter.
public sealed class HostNamesTests : IDisposable
{
    private const string Machine = "testhost", Domain = "example.lan";
    private static readonly string[] NoExtra = [];
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

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
        Assert.Equal(allowed, HostNames.Allowed(host, Machine, Domain, NoExtra));

    [Fact]
    public void The_pcs_own_name_counts_only_when_it_has_no_dns_ending()
    {
        Assert.True(HostNames.Allowed("testhost:8076", Machine, null, NoExtra));
        Assert.False(HostNames.Allowed("testhost.example.lan:8076", Machine, null, NoExtra));
        Assert.False(HostNames.Allowed("testhost.example.lan:8076", Machine, "", NoExtra));
    }

    [Fact]
    public void Without_a_machine_name_only_addresses_and_localhost_are_hosts()
    {
        Assert.True(HostNames.Allowed("127.0.0.1:8076", "", null, NoExtra));
        Assert.True(HostNames.Allowed("localhost", "", null, NoExtra));
        Assert.False(HostNames.Allowed("testhost:8076", "", null, NoExtra));
    }

    [Theory]
    [InlineData("pc.example.ts.net:8091")]                  // mit Port
    [InlineData("pc.example.ts.net")]                       // ohne Port
    [InlineData("PC.Example.TS.NET:8091")]                  // ohne Groß-/Kleinschreibung
    public void An_extra_name_is_allowed_with_or_without_a_port(string host) =>
        Assert.True(HostNames.Allowed(host, Machine, Domain, ["pc.example.ts.net"]));

    [Theory]
    [InlineData("pc.example.ts.net.evil.example:8091")]     // der Zusatzname am Anfang
    [InlineData("evilpc.example.ts.net:8091")]              // der Zusatzname am Ende
    [InlineData("xpc.example.ts.net")]
    [InlineData("pc.example.ts.ne")]                        // ein Teil davon
    [InlineData("pc.example.ts.net.")]                      // leere Endung
    [InlineData("evil.example:8091")]
    public void A_name_that_only_starts_or_ends_with_the_extra_name_is_refused(string host) =>
        Assert.False(HostNames.Allowed(host, Machine, Domain, ["pc.example.ts.net"]));

    [Fact]
    public void Without_the_extra_name_the_tailscale_name_is_refused()
    {
        Assert.False(HostNames.Allowed("pc.example.ts.net:8091", Machine, Domain, NoExtra));
    }

    [Fact]
    public void An_extra_name_does_not_open_the_dns_rebinding_form_of_this_pcs_name()
    {
        Assert.False(HostNames.Allowed("testhost.evil.example:8091", Machine, Domain, ["pc.example.ts.net"]));
    }

    [Theory]
    [InlineData("*")]                                       // ein Platzhalter als Zusatzname trifft nie
    [InlineData("*:8091")]
    [InlineData("*.example.ts.net")]
    [InlineData("pc.example.ts.net")]
    public void An_extra_name_that_is_not_a_valid_name_matches_nothing(string host) =>
        Assert.False(HostNames.Allowed(host, Machine, Domain, new[] { "*", "*.example.ts.net", " ", "" }));

    [Fact]
    public void A_wildcard_extra_name_does_not_match_a_sub_name()
    {
        Assert.False(HostNames.Allowed("pc.example.ts.net", Machine, Domain, ["*.example.ts.net"]));
    }

    [Theory]
    [InlineData("pc.example.ts.net")]
    [InlineData("pc.example.ts.net.")]
    [InlineData("pc..example.ts.net")]
    [InlineData(".pc.example.ts.net")]
    [InlineData("pc/example.ts.net")]
    [InlineData("pc_example.ts.net")]
    [InlineData("pc example.ts.net")]
    [InlineData("1.2.3.4")]
    [InlineData("localhost")]
    [InlineData("::1")]
    public void IsValidName_accepts_only_plain_dns_names(string name) =>
        Assert.Equal(name == "pc.example.ts.net", HostNames.IsValidName(name));

    [Fact]
    public void A_name_of_253_characters_is_valid_and_254_is_not()
    {
        Assert.True(HostNames.IsValidName(new string('a', 253)));
        Assert.False(HostNames.IsValidName(new string('a', 254)));
    }

    [Fact]
    public void LoadExtra_without_a_file_is_empty()
    {
        Assert.Empty(HostNames.LoadExtra(null));
        Assert.Empty(HostNames.LoadExtra(""));
        Assert.Empty(HostNames.LoadExtra(Path.Combine(_folder.Root, "missing.json")));
    }

    [Fact]
    public void LoadExtra_without_the_field_is_empty()
    {
        Assert.Empty(HostNames.LoadExtra(Write("""{ "modules": [] }""")));
        Assert.Empty(HostNames.LoadExtra(Write("{}")));
    }

    [Fact]
    public void LoadExtra_reads_only_the_top_level_field()
    {
        Assert.Empty(HostNames.LoadExtra(Write("""{ "modules": [ { "id": "llm", "hostNames": ["pc.example.ts.net"] } ] }""")));
    }

    [Fact]
    public void LoadExtra_with_a_broken_or_odd_file_is_empty()
    {
        Assert.Empty(HostNames.LoadExtra(Write("{ not json")));
        Assert.Empty(HostNames.LoadExtra(Write("""["pc.example.ts.net"]""")));
        Assert.Empty(HostNames.LoadExtra(Write("""{ "hostNames": "pc.example.ts.net" }""")));
        Assert.Empty(HostNames.LoadExtra(Write("")));
    }

    [Fact]
    public void LoadExtra_reads_the_names_and_trims_them()
    {
        var names = HostNames.LoadExtra(Write("""{ "hostNames": ["pc.example.ts.net", "  other.example.ts.net  "] }"""));

        Assert.Equal(new[] { "pc.example.ts.net", "other.example.ts.net" }, names);
    }

    [Fact]
    public void LoadExtra_drops_the_invalid_entries_quietly()
    {
        var file = Json(new object[]
        {
            "", " ", "a b", "pc/evil", "*.example.ts.net", "*", "localhost", "192.0.2.10", "::1", "a..b", ".pc.example",
            "pc.example.", "pc_example", "é.example", 42, null!, true, new string('a', 254),
            "ok.example.ts.net",
        });

        Assert.Equal(new[] { "ok.example.ts.net" }, HostNames.LoadExtra(Write(file)));
    }

    [Fact]
    public void LoadExtra_keeps_a_name_of_253_characters()
    {
        var file = Json(new object[] { new string('a', 253) });

        Assert.Equal(new[] { new string('a', 253) }, HostNames.LoadExtra(Write(file)));
    }

    [Fact]
    public void LoadExtra_drops_duplicates_without_regard_to_case()
    {
        var file = Json(new object[] { "pc.example.ts.net", "PC.Example.TS.NET", "other.example.ts.net" });

        Assert.Equal(new[] { "pc.example.ts.net", "other.example.ts.net" }, HostNames.LoadExtra(Write(file)));
    }

    [Fact]
    public void Names_from_LoadExtra_open_the_host_check_for_that_name_only()
    {
        var names = HostNames.LoadExtra(Write(Json(new object[] { "pc.example.ts.net" })));

        Assert.True(HostNames.Allowed("pc.example.ts.net:8091", Machine, Domain, names));
        Assert.False(HostNames.Allowed("evil.example.ts.net:8091", Machine, Domain, names));
    }

    private string Write(string text)
    {
        var file = Path.Combine(_folder.Root, "stykker.json");
        File.WriteAllText(file, text);
        return file;
    }

    private static string Json(object hostNames) => JsonSerializer.Serialize(new { hostNames });
}
