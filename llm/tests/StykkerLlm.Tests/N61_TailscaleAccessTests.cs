using System.Net;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Der Tailscale-Schalter: nur Geräte im Tailnet dürfen von außen, Home/VPN bleibt das ganze Netz. Ohne Netz, mit Adressen als Text.
[TestClass]
public class N61_TailscaleAccessTests
{
    private static AppPaths TempPaths(string tag)
    {
        var p = new AppPaths(Path.Combine(Path.GetTempPath(), "slm-n61-" + tag + "-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(p.Root);
        return p;
    }

    [TestMethod]
    public void IsTailscale_RecognizesTheTailnetAddresses()
    {
        foreach (var ip in new[] { "100.64.0.1", "100.101.1.2", "100.127.255.254", "::ffff:100.100.1.1", "fd7a:115c:a1e0::1" })
            Assert.IsTrue(NetAddr.IsTailscale(ip), ip);
        foreach (var ip in new[] { "100.63.255.255", "100.128.0.1", "192.168.1.20", "10.0.0.5", "127.0.0.1", "fe80::1" })
            Assert.IsFalse(NetAddr.IsTailscale(ip), ip);
    }

    [TestMethod]
    public void AllowsRemote_TailscaleOnlyTheTailnet_HomeVpnTheWholeNetwork()
    {
        var access = new AccessControl(TempPaths("scope"), new FakePlatform());
        var tailnet = IPAddress.Parse("100.101.1.2");
        var lan = IPAddress.Parse("192.168.1.20");

        Assert.IsFalse(access.AllowsRemote(tailnet), "alles aus");
        Assert.IsFalse(access.AllowsRemote(lan));

        access.SetTailscale(true);
        Assert.IsTrue(access.AllowsRemote(tailnet), "Tailscale an: das Tailnet kommt rein");
        Assert.IsFalse(access.AllowsRemote(lan), "aber nicht das ganze Netz");

        access.SetTailscale(false);
        access.SetRemote(true);
        Assert.IsTrue(access.AllowsRemote(lan), "Home/VPN an: das ganze Netz");
        Assert.IsTrue(access.AllowsRemote(tailnet));
    }

    [TestMethod]
    public void TailscaleSwitch_IsKeptAfterRestart()
    {
        var paths = TempPaths("keep");
        new AccessControl(paths, new FakePlatform()).SetTailscale(true);
        var again = new AccessControl(paths, new FakePlatform());
        Assert.IsTrue(again.TailscaleEnabled);
        Assert.IsTrue(again.AllowsRemote(IPAddress.Parse("100.101.1.2")));
    }

    [TestMethod]
    public void IsOwnHost_LoopbackAlways_TailnetIpOnlyWithTheSwitch()
    {
        Assert.IsTrue(NetAddr.IsOwnHost("127.0.0.1:17400", false));
        Assert.IsTrue(NetAddr.IsOwnHost("localhost:17400", false));
        Assert.IsTrue(NetAddr.IsOwnHost("[::1]:17400", false));
        Assert.IsTrue(NetAddr.IsOwnHost("", false), "HTTP/1.0 ohne Host");
        Assert.IsFalse(NetAddr.IsOwnHost("100.101.1.2:17400", false), "die Tailnet-IP ohne Schalter");
        Assert.IsTrue(NetAddr.IsOwnHost("100.101.1.2:17400", true));
        Assert.IsTrue(NetAddr.IsOwnHost("[fd7a:115c:a1e0::1]:17400", true));
        Assert.IsFalse(NetAddr.IsOwnHost("evil.example:17400", true), "fremde Domainnamen bleiben draußen");
        Assert.IsFalse(NetAddr.IsOwnHost("100.63.0.1:17400", true), "außerhalb von 100.64.0.0/10");
    }
}
