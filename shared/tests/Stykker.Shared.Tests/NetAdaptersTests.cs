using System.Net.NetworkInformation;
using Stykker.Shared.Net;

namespace Stykker.Shared.Tests;

// Netzwerk je Adapter: Filter-Treiber zählen nicht, VPNs (Tailscale) sind Tunnel und gehen nicht in die Summe.
public class NetAdaptersTests
{
    [Theory]
    [InlineData("Ethernet-QoS Packet Scheduler-0000", true)]
    [InlineData("Ethernet-WFP Native MAC Layer LightWeight Filter-0000", true)]
    [InlineData("Ethernet-VirtualBox NDIS Light-Weight Filter-0000", true)]
    [InlineData("Ethernet 2-WFP 802.3 MAC Layer LightWeight Filter-0001", true)]
    [InlineData("Ethernet", false)]
    [InlineData("Ethernet 2", false)]
    [InlineData("WLAN", false)]
    [InlineData("Tailscale", false)]
    [InlineData("vEthernet (Default Switch)", false)]
    [InlineData("Router-1234", false)]   // vier Ziffern, aber kein Filter
    public void FilterDriversAreRecognized(string name, bool filter) => Assert.Equal(filter, NetAdapters.IsFilter(name));

    [Theory]
    [InlineData(NetworkInterfaceType.Unknown, "Tailscale", "Tailscale Tunnel", NetAdapterKind.Tunnel)]
    [InlineData(NetworkInterfaceType.Ethernet, "WireGuard", "WireGuard Tunnel", NetAdapterKind.Tunnel)]
    [InlineData(NetworkInterfaceType.Tunnel, "Teredo", "Teredo Tunneling Pseudo-Interface", NetAdapterKind.Tunnel)]
    [InlineData(NetworkInterfaceType.Wireless80211, "WLAN", "Intel Wi-Fi 6", NetAdapterKind.Wifi)]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet", "Realtek PCIe GbE", NetAdapterKind.Ethernet)]
    [InlineData(NetworkInterfaceType.Ppp, "Modem", "Dial-up", NetAdapterKind.Other)]
    public void KindOfAdapter(NetworkInterfaceType type, string name, string description, NetAdapterKind kind) =>
        Assert.Equal(kind, NetAdapters.KindOf(type, name, description));

    [Fact]
    public void TunnelsDoNotCountInTotals()
    {
        Assert.False(new NetAdapterRow("1", "Tailscale", "Tailscale Tunnel", NetAdapterKind.Tunnel, 0, 10, 10).CountsInTotal);
        Assert.True(new NetAdapterRow("2", "Ethernet", "Realtek", NetAdapterKind.Ethernet, 125e6, 10, 10).CountsInTotal);
    }

    [Fact]
    public void ReadingTheRealAdaptersWorks()
    {
        var net = new NetAdapters();
        var first = net.Read();
        if (first == null) return;   // ohne Netz (Testrechner): nichts zu prüfen
        Assert.All(first, a => Assert.Null(a.RxBytesPerSec));          // der erste Takt setzt nur die Basis
        Assert.DoesNotContain(first, a => NetAdapters.IsFilter(a.Name));
    }
}
