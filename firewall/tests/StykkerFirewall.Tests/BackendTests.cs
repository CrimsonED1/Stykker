using StykkerFirewall.Backend.Simulator;
using StykkerFirewall.Backend.WindowsFirewall;
using StykkerFirewall.Core;
using StykkerFirewall.Core.Backend;
using StykkerFirewall.Core.Ipc;
using StykkerFirewall.Core.Rules;
using static StykkerFirewall.Tests.TestData;

namespace StykkerFirewall.Tests;

public class Audit5157ParserTests
{
    private const string Sample = """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Microsoft-Windows-Security-Auditing" />
            <EventID>5157</EventID>
            <TimeCreated SystemTime="2026-11-16T12:00:01.1234567Z" />
          </System>
          <EventData>
            <Data Name="ProcessID">4242</Data>
            <Data Name="Application">\device\harddiskvolume3\program files\example\browser.exe</Data>
            <Data Name="Direction">%%14593</Data>
            <Data Name="SourceAddress">192.0.2.2</Data>
            <Data Name="SourcePort">50123</Data>
            <Data Name="DestAddress">192.0.2.10</Data>
            <Data Name="DestPort">443</Data>
            <Data Name="Protocol">6</Data>
            <Data Name="FilterRTID">0</Data>
          </EventData>
        </Event>
        """;

    private static readonly DevicePaths Map = new(new Dictionary<string, string> { [@"\Device\HarddiskVolume3"] = "C:" });

    [Fact]
    public void Parses_outbound_event_and_resolves_device_path()
    {
        var a = Audit5157Parser.Parse(Sample, Map.Resolve)!;
        Assert.Equal(@"C:\program files\example\browser.exe", a.AppPath);
        Assert.Equal(TrafficDirection.Outbound, a.Direction);
        Assert.Equal(4242, a.ProcessId);
        Assert.Equal(IpProtocol.Tcp, a.Protocol);
        Assert.Equal("192.0.2.2", a.LocalAddress);
        Assert.Equal(50123, a.LocalPort);
        Assert.Equal("192.0.2.10", a.RemoteAddress);
        Assert.Equal(443, a.RemotePort);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 12, 0, 1, TimeSpan.Zero), a.Time.AddTicks(-1234567));
    }

    [Fact]
    public void Inbound_direction_code()
    {
        var a = Audit5157Parser.Parse(Sample.Replace("%%14593", "%%14592"), Map.Resolve)!;
        Assert.Equal(TrafficDirection.Inbound, a.Direction);
    }

    [Fact]
    public void Garbage_and_unknown_direction_give_null()
    {
        Assert.Null(Audit5157Parser.Parse("not xml", Map.Resolve));
        Assert.Null(Audit5157Parser.Parse(Sample.Replace("%%14593", "%%99999"), Map.Resolve));
    }

    [Fact]
    public void Device_path_prefix_must_end_at_a_separator()
    {
        Assert.Equal(@"\device\harddiskvolume30\x.exe", Map.Resolve(@"\device\harddiskvolume30\x.exe"));
        Assert.Equal("System", Map.Resolve("System"));
    }
}

public class WindowsFirewallBackendTests
{
    [Fact]
    public void Rule_id_round_trips_through_description_and_name_is_unique()
    {
        var rule = DecisionMapper.ToRule(Out(), PromptDecision.AllowApp, Now)!;
        Assert.Equal(rule.Id, WindowsFirewallBackend.ParseId("stykker-id:" + rule.Id.ToString("D")));
        Assert.Null(WindowsFirewallBackend.ParseId("something else"));
        Assert.Null(WindowsFirewallBackend.ParseId(null));
        Assert.Contains(rule.Id.ToString("N")[..8], WindowsFirewallBackend.RuleName(rule));
        Assert.StartsWith("Stykker: browser.exe (allow, out)", WindowsFirewallBackend.RuleName(rule));
    }

    [Fact]
    public void Audit_backup_value_is_read_from_last_column()
    {
        string[] lines =
        [
            "Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value",
            "PC,System,Filterplattformverbindung,{0CCE9226-69AE-11D9-BED3-505054503030},Fehler,,2",
            "PC,System,Anmelden,{0CCE9215-69AE-11D9-BED3-505054503030},Erfolg,,1",
        ];
        Assert.Equal(2, AuditPolicy.ParseBackup(lines, AuditPolicy.FilteringPlatformConnection));
        Assert.Null(AuditPolicy.ParseBackup(lines[..1], AuditPolicy.FilteringPlatformConnection));
    }
}

public class SimulatorAndCatalogTests
{
    [Fact]
    public async Task Simulator_reports_blocked_attempts_and_respects_allow_rules()
    {
        await using var sim = new SimulatedBackend(interval: TimeSpan.FromMilliseconds(1), seed: 7);
        await sim.SetModeAsync(ProtectionMode.Ask, default);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var seen = new List<ConnectionAttempt>();
        await foreach (var a in sim.WatchBlockedAsync(cts.Token))
        {
            seen.Add(a);
            if (seen.Count == 1)
                await sim.ApplyRuleAsync(DecisionMapper.ToRule(a, PromptDecision.AllowApp, a.Time)! with { Created = Now }, default);
            if (seen.Count == 20) break;
        }

        var first = seen[0];
        Assert.DoesNotContain(seen.Skip(1), a => AppPaths.Same(a.AppPath, first.AppPath) && a.Direction == first.Direction);
    }

    [Fact]
    public void Catalog_creates_by_id_and_rejects_unknown()
    {
        var catalog = new BackendCatalog().Add(SimulatedBackend.BackendId, () => new SimulatedBackend());
        Assert.Equal(SimulatedBackend.BackendId, catalog.Create("SIMULATOR").Id);
        Assert.Throws<ArgumentException>(() => catalog.Create("nope"));
    }
}

public class IpcTests
{
    [Fact]
    public void Messages_round_trip()
    {
        var prompt = new PendingPrompt(Guid.NewGuid(), Out(), 2);
        IpcMessage[] messages =
        [
            new GetSnapshot(),
            new Decide(Guid.NewGuid(), PromptDecision.AllowTarget),
            new SetMode(ProtectionMode.Ask),
            new ConfirmMode(),
            new PromptRaised(prompt),
            new ErrorMessage("x"),
        ];
        foreach (var m in messages)
        {
            var line = IpcChannel.Encode(m);
            Assert.DoesNotContain('\n', line);
            var back = IpcChannel.Decode(line);
            Assert.Equal(m.GetType(), back!.GetType());
        }

        var decoded = (PromptRaised)IpcChannel.Decode(IpcChannel.Encode(new PromptRaised(prompt)))!;
        Assert.Equal(prompt, decoded.Prompt);

        var id = Guid.NewGuid();
        var late = Assert.IsType<Decide>(IpcChannel.Decode($$"""{"promptId":"{{id}}","decision":"BlockOnce","type":"decide"}"""));
        Assert.Equal(id, late.PromptId);
        Assert.Equal(PromptDecision.BlockOnce, late.Decision);
    }

    [Fact]
    public async Task Channel_sends_and_receives_over_a_pipe()
    {
        var name = "stykker-fw-test-" + Guid.NewGuid().ToString("N");
        await using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 65536, 65536);
        var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(5000));

        await using var a = new IpcChannel(server);
        await using var b = new IpcChannel(client);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receive = b.ReceiveAsync(cts.Token);
        await a.SendAsync(new SetMode(ProtectionMode.Monitor), cts.Token);
        var got = Assert.IsType<SetMode>(await receive);
        Assert.Equal(ProtectionMode.Monitor, got.Mode);
    }
}
