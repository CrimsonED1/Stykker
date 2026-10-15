using System.Text.Json;
using Stykker.Shared.Disk;
using StykkerSys.Core;

namespace StykkerSys.Tests;

// Die Laufwerkseite: die Messung mit einer festen Sonde, ohne echte Laufwerke
public class DiskSamplerTests
{
    // Eine Sonde mit festen Zahlen, die zählt, wie oft die Messung sie fragt
    private sealed class FakeDiskProbe : IDiskProbe
    {
        public int DiskReads, ProcessReads, Resets;
        public bool Answer = true;

        public DiskSample? ReadDisks()
        {
            DiskReads++;
            if (!Answer) return null;
            return new DiskSample(DateTime.Now,
                new[] { new PhysicalDiskRow(0, DiskKind.Nvme, "Example NVMe", 1e6, 2e6, 3, 0.1) },
                new[] { new VolumeRow("C", "System", "NTFS", 1e12, 4e11, 0, DiskKind.Nvme, 1e6, 2e6, 3, 0.1) });
        }

        public IReadOnlyList<ProcessIoRow>? ReadProcesses(int count)
        {
            ProcessReads++;
            return Answer ? new[] { new ProcessIoRow(4242, "llama-server", 3e8, 0) } : null;
        }

        public void Reset() => Resets++;
        public void Dispose() { }
    }

    [Fact]
    public void ASampleHasTheDrivesAndTheProgramsWithoutNotes()
    {
        var snapshot = new DiskSampler(new FakeDiskProbe()).Sample();

        Assert.NotNull(snapshot.Disks);
        Assert.Equal("C", Assert.Single(snapshot.Disks!.Volumes).Letter);
        Assert.Equal(4242, Assert.Single(snapshot.Processes).Pid);
        Assert.Empty(snapshot.Notes);
    }

    [Fact]
    public void MissingCountersAreNamedInTheNotesNotInvented()
    {
        var snapshot = new DiskSampler(new FakeDiskProbe { Answer = false }).Sample();

        Assert.Null(snapshot.Disks);
        Assert.Empty(snapshot.Processes);
        Assert.Equal(2, snapshot.Notes.Length);
        Assert.Contains(snapshot.Notes, n => n.StartsWith("No disk values", StringComparison.Ordinal));
        Assert.Contains(snapshot.Notes, n => n.StartsWith("No values per program", StringComparison.Ordinal));
    }

    [Fact]
    public void TheProgramsAreReadOnlyEveryFifthSample()
    {
        var probe = new FakeDiskProbe();
        var sampler = new DiskSampler(probe);

        for (int i = 0; i < 6; i++) sampler.Sample();

        Assert.Equal(6, probe.DiskReads);
        Assert.Equal(2, probe.ProcessReads);   // Takt 0 und Takt 5
    }

    [Fact]
    public void ResumeStartsAgainWithTheBaseline()
    {
        var probe = new FakeDiskProbe();
        var sampler = new DiskSampler(probe);
        sampler.Resume();
        sampler.Sample();

        Assert.Equal(1, probe.Resets);
        Assert.Equal(1, probe.ProcessReads);   // nach dem Neubeginn gleich wieder die Prozesse
    }

    [Fact]
    public void TheBasicProbeAnswersNothingAndTheSampleSaysSo()
    {
        var snapshot = new DiskSampler(new BasicDiskProbe()).Sample();

        Assert.Null(snapshot.Disks);
        Assert.Empty(snapshot.Processes);
        Assert.Equal(2, snapshot.Notes.Length);
    }

    [Fact]
    public void TheKindIsWrittenAsItsNameInJson()
    {
        var snapshot = new DiskSampler(new FakeDiskProbe()).Sample();

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"kind\":\"Nvme\"", json);
        Assert.Contains("\"readBytesPerSec\":1000000", json);
    }
}
