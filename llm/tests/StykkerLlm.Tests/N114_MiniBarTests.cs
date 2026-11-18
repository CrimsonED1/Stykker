using Stykker.Shared.Net;
using Stykker.Shared.Disk;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N114 – Hardware-Minibar: die acht Felder (MiniBar) und der Weg des Netzwerks durch Messschleife und Zustand (JSON). Keine echten Adapter: alles mit Testdaten.
[TestClass]
public class N114_MiniBarTests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 12, 0, 0);
    private const double Mb = 1024.0 * 1024.0;

    private static GpuSample Gpu(double util = 72, double used = 14.2, double total = 24, double temp = 58) =>
        new("Example GPU", util, used, total, 200, 300, temp, 40, 2000, 2900, 10000, 10500, 0);

    private static SystemSample Sys(double cpu = 35, double used = 25.9, double total = 32) => new(cpu, 16, used, total, 30, 40);

    private static DiskSample Disks(params VolumeRow[] volumes) =>
        new(T0, new[]
        {
            new PhysicalDiskRow(0, DiskKind.Nvme, "A", 10 * Mb, 20 * Mb, 30, 0.1),
            new PhysicalDiskRow(1, DiskKind.Hdd, "B", 0.6 * Mb, 3.8 * Mb, 55, 0.4),
        }, volumes);

    private static VolumeRow Vol(string letter, double total, double free) => new(letter, "", "NTFS", total, free, 0, DiskKind.Nvme, 0, 0, 0, 0);

    [TestMethod]
    public void BuildGivesEightCellsInTheOrderOfTheDraft()
    {
        var cells = MiniBar.Build(Gpu(), Sys(), Disks(Vol("C", 1e12, 3e11)), new NetSample(2 * Mb, 12 * Mb, 1e9 / 8));
        CollectionAssert.AreEqual(new[] { "vram", "gpu", "cpu", "ram", "disk", "io", "net", "temp" }, cells.Select(c => c.Key).ToArray());
        Assert.IsFalse(cells.Any(c => c.Missing));
    }

    [TestMethod]
    public void MissingSourcesShowADashAndAnEmptyBar()
    {
        var cells = MiniBar.Build(null, null, null, null);
        Assert.AreEqual(8, cells.Count);
        Assert.IsTrue(cells.All(c => c.Value == MiniBar.None && c.Fraction == null && c.Missing));
    }

    [TestMethod]
    public void VramGpuCpuAndRamReadLikeTheDraft()
    {
        Assert.AreEqual("14.2 / 24 GB", MiniBar.Vram(Gpu()).Value);
        Assert.AreEqual(14.2 / 24, MiniBar.Vram(Gpu()).Fraction!.Value, 1e-9);
        Assert.AreEqual("72%", MiniBar.GpuLoad(Gpu()).Value);
        Assert.AreEqual("35%", MiniBar.Cpu(Sys()).Value);
        Assert.AreEqual("6.1 GB free", MiniBar.Ram(Sys()).Value);
    }

    [TestMethod]
    public void AFullBarTurnsHot()
    {
        Assert.AreEqual(MiniLevel.Hot, MiniBar.Vram(Gpu(used: 23.5)).Level);
        Assert.AreEqual(MiniLevel.Normal, MiniBar.Vram(Gpu(used: 12)).Level);
        Assert.AreEqual(MiniLevel.Hot, MiniBar.Cpu(Sys(cpu: 99)).Level);
    }

    [TestMethod]
    public void TemperatureIsGreenThenYellowThenRed()
    {
        Assert.AreEqual(MiniLevel.Good, MiniBar.Temp(Gpu(temp: 58)).Level);
        Assert.AreEqual(MiniLevel.Warn, MiniBar.Temp(Gpu(temp: 75)).Level);
        Assert.AreEqual(MiniLevel.Hot, MiniBar.Temp(Gpu(temp: 85)).Level);
        Assert.AreEqual("58 °C", MiniBar.Temp(Gpu(temp: 58)).Value);
        Assert.IsTrue(MiniBar.Temp(Gpu(temp: 0)).Missing);   // kein Sensor
    }

    [TestMethod]
    public void DiskShowsTheFullestDrive()
    {
        var cell = MiniBar.Disk(Disks(Vol("C", 1e12, 5e11), Vol("D", 2e12, 2e11), Vol("E", 0, 0)));
        Assert.AreEqual("D: 90%", cell.Value);
        Assert.AreEqual(0.9, cell.Fraction!.Value, 1e-9);
        Assert.IsTrue(MiniBar.Disk(Disks()).Missing);
    }

    [TestMethod]
    public void DiskIoAddsUpThePhysicalDisksAndUsesTheBusiestForTheBar()
    {
        var cell = MiniBar.DiskIo(Disks());
        Assert.AreEqual("10.6 / 23.8 MB/s", cell.Value);
        Assert.AreEqual(0.55, cell.Fraction!.Value, 1e-9);
        // ohne Raten (erste Messung): ein Strich, kein erfundener Wert
        var first = new DiskSample(T0, new[] { new PhysicalDiskRow(0, DiskKind.Nvme, "A", null, null, null, null) }, Array.Empty<VolumeRow>());
        Assert.IsTrue(MiniBar.DiskIo(first).Missing);
    }

    [TestMethod]
    public void NetworkMeasuresAgainstTheLinkSpeedWhenKnown()
    {
        var known = MiniBar.Network(new NetSample(2.1 * Mb, 12.5 * Mb, 1e9 / 8));
        Assert.AreEqual("2.1 / 12.5 MB/s", known.Value);
        Assert.AreEqual(12.5 * Mb / (1e9 / 8), known.Fraction!.Value, 1e-9);
        StringAssert.Contains(known.Tip, "1 Gbit/s");
        Assert.IsNull(MiniBar.Network(new NetSample(1, 1, 0)).Fraction);   // Verbindung unbekannt: kein Balken
    }

    [TestMethod]
    public void PairKeepsBothRatesInOneUnit()
    {
        Assert.AreEqual("0.5 / 2.0 MB/s", MiniBar.Pair(0.5 * Mb, 2 * Mb));
        Assert.AreEqual("0 / 0 B/s", MiniBar.Pair(0, 0));
        Assert.AreEqual("300 / 12 B/s", MiniBar.Pair(300, 12));
        Assert.AreEqual("1.5 / 0.0 GB/s", MiniBar.Pair(1.5 * 1024 * Mb, 0));
        Assert.AreEqual("1.2 / <0.1 GB/s", MiniBar.Pair(1.2 * 1024 * Mb, 30 * Mb));   // kleines Schreiben sieht nicht wie keins aus
    }

    [TestMethod]
    public void LinkTextLooksLikeTheAdapter()
    {
        Assert.AreEqual("1 Gbit/s", MiniBar.LinkText(1e9 / 8));
        Assert.AreEqual("2.5 Gbit/s", MiniBar.LinkText(2.5e9 / 8));
        Assert.AreEqual("100 Mbit/s", MiniBar.LinkText(1e8 / 8));
    }

    // ── Messschleife und Zustand (die Rate selbst prüft Stykker.Shared.Tests/NetRatesTests) ──

    [TestMethod]
    public async Task NetworkReachesTheEngineAndTheState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-net-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(dir);
            var platform = new FakePlatform { Net = new NetSample(2 * Mb, 3 * Mb, 2.5e9 / 8) };
            using var engine = new MonitorEngine(platform, paths, AppSettings.Load(paths.SettingsFile), new HttpClient(new FakeHandler()));
            await engine.TickAsync();
            Assert.IsNotNull(engine.Net);

            var json = StateJson.WriteText(engine, null, null, 8078, DateTimeOffset.Now);
            StringAssert.Contains(json, "\"network\":{");
            var net = StateSnapshot.Parse(json).Net;
            Assert.IsNotNull(net);
            Assert.AreEqual(2 * Mb, net!.RxBytesPerSec, 0.01);
            Assert.AreEqual(3 * Mb, net.TxBytesPerSec, 0.01);
            Assert.AreEqual(2.5e9 / 8, net.LinkBytesPerSec, 0.01);

            platform.Net = null;   // Plattform ohne Netzwerkzähler: der Zustand sagt null, nicht 0
            using var engine2 = new MonitorEngine(platform, new AppPaths(dir + "-2"), AppSettings.Load(paths.SettingsFile), new HttpClient(new FakeHandler()));
            await engine2.TickAsync();
            Assert.IsNull(StateSnapshot.Parse(StateJson.WriteText(engine2, null, null, 8078, DateTimeOffset.Now)).Net);
        }
        finally
        {
            foreach (var d in new[] { dir, dir + "-2" })
                try { Directory.Delete(d, true); } catch { }
        }
    }
}
