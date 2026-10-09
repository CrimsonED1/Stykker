using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Benchmarks laufen auch im Simulator (für Vorführung und Doku-Bilder); ohne Kontextangabe der Seite nimmt der Dienst
// die Slot-Größe des Servers, sonst entfiele die Kontext-Leiter
[TestClass]
public class N65_SimBenchmarkTests
{
    [TestMethod, Timeout(60000)]
    public async Task Benchmark_RunsInTheSimulator_WithTheContextLadder()
    {
        var spec = new SimServerSpec { Kind = BackendKind.LlamaCpp, Name = "sim-b", Model = "m", Context = 8192, Slots = 1, TpsMin = 400, TpsMax = 500 };
        using var host = new SimHost(new List<SimServerSpec> { spec }, seed: 7, autoStep: true);
        host.World.Start();
        ServerWatcher? w = null;
        for (int i = 0; i < 20 && (w == null || !w.Online || w.Slots.Count == 0); i++)
        {
            await host.Engine.Registry.RefreshNowAsync();
            await host.Engine.TickAsync();
            w = host.Engine.Servers.FirstOrDefault(s => s.Name == "sim-b");
        }
        Assert.IsNotNull(w, "der simulierte Server wird erkannt");

        var options = new BenchOptions { Chat = true, Tool = false, Parallel = false, ContextSizes = new[] { 1024 }, ContextPerSlot = 0, GenTokens = 16 };
        var result = await new BenchmarkService().RunAsync(host.Engine, new LaunchCoordinator(host.Engine, new RemotePrompt()), w!.Key, options);
        Assert.IsNotNull(result);
        string steps = string.Join("; ", result.Steps.Select(s => $"{s.Kind}/{s.Name}:{s.Ok} {s.Note}"));
        Assert.IsTrue(result.Steps.Any(s => s.Kind == "chat" && s.Ok), steps);
        Assert.IsTrue(result.Steps.Any(s => s.Kind == "context" && s.Ok), "Kontext-Stufe ohne Angabe der Seite: " + steps);
        Assert.IsTrue(result.ContextPerSlot > 0);
    }
}
