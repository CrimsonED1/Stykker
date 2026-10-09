using System.Net;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Kopplung eines Model-Hosts (docs/plan-hosts-gateway.md, P3): Code am Server, Anfrage des Hosts, Kopplungsseite.
[TestClass]
public class N57_HostPairingTests
{
    private sealed class Clock { public DateTime Now = new(2026, 10, 7, 12, 0, 0); }

    private static HostRegistry Registry() =>
        new(new AppPaths(Path.Combine(Path.GetTempPath(), "slm-n57-" + Guid.NewGuid().ToString("N"))), new FakePlatform());

    [TestMethod]
    public void Code_SixDigits_WorksOnce_AndCreatesTheHost()
    {
        var reg = Registry();
        var pairing = new HostPairing(reg);
        var code = pairing.Code;
        Assert.AreEqual(6, code.Length);
        var p = pairing.TryPair(code[..3] + " " + code[3..], "gpu-box");   // mit Leerzeichen wie angezeigt
        Assert.IsNotNull(p);
        Assert.AreEqual("gpu-box", reg.Find(p.Value.Token)?.Name);
        Assert.IsNull(pairing.TryPair(code, "again"), "a code works only once");
        Assert.AreNotEqual(code, pairing.Code);
    }

    [TestMethod]
    public void Code_Expires_AndWrongTriesRotateIt()
    {
        var clock = new Clock();
        var pairing = new HostPairing(Registry(), () => clock.Now);
        var code = pairing.Code;
        clock.Now = clock.Now + HostPairing.Lifetime;
        Assert.IsNull(pairing.TryPair(code, "late"));

        var fresh = pairing.Code;
        for (int i = 0; i < HostPairing.MaxWrongTries; i++) Assert.IsNull(pairing.TryPair("000000" == fresh ? "111111" : "000000", "guess"));
        Assert.AreNotEqual(fresh, pairing.Code, "after five wrong tries there is a new code");
    }

    [TestMethod]
    public async Task Client_GetsTheToken_OrTheServersReason()
    {
        var h = new FakeHandler();
        h.Responder = (key, _) => key.EndsWith("/hosts/pair") ? (HttpStatusCode.OK, "{\"ok\":true,\"token\":\"tok\",\"id\":\"abc\",\"serverName\":\"MAIN\"}") : null;
        var ok = await HostPairClient.PairAsync("http://pc:17400", "123456", "box", h);
        Assert.IsTrue(ok.Ok);
        Assert.AreEqual("tok", ok.Token);
        Assert.AreEqual("MAIN", ok.ServerName);

        var no = new FakeHandler();
        no.Responder = (key, _) => (HttpStatusCode.Unauthorized, "{\"ok\":false,\"message\":\"" + Strings.HostPairWrongCode + "\"}");
        var refused = await HostPairClient.PairAsync("http://pc:17400/", "000000", "box", no);
        Assert.IsFalse(refused.Ok);
        Assert.AreEqual(Strings.HostPairWrongCode, refused.Message);

        Assert.AreEqual(Strings.HostPairBadUrl, (await HostPairClient.PairAsync("pc:17400", "1", "box", h)).Message);
        Assert.AreEqual(Strings.HostPairBadUrl, (await HostPairClient.PairAsync("ftp://pc", "1", "box", h)).Message);
    }

    [TestMethod, Timeout(20000)]
    public async Task Page_OnlyUnderItsSecretAddress_AndPairsWithTheForm()
    {
        string? gotServer = null, gotCode = null;
        using var page = new HostPairPage((server, code) =>
        {
            gotServer = server; gotCode = code;
            return Task.FromResult(new HostPairClient.Result(true, Strings.HostPairDone("MAIN"), "tok", "MAIN"));
        }, _ => Task.FromResult(new List<DiscoveredNode> { new("MAIN", "http://192.168.1.10:17400", true, false) }));
        page.Start();
        StringAssert.StartsWith(page.Url, "http://localhost:");
        using var http = new HttpClient();

        var form = await http.GetStringAsync(page.Url);
        StringAssert.Contains(form, Strings.HostPairHint);
        var search = await http.GetStringAsync(page.Url + "search");
        StringAssert.Contains(search, "192.168.1.10");

        var root = new Uri(page.Url).GetLeftPart(UriPartial.Authority);
        Assert.AreEqual(HttpStatusCode.NotFound, (await http.GetAsync(root + "/")).StatusCode, "without the secret path there is nothing");

        var post = await http.PostAsync(page.Url + "pair", new FormUrlEncodedContent(new Dictionary<string, string> { ["server"] = "http://192.168.1.10:17400", ["code"] = "123 456" }));
        Assert.AreEqual(HttpStatusCode.OK, post.StatusCode);
        StringAssert.Contains(await post.Content.ReadAsStringAsync(), "MAIN");
        Assert.AreEqual("http://192.168.1.10:17400", gotServer);
        Assert.AreEqual("123 456", gotCode);
    }
}
