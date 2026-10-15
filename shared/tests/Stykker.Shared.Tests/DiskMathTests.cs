using Stykker.Shared.Disk;

namespace Stykker.Shared.Tests;

// Die Umrechnung der Laufwerkswerte mit Testdaten – keine echten Laufwerke vorausgesetzt.
public class DiskMathTests
{
    private static readonly DateTime At = new(2026, 10, 15, 12, 0, 0);

    [Theory]
    [InlineData(17, null, DiskKind.Nvme)]        // NVMe sagt der Bus, die Suchzeit spielt keine Rolle
    [InlineData(17, true, DiskKind.Nvme)]
    [InlineData(7, false, DiskKind.Usb)]         // USB-Gehäuse gelten als USB, egal ob SSD oder HDD darin steckt
    [InlineData(11, true, DiskKind.Hdd)]         // SATA mit Suchzeit: HDD
    [InlineData(11, false, DiskKind.Ssd)]        // SATA ohne Suchzeit: SSD
    [InlineData(0, null, DiskKind.Unknown)]      // weder Bus noch Suchzeit bekannt: unknown, nicht geraten
    public void TheKindComesFromTheBusAndTheSeekPenalty(int bus, bool? seek, DiskKind expected) =>
        Assert.Equal(expected, DiskMath.Classify(bus, seek));

    [Theory]
    [InlineData(DiskKind.Nvme, "NVMe")]
    [InlineData(DiskKind.Ssd, "SSD")]
    [InlineData(DiskKind.Hdd, "HDD")]
    [InlineData(DiskKind.Usb, "USB")]
    [InlineData(DiskKind.Unknown, "unknown")]
    public void TheLabelIsTheShortName(DiskKind kind, string expected) => Assert.Equal(expected, DiskKinds.Label(kind));

    [Theory]
    [InlineData("0 C: D:", 0)]
    [InlineData("12 E:", 12)]
    [InlineData("3", 3)]
    [InlineData("_Total", null)]
    [InlineData("", null)]
    [InlineData("-1 C:", null)]
    public void ThePhysicalNumberIsTheLeadingNumber(string instance, int? expected) =>
        Assert.Equal(expected, DiskMath.PhysicalNumber(instance));

    [Theory]
    [InlineData("c:", "C")]
    [InlineData("D:", "D")]
    [InlineData("_Total", null)]
    [InlineData("HarddiskVolume3", null)]
    [InlineData("C:\\", null)]
    public void OnlyDriveLettersCountAsVolumes(string instance, string? expected) =>
        Assert.Equal(expected, DiskMath.VolumeLetter(instance));

    [Theory]
    [InlineData("chrome#3", "chrome")]
    [InlineData("llama-server", "llama-server")]
    [InlineData("svc#x", "svc#x")]          // kein Zähler: der Name bleibt, wie er ist
    [InlineData("a#", "a#")]
    public void TheSuffixOfTheSameNameIsRemoved(string instance, string expected) =>
        Assert.Equal(expected, DiskMath.ProcessName(instance));

    [Fact]
    public void ProcessRowsJoinByInstanceAndSortByTraffic()
    {
        var pid = new Dictionary<string, int> { ["llama#2"] = 40, ["chrome"] = 77, ["quiet"] = 9, ["_Total"] = 0, ["Idle"] = 0 };
        var read = new Dictionary<string, double> { ["llama#2"] = 1_200_000_000, ["chrome"] = 10_000_000, ["quiet"] = 0, ["Idle"] = 5 };
        var write = new Dictionary<string, double> { ["llama#2"] = 0, ["chrome"] = 20_000_000, ["quiet"] = 0, ["_Total"] = 900 };

        var rows = DiskMath.ProcessRows(pid, read, write, 10);

        Assert.Equal(2, rows.Count);                 // „quiet“ ohne Bewegung, „_Total“ und Leerlauf fallen weg
        Assert.Equal(new ProcessIoRow(40, "llama", 1_200_000_000, 0), rows[0]);
        Assert.Equal(new ProcessIoRow(77, "chrome", 10_000_000, 20_000_000), rows[1]);
    }

    [Fact]
    public void ProcessRowsKeepOnlyTheLargestCount()
    {
        var pid = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2, ["c"] = 3 };
        var read = new Dictionary<string, double> { ["a"] = 1, ["b"] = 3, ["c"] = 2 };
        var write = new Dictionary<string, double>();

        var rows = DiskMath.ProcessRows(pid, read, write, 2);

