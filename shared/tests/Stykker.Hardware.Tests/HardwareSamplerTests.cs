using Stykker.Hardware;
using Stykker.Hardware.Components;
using Stykker.Shared.Disk;
using Stykker.Shared.Gpu;
using Stykker.Shared.Net;
using Stykker.Shared.Windows;

namespace Stykker.Hardware.Tests;

// Die gemeinsame Hardware-Seite: Zusammensetzen der Messung, Verlauf, NVIDIA vor den Windows-Zählern, Zahlenformat.
public class HardwareSamplerTests
{
    private sealed class Probe : IHardwareProbe
    {
        public NvidiaGpu? Nvidia;
        public SystemLoad? ReadSystem() => new(25, 4, new[] { 10.0, 20, 30, 40 }, 8, 16, 10, 20);
        public GpuUtilSample? ReadGpuUtil() => new(Array.Empty<GpuProcRow>(), new[] { new GpuEngineRow("3D", 12) });
        public IReadOnlyList<GpuProcRow>? ReadGpuMemory() => Array.Empty<GpuProcRow>();
        public double? ReadGpuMemoryUsedMb() => 2048;
        public NvidiaGpu? ReadNvidia() => Nvidia;
        public void Dispose() { }
    }

    private sealed class Disks : IDiskProbe
    {
        public DiskSample? ReadDisks() => new(DateTime.Now, Array.Empty<PhysicalDiskRow>(),
            new[] { new VolumeRow("C", "System", "NTFS", 100, 40, 0, DiskKind.Ssd, 1000, 2000, 5, 0.1) });
        public IReadOnlyList<ProcessIoRow>? ReadProcesses(int count) => Array.Empty<ProcessIoRow>();
        public void Reset() { }
        public void Dispose() { }
    }

    private static NvidiaGpu Gpu() => new("RTX", 55, 6, 12, 120, 200, 61, 30, 1800, 2500, 7000, 7000, 0);

    [Fact]
    public void HistoryGrowsAndIsCapped()
    {
        var sampler = new HardwareSampler(new Probe(), new Disks());
        sampler.Resume();
        HardwareSnapshot last = null!;
        for (int i = 0; i < HardwareSampler.HistoryLength + 20; i++) last = sampler.Sample();
        Assert.Equal(HardwareSampler.HistoryLength, last.History.Count);
        Assert.Equal(1000, last.History[^1].Volumes["C"].Read);
        sampler.Resume();                                  // nach einer Pause beginnt der Verlauf neu
        Assert.Single(sampler.Sample().History);
    }

    [Fact]
    public void NvidiaWinsOverTheWindowsCounters()
    {
        var probe = new Probe();
        var sampler = new HardwareSampler(probe, new Disks());
        var without = sampler.Sample().History[^1];
        Assert.Equal(12, without.GpuPercent);              // die busiest engine der Windows-Zähler
        Assert.Equal(2, without.VramUsedGb);               // 2048 MB
        Assert.Null(without.TempC);

        probe.Nvidia = Gpu();
        var with = sampler.Sample();
        Assert.NotNull(with.Nvidia);
        Assert.Equal(55, with.History[^1].GpuPercent);
        Assert.Equal(6, with.History[^1].VramUsedGb);
        Assert.Equal(61, with.History[^1].TempC);
    }

    [Fact]
    public void NetworkTotalsLeaveOutTunnels()
    {
        var overview = new OverviewSnapshot(DateTime.Now, null, null, Array.Empty<ProgramLoad>(), Array.Empty<ProgramLoad>(), Array.Empty<string>());
        var disks = new DiskSnapshot(DateTime.Now, null, Array.Empty<ProcessIoRow>(), Array.Empty<string>());
        var net = new[]
        {
            new NetAdapterRow("1", "Ethernet", "", NetAdapterKind.Ethernet, 125e6, 100, 50),
            new NetAdapterRow("2", "WLAN", "", NetAdapterKind.Wifi, 50e6, 10, 5),
            new NetAdapterRow("3", "Tailscale", "", NetAdapterKind.Tunnel, 0, 80, 40),
        };
        var point = HardwareSampler.Point(overview, null, disks, net);
        Assert.Equal(110, point.NetRx);
        Assert.Equal(55, point.NetTx);
        Assert.Null(HardwareSampler.Point(overview, null, disks, null).NetRx);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    [InlineData(200L * 1024 * 1024 * 1024, "200 GB")]
    public void SizesUseBinaryUnits(double bytes, string text) => Assert.Equal(text, HwFormat.Size(bytes));

    [Fact]
    public void MissingValuesShowADash()
    {
        Assert.Equal("–", HwFormat.Rate(null));
        Assert.Equal("–", HwFormat.Pct(null));
        Assert.Equal("–", HwFormat.Gb(null));
        Assert.Equal("–", HwFormat.Mhz(0));
        Assert.Equal("50%", HwFormat.Css(.5));
        Assert.Equal("100%", HwFormat.Css(3));
    }

    [Fact]
    public void ASeedFillsTheHistoryAfterAPause()
    {
        var seed = Enumerable.Range(0, 400).Select(i => new HistoryPoint(DateTime.Now.AddSeconds(i - 400), null, null, i, null, null, null,
            null, null, new Dictionary<string, VolumeRate>())).ToArray();
        var sampler = new HardwareSampler(new Probe(), new Disks(), seed: () => seed);
        sampler.Resume();
        var s = sampler.Sample();
        Assert.Equal(HardwareSampler.HistoryLength, s.History.Count);   // gekürzt, der neue Punkt kommt hinten dazu
        Assert.Equal(399, s.History[^2].GpuPercent);
    }

    [Fact]
    public void AFailingSeedDoesNotStopTheMeasuring()
    {
        var sampler = new HardwareSampler(new Probe(), new Disks(), seed: () => throw new InvalidOperationException());
        sampler.Resume();
        Assert.Single(sampler.Sample().History);
    }

    [Fact]
    public void WithoutNetworkNoAdaptersAreRead()
    {
        var sampler = new HardwareSampler(new Probe(), new Disks(), network: false);
        sampler.Resume();
        sampler.Sample();
        Assert.Null(sampler.Sample().Network);
    }
}
