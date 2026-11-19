using System.Net;
using StykkerMeshShare.Core;
using Xunit;

namespace StykkerMeshShare.Tests;

public class NetworkRouteTests
{
    [Theory]
    [InlineData("127.0.0.1", PeerRoute.ThisPc)]
    [InlineData("::1", PeerRoute.ThisPc)]
    [InlineData("192.168.1.20", PeerRoute.Lan)]
    [InlineData("10.0.0.5", PeerRoute.Lan)]
    [InlineData("172.20.1.1", PeerRoute.Lan)]
    [InlineData("169.254.3.4", PeerRoute.Lan)]
    [InlineData("fe80::1", PeerRoute.Lan)]
    [InlineData("fd12:3456::1", PeerRoute.Lan)]
    [InlineData("100.101.102.103", PeerRoute.Tailscale)]
    [InlineData("fd7a:115c:a1e0::1", PeerRoute.Tailscale)]
    [InlineData("8.8.8.8", PeerRoute.Other)]
    [InlineData("100.20.0.1", PeerRoute.Other)]
    public void Classify(string address, PeerRoute expected) =>
        Assert.Equal(expected, NetworkRoute.Classify(IPAddress.Parse(address)));

    [Fact]
    public void LanIsPreferredOverTailscale() => Assert.True(PeerRoute.Lan < PeerRoute.Tailscale);

    [Theory]
    [InlineData("192.168.1.5:17404", "192.168.1.5")]
    [InlineData("[fe80::1]:17404", "fe80::1")]
    [InlineData("pc.example.ts.net:17404", "pc.example.ts.net")]
    [InlineData("pc", "pc")]
    public void HostOf(string address, string host) => Assert.Equal(host, NetworkRoute.HostOf(address));

    [Theory]
    [InlineData("192.168.1.5:17404", PeerRoute.Lan)]
    [InlineData("100.70.1.2:17404", PeerRoute.Tailscale)]
    [InlineData("pc.example.ts.net:17404", PeerRoute.Tailscale)]
    [InlineData("127.0.0.1:17404", PeerRoute.ThisPc)]
    public async Task Resolve(string address, PeerRoute expected) =>
        Assert.Equal(expected, await NetworkRoute.ResolveAsync(address, CancellationToken.None));
}
