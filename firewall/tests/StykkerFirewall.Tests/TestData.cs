using StykkerFirewall.Core;

namespace StykkerFirewall.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    public const string Browser = @"C:\Program Files\Example\browser.exe";

    public static ConnectionAttempt Out(string app = Browser, string remote = "192.0.2.10", int port = 443, int proto = IpProtocol.Tcp) =>
        new(Guid.NewGuid(), Now, app, 4242, TrafficDirection.Outbound, proto, "192.0.2.2", 50000, remote, port);

    public static ConnectionAttempt In(string app = Browser, string remote = "198.51.100.4", int localPort = 27015, int proto = IpProtocol.Udp) =>
        new(Guid.NewGuid(), Now, app, 4242, TrafficDirection.Inbound, proto, "192.0.2.2", localPort, remote, 61000);
}

internal sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Value { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Value;
}
