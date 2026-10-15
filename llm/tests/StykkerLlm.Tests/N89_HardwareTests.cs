using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N89 – Hardware-Bausteine: der Verlauf der GPU (Gerätedetails) und die Auslastung je Engine aus einer Lesung.
// Keine echte Grafikkarte: der Simulator und Testdaten.
[TestClass]
public class N89_HardwareTests
{
    private static GpuSample Gpu(double util, double used) => new("Example GPU", util, used, 24, 200, 300, 60, 40, 2500, 3000, 13000, 14000, 0);

    [TestMethod]
    public void GpuHistoryKeepsTheNewestPointsOldestFirst()
    {
        var h = new GpuHistory();
        for (int i = 0; i < GpuHistory.Length + 25; i++) h.Add(Gpu(i, 1));

        var points = h.Points();
        Assert.AreEqual(GpuHistory.Length, points.Count);
        Assert.AreEqual(25, points[0].Util, "der älteste Punkt ist der 26.");
        Assert.AreEqual(GpuHistory.Length + 24, points[^1].Util, "der neueste steht zuletzt");
    }

    [TestMethod]
    public void GpuHistoryCopiesTheFieldsOfTheReading()
    {
        var h = new GpuHistory();
        h.Add(new GpuSample("g", 42, 6.5, 16, 150, 300, 71, 30, 0, 0, 0, 0, 0));
        var p = h.Points().Single();
        Assert.AreEqual(42, p.Util);
        Assert.AreEqual(6.5, p.MemUsedGb);
        Assert.AreEqual(150, p.PowerW);
        Assert.AreEqual(71, p.TempC);
    }

    [TestMethod]
    public void TheSimulatorGivesEnginesAndProcessesInOneReading()
    {
        var sim = new SimPlatform(new SimWorld(seed: 3));

        var reading = sim.ReadGpuUtil();

        Assert.IsNotNull(reading);
        CollectionAssert.AreEqual(new[] { "3D", "Compute", "Copy", "Video decode" }, reading!.Engines.Select(e => e.Engine).ToArray());
        Assert.IsTrue(reading.Engines.All(e => e.Percent >= 0 && e.Percent <= 100));
        var procs = reading.Processes.Select(p => p.Percent).ToArray();
        CollectionAssert.AreEqual(procs.OrderByDescending(x => x).ToArray(), procs, "größte zuerst");
    }

    [TestMethod]
    public void WithoutAGpuThereIsNoReading()
    {
        var sim = new SimPlatform(new SimWorld(seed: 3) { ShowGpu = false });
        Assert.IsNull(sim.ReadGpuUtil());
    }
}
