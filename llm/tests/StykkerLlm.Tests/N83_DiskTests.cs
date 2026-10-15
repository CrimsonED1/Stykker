using Stykker.Shared.Disk;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N83 – Laufwerke: die Karte „Disks“ und der Hinweis zum Modellserver, die Formate, der Verlauf, der Zustand (JSON) und die
// simulierten Laufwerke. Keine echten Laufwerke: alles mit Testdaten.
[TestClass]
public class N83_DiskTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 12, 0, 0);
    private const double Mb = 1024.0 * 1024.0;

    private static VolumeRow Vol(string letter, DiskKind kind, double? read, double? write = 0, int? disk = 0) =>
        new(letter, "", "NTFS", 1e12, 4e11, disk, kind, read, write, 5, 0.1);

    private static DiskSample Sample(params VolumeRow[] volumes) =>
        new(T0, new[] { new PhysicalDiskRow(0, DiskKind.Nvme, "Example NVMe", 1, 1, 1, 0.1) }, volumes);

    // ── Laufwerk aus dem Modellpfad ──

    [TestMethod]
    public void DriveOfTakesTheLetterOfALocalPathOnly()
    {
        Assert.AreEqual("D", DiskHints.DriveOf(@"d:\models\x.gguf"));
        Assert.AreEqual("C", DiskHints.DriveOf(@"C:\x"));
        Assert.IsNull(DiskHints.DriveOf(@"\\server\share\x.gguf"));   // Netzpfad
        Assert.IsNull(DiskHints.DriveOf(@"models\x.gguf"));           // relativ
        Assert.IsNull(DiskHints.DriveOf(""));
        Assert.IsNull(DiskHints.DriveOf(null));
    }

    [TestMethod]
    public void DriveForPrefersTheModelPath()
    {
        var volumes = new[] { Vol("C", DiskKind.Nvme, 5e6), Vol("D", DiskKind.Hdd, 3e8) };
        Assert.AreEqual("C:", DiskHints.DriveFor(@"C:\models\m.gguf", 3e8, volumes));
    }

    [TestMethod]
    public void DriveForWithoutPathNamesOnlyTheVolumeThatCarriesTheRead()
    {
        var volumes = new[] { Vol("C", DiskKind.Nvme, 5e6), Vol("D", DiskKind.Hdd, 3e8) };
        Assert.AreEqual("D:", DiskHints.DriveFor(null, 3.1e8, volumes));
    }

    [TestMethod]
    public void DriveForStaysSilentWhenTwoVolumesShareTheRead()
    {
        var volumes = new[] { Vol("C", DiskKind.Nvme, 1.5e8), Vol("D", DiskKind.Hdd, 1.5e8) };
        Assert.IsNull(DiskHints.DriveFor(null, 3e8, volumes));
    }

    // ── Hinweise zur Karte ──

    [TestMethod]
    public void OnlyServersAboveTwoHundredMegabytesGetALine()
    {
        var processes = new[] { new ProcessIoRow(10, "llama-server", 250 * Mb, 0), new ProcessIoRow(11, "other", 100 * Mb, 0) };
        var servers = new[] { new ServerIo("llama", new[] { 10 }, @"D:\models\m.gguf"), new ServerIo("other", new[] { 11 }, null) };

        var lines = DiskHints.Lines(servers, processes, new[] { Vol("D", DiskKind.Hdd, 250 * Mb) });

        Assert.AreEqual(1, lines.Count);
        Assert.AreEqual("llama reads 250 MB/s from D:", lines[0]);
    }

    [TestMethod]
    public void EnginesAddUpAndTheBiggestReaderComesFirst()
    {
        var processes = new[]
        {
            new ProcessIoRow(20, "engine-a", 150 * Mb, 0), new ProcessIoRow(21, "engine-b", 120 * Mb, 0),
            new ProcessIoRow(30, "big", 1024 * Mb, 0),
        };
        var servers = new[] { new ServerIo("lm", new[] { 20, 21 }, null), new ServerIo("big", new[] { 30 }, null) };
        var volumes = new[] { Vol("C", DiskKind.Nvme, 270 * Mb) };

        var lines = DiskHints.Lines(servers, processes, volumes);

        Assert.AreEqual(2, lines.Count);
        Assert.AreEqual("big reads 1.0 GB/s", lines[0]);                // ohne Pfad und ohne eindeutiges Laufwerk: kein Buchstabe
        Assert.AreEqual("lm reads 270 MB/s from C:", lines[1]);         // Lesen verteilt sich auf zwei PIDs, ein Volume trägt es
        Assert.AreEqual(1, DiskHints.Lines(servers, processes, volumes, max: 1).Count);
    }

    [TestMethod]
    public void NoProcessDataMeansNoLines()
    {
        var lines = DiskHints.Lines(new[] { new ServerIo("a", new[] { 1 }, null) }, Array.Empty<ProcessIoRow>(), Array.Empty<VolumeRow>());
        Assert.AreEqual(0, lines.Count);
    }

    // ── Formate ──

    [TestMethod]
    public void RatesAndSizesUseBinaryUnitsAndADashForNonsense()
    {
        Assert.AreEqual("0 B/s", DiskFormat.Rate(0));
        Assert.AreEqual("1.2 GB/s", DiskFormat.Rate(1.2 * 1024 * Mb));
        Assert.AreEqual("340 MB/s", DiskFormat.Rate(340 * Mb));
        Assert.AreEqual("12.0 KB/s", DiskFormat.Rate(12 * 1024));
        Assert.AreEqual("–", DiskFormat.Size(-1));
        Assert.AreEqual("–", DiskFormat.Size(double.NaN));
    }

    // ── Verlauf und Sparkline ──

    [TestMethod]
    public void SparklinePlacesTheNewestValueRightAndSkipsGaps()
    {
        Assert.AreEqual("0,70 200,35 300,0", Sparkline.Points(new double?[] { 0, null, 35, 70 }, 70));
        Assert.AreEqual("", Sparkline.Points(new double?[] { 1 }, 10));   // eine Messung reicht für keine Linie
        Assert.AreEqual("", Sparkline.Points(new double?[] { 1, 2 }, 0));
        Assert.AreEqual("0,0 300,0", Sparkline.Points(new double?[] { 140, 140 }, 70));   // über dem Maximum: oben begrenzt
    }

    [TestMethod]
    public void ScaleIsTheLargestValueOrTheFloor()
    {
        Assert.AreEqual(9, Sparkline.Scale(new double?[] { null, 5, 9 }, 4));
        Assert.AreEqual(4, Sparkline.Scale(new double?[] { 1 }, 4));
        Assert.AreEqual(4, Sparkline.Scale(Array.Empty<double?>(), 4));
    }

    [TestMethod]
    public void HistoryKeepsTheLastFiveMinutesPerVolume()
    {
        var history = new DiskHistory();
        for (int i = 0; i < DiskHistory.Length + 10; i++) history.Add(Sample(Vol("C", DiskKind.Nvme, i)));

        var points = history.Of("C");
        Assert.AreEqual(DiskHistory.Length, points.Count);
        Assert.AreEqual(10.0, points[0].Read);
        Assert.AreEqual(DiskHistory.Length + 9.0, points[^1].Read);
        Assert.AreEqual(0, history.Of("Z").Count);
        CollectionAssert.AreEqual(new[] { "C" }, history.Letters().ToArray());
    }

    // ── Zustand (JSON) und Plattform ──

    [TestMethod]
    public async Task TheTickFillsDisksHistoryAndTheStateJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-disk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(dir);
            var platform = new FakePlatform
            {
                Disks = Sample(Vol("C", DiskKind.Nvme, 1e6, 2e6)),
                DiskProcs = new List<ProcessIoRow> { new(4242, "llama-server", 3e8, 0) },
            };
            using var engine = new MonitorEngine(platform, paths, AppSettings.Load(paths.SettingsFile), new HttpClient(new FakeHandler()));

            await engine.TickAsync();
            for (int i = 0; i < 100 && engine.DiskProcesses.Count == 0; i++) await Task.Delay(20);   // die Prozesse kommen im Hintergrund

            Assert.IsNotNull(engine.Disks);
            Assert.AreEqual(1, engine.DiskHistory.Of("C").Count);
            Assert.AreEqual(4242, engine.DiskProcesses.Single().Pid);

            var json = StateJson.WriteText(engine, null, null, 8078, DateTimeOffset.Now);
            StringAssert.Contains(json, "\"disks\":{");
            var state = StateSnapshot.Parse(json);
            Assert.IsNotNull(state.Disks);
            var volume = state.Disks!.Volumes.Single();
            Assert.AreEqual("C", volume.Letter);
            Assert.AreEqual(DiskKind.Nvme, volume.Kind);
            Assert.AreEqual(1e6, volume.ReadBytesPerSec);
            Assert.AreEqual(2e6, volume.WriteBytesPerSec);
            Assert.AreEqual(0, volume.DiskNumber);
            Assert.AreEqual(DiskKind.Nvme, state.Disks.Disks.Single().Kind);
            Assert.AreEqual("llama-server", state.DiskProcesses.Single().Name);
            Assert.AreEqual(1, state.DiskHistoryByLetter["C"].Count);

            // Ohne Verlauf (Viewer-Gerät) bleiben die Punkte weg
            var lean = StateSnapshot.Parse(StateJson.Write(engine, null, null, 8078, DateTimeOffset.Now, withHistory: false, indented: false));
            Assert.AreEqual(0, lean.DiskHistoryByLetter.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void APlatformWithoutDisksLeavesTheBlockNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-disk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(dir);
            using var engine = new MonitorEngine(new FakePlatform(), paths, AppSettings.Load(paths.SettingsFile), new HttpClient(new FakeHandler()));
            Assert.IsNull(engine.Disks);
            StringAssert.Contains(StateJson.WriteText(engine, null, null, 8078, DateTimeOffset.Now), "\"disks\":null");
            Assert.IsNull(StateSnapshot.Parse(StateJson.WriteText(engine, null, null, 8078, DateTimeOffset.Now)).Disks);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void BasicPlatformHasNoDisks()
    {
        IPlatform basic = new BasicPlatform();
        Assert.IsNull(basic.ReadDisks());
        Assert.IsNull(basic.ReadDiskProcesses(5));
    }

    [TestMethod]
    public void TheSimulatorHasAnNvmeAndAHddWithVolumes()
    {
        var sim = new SimPlatform(new SimWorld(seed: 1));

        var sample = sim.ReadDisks();

        Assert.IsNotNull(sample);
        CollectionAssert.AreEqual(new[] { DiskKind.Nvme, DiskKind.Hdd }, sample!.Disks.Select(d => d.Kind).ToArray());
        CollectionAssert.AreEqual(new[] { "C", "D" }, sample.Volumes.Select(v => v.Letter).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1 }, sample.Volumes.Select(v => v.DiskNumber!.Value).ToArray());
        Assert.IsTrue(sample.Volumes.All(v => v.FreeBytes < v.TotalBytes));
    }

    [TestMethod]
    public void TheSimulatorsServerNameGoesWithTheHint()
    {
        var sim = new SimPlatform(new SimWorld(seed: 2));
        var rows = sim.ReadDiskProcesses(12);
        Assert.IsNotNull(rows);
        foreach (var r in rows!) Assert.IsTrue(r.Pid > 0 && r.Name.Length > 0);
    }
}
