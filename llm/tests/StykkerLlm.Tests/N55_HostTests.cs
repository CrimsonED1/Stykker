using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// StykkerHost (docs/plan-hosts-gateway.md, P1): Datenordner, Tooltip, Statuszeilen und Protokoll-Änderungen.
[TestClass]
public class N55_HostTests
{
    private static async Task<SimHost> Sim()
    {
        var sim = new SimHost(SimServerSpec.Defaults());
        sim.World.Start();
        for (int i = 0; i < 3; i++) { await sim.Engine.TickAsync(); await Task.Delay(150); }
        return sim;
    }

    [TestMethod]
    public void Host_HasItsOwnDataFolder_NextToTheServer()
    {
        var host = HostStatus.DefaultPaths().Root;
        StringAssert.EndsWith(host, Path.Combine("StykkerLLM", "host"));
        Assert.AreNotEqual(SingleInstance.NameFor(AppPaths.Default().Root), SingleInstance.NameFor(host), "eigene Sperre, läuft neben dem Server");
    }

    [TestMethod]
    public async Task Tip_IsShort_AndCountsRunningServers()
    {
        using var sim = await Sim();
        var tip = HostStatus.Tip(sim.Engine.Servers, sim.Engine.Gpu);
        Assert.IsTrue(tip.Length <= 63, tip);
        StringAssert.StartsWith(tip, Strings.HostName);
        StringAssert.Contains(tip, Strings.HostRunning(sim.Engine.Servers.Count(s => s.Online)));
        StringAssert.Contains(tip, "GB");
    }

    [TestMethod]
    public async Task Lines_ShowGpuAndEveryServer()
    {
        using var sim = await Sim();
        var lines = HostStatus.Lines(sim.Engine.Servers, sim.Engine.Gpu);
        StringAssert.Contains(lines[0], "VRAM");
        Assert.IsTrue(lines.Any(l => l.Contains("qwen3-8b") && l.Contains("t/s")));
        CollectionAssert.AreEqual(new[] { Strings.HostNoGpu, Strings.RunningEmpty }, HostStatus.Lines(Array.Empty<ServerWatcher>(), null).ToList());
    }

    [TestMethod]
    public void Change_NamesNewAndGoneServers_OrNothing()
    {
        Assert.AreEqual("", HostStatus.Change(new[] { "a:1" }, new[] { "a:1" }));
        Assert.AreEqual("servers: +b:2, -a:1", HostStatus.Change(new[] { "a:1" }, new[] { "b:2" }));
    }

    [TestMethod]
    public void Autostart_QuotesThePath()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows only"); return; }
        Assert.AreEqual("\"C:\\Program Files\\Stykker\\StykkerHost.exe\"",
            StykkerLlm.Platform.Windows.Autostart.Command(@"C:\Program Files\Stykker\StykkerHost.exe"));
    }
}