        Assert.Equal(new[] { 2, 3 }, rows.Select(r => r.Pid).ToArray());
        Assert.Empty(DiskMath.ProcessRows(pid, read, write, 0));
    }

    [Fact]
    public void ProcessRowsIgnoreNegativeAndBrokenValues()
    {
        var pid = new Dictionary<string, int> { ["a"] = 5 };
        var read = new Dictionary<string, double> { ["a"] = double.NaN };
        var write = new Dictionary<string, double> { ["a"] = -4 };

        Assert.Empty(DiskMath.ProcessRows(pid, read, write, 5));
    }

    [Fact]
    public void FirstSampleHasTheDisksAndVolumesButNoRates()
    {
        var physical = new Dictionary<string, double[]> { ["0 C:"] = new[] { 1.0, 2, 3, 4 }, ["_Total"] = new[] { 1.0, 2, 3, 4 } };
        var logical = new Dictionary<string, double[]> { ["C:"] = new[] { 1.0, 2, 3, 4 } };
        var volumes = new List<DiskMath.VolumeFact> { new("C", "System", "NTFS", 1000, 400, 0) };
        var identity = new Dictionary<int, (DiskKind Kind, string Model)> { [0] = (DiskKind.Nvme, "Example NVMe 1") };

        var sample = DiskMath.BuildSample(At, physical, logical, volumes, identity, rates: false);

        var disk = Assert.Single(sample.Disks);
        Assert.Equal(new PhysicalDiskRow(0, DiskKind.Nvme, "Example NVMe 1", null, null, null, null), disk);
        var volume = Assert.Single(sample.Volumes);
        Assert.Null(volume.ReadBytesPerSec);
        Assert.Null(volume.BusyPercent);
        Assert.Equal(600, volume.UsedBytes);
    }

    [Fact]
    public void RatesComeFromTheCountersOncePastTheFirstSample()
    {
        var physical = new Dictionary<string, double[]> { ["1 D:"] = new[] { 300e6, 50e6, 12.5, 0.3 } };
        var logical = new Dictionary<string, double[]>
        {
            ["D:"] = new[] { 300e6, 50e6, 130.0, 0.3 },   // über 100 %: wird begrenzt
            ["_Total"] = new[] { 9.0, 9, 9, 9 },
        };
        var volumes = new List<DiskMath.VolumeFact> { new("D", "Data", "exFAT", 4e12, 1e12, 1) };
        var identity = new Dictionary<int, (DiskKind Kind, string Model)> { [1] = (DiskKind.Hdd, "") };

        var sample = DiskMath.BuildSample(At, physical, logical, volumes, identity, rates: true);

        var disk = Assert.Single(sample.Disks);
        Assert.Equal(DiskKind.Hdd, disk.Kind);
        Assert.Equal(300e6, disk.ReadBytesPerSec);
        Assert.Equal(12.5, disk.BusyPercent);
        var volume = Assert.Single(sample.Volumes);
        Assert.Equal(DiskKind.Hdd, volume.Kind);
        Assert.Equal(300e6, volume.ReadBytesPerSec);
        Assert.Equal(50e6, volume.WriteBytesPerSec);
        Assert.Equal(100, volume.BusyPercent);
        Assert.Equal(0.3, volume.QueueLength);
        Assert.Equal(3e12, volume.UsedBytes);
    }

    [Fact]
    public void AVolumeWithoutItsDiskNumberIsUnknownAndHasNoDiskRow()
    {
        var volumes = new List<DiskMath.VolumeFact> { new("E", "", "FAT32", 8e9, 2e9, null) };
        var identity = new Dictionary<int, (DiskKind Kind, string Model)> { [0] = (DiskKind.Ssd, "") };

        var sample = DiskMath.BuildSample(At, new Dictionary<string, double[]>(), new Dictionary<string, double[]>(), volumes, identity, rates: true);

        Assert.Empty(sample.Disks);
        var volume = Assert.Single(sample.Volumes);
        Assert.Equal(DiskKind.Unknown, volume.Kind);
        Assert.Null(volume.DiskNumber);
        Assert.Null(volume.ReadBytesPerSec);   // kein Zähler für E: – null, nicht 0
    }

    [Fact]
    public void ADiskWithoutIdentityIsUnknownAndStillListed()
    {
        var physical = new Dictionary<string, double[]> { ["2 F:"] = new[] { 1.0, 1, 1, 1 } };

        var sample = DiskMath.BuildSample(At, physical, new Dictionary<string, double[]>(), new List<DiskMath.VolumeFact>(),
            new Dictionary<int, (DiskKind Kind, string Model)>(), rates: true);

        var disk = Assert.Single(sample.Disks);
        Assert.Equal(2, disk.Number);
        Assert.Equal(DiskKind.Unknown, disk.Kind);
        Assert.Equal("", disk.Model);
    }

    [Fact]
    public void ADiskNamedByAVolumeIsListedEvenBeforeTheCountersHaveNames()
    {
        var volumes = new List<DiskMath.VolumeFact> { new("C", "", "NTFS", 1e9, 5e8, 4) };
        var identity = new Dictionary<int, (DiskKind Kind, string Model)> { [4] = (DiskKind.Nvme, "Example") };

        var sample = DiskMath.BuildSample(At, new Dictionary<string, double[]>(), new Dictionary<string, double[]>(), volumes, identity, rates: false);

        var disk = Assert.Single(sample.Disks);
        Assert.Equal(4, disk.Number);
        Assert.Equal(DiskKind.Nvme, disk.Kind);
        Assert.Null(disk.BusyPercent);
    }

    [Fact]
    public void NegativeOrNaNRatesBecomeUnknown()
    {
        var physical = new Dictionary<string, double[]> { ["0"] = new[] { double.NaN, -1.0, 5.0, double.PositiveInfinity } };

        var disk = Assert.Single(DiskMath.BuildSample(At, physical, new Dictionary<string, double[]>(), new List<DiskMath.VolumeFact>(),
            new Dictionary<int, (DiskKind Kind, string Model)>(), rates: true).Disks);

        Assert.Null(disk.ReadBytesPerSec);
        Assert.Null(disk.WriteBytesPerSec);
        Assert.Equal(5.0, disk.BusyPercent);
        Assert.Null(disk.QueueLength);
    }

    [Fact]
    public void TheVolumeOfAnEmptyListIsNotInvented()
    {
        var sample = DiskMath.BuildSample(At, new Dictionary<string, double[]>(), new Dictionary<string, double[]>(),
            new List<DiskMath.VolumeFact>(), new Dictionary<int, (DiskKind Kind, string Model)>(), rates: true);

        Assert.Empty(sample.Disks);
        Assert.Empty(sample.Volumes);
        Assert.Equal(At, sample.At);
    }
}
