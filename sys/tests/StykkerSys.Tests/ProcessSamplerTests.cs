using Stykker.Shared.Gpu;
using StykkerSys.Core;

namespace StykkerSys.Tests;

public class ProcessSamplerTests
{
    // Eine Sonde mit festen Zahlen: der Prozess dieses Tests bekommt Auslastung und Speicher, eine fremde PID nichts.
    private sealed class FakeProbe : IProcessProbe
    {
        public int MemoryReads;

        public GpuUtilSample? ReadGpuUtil() => new(
            new[] { new GpuProcRow(Environment.ProcessId, 42.5), new GpuProcRow(Environment.ProcessId, 10), new GpuProcRow(-1, 5) },
            new[] { new GpuEngineRow("3D", 60) });

        public IReadOnlyList<GpuProcRow>? ReadGpuMemory()
        {
            MemoryReads++;
            return new[] { new GpuProcRow(Environment.ProcessId, 512) };
        }

        public string? PathOf(int pid) => pid == Environment.ProcessId ? @"C:\test\this.exe" : null;
        public void Dispose() { }
    }

    [Fact]
    public void GpuValuesAreSummedPerProcessAndAttachedByPid()
    {
        var me = new ProcessSampler(new FakeProbe()).Sample().Processes.Single(p => p.Pid == Environment.ProcessId);

        Assert.Equal(52.5, me.GpuPercent);   // 42,5 + 10 über zwei Einträge
        Assert.Equal(512, me.VramMb);
        Assert.Equal(@"C:\test\this.exe", me.Path);
    }

    [Fact]
    public void OtherProcessesKeepEmptyGpuCells()
    {
        var snapshot = new ProcessSampler(new FakeProbe()).Sample();
        Assert.All(snapshot.Processes.Where(p => p.Pid != Environment.ProcessId),
            p => { Assert.Null(p.GpuPercent); Assert.Null(p.VramMb); });
    }

    [Fact]
    public void MemoryIsReadOnlyEveryThirdTick()
    {
        var probe = new FakeProbe();
        var sampler = new ProcessSampler(probe);
        for (int i = 0; i < 4; i++) sampler.Sample();
        Assert.Equal(2, probe.MemoryReads);   // Takt 0 und Takt 3
    }

    [Fact]
    public void ResumeStartsTheCpuBaselineOver()
    {
        var sampler = new ProcessSampler(new FakeProbe());
        sampler.Sample();
        sampler.Resume();
        var snapshot = sampler.Sample();
        Assert.All(snapshot.Processes, p => Assert.Equal(0.0, p.CpuPercent));
    }

    [Fact]
    public void WithoutCountersTheCellsStayEmptyAndANoteSaysWhy()
    {
        var snapshot = new ProcessSampler(new BasicProcessProbe()).Sample();
        Assert.All(snapshot.Processes, p => { Assert.Null(p.GpuPercent); Assert.Null(p.VramMb); Assert.Null(p.Path); });
        Assert.Contains(snapshot.Notes, n => n.Contains("GPU"));
    }

    [Fact]
    public void NoNoteWhileEveryProcessCanBeRead()
    {
        Assert.Null(ProcessSampler.UnreadableNote(0, 300));
    }

    [Fact]
    public void TheNoteCountsTheProcessesTheListLeavesOut()
    {
        var note = ProcessSampler.UnreadableNote(215, 346);
        Assert.NotNull(note);
        Assert.Contains("215 of 346", note);
        Assert.Contains("StykkerHUD", note);
    }
}
