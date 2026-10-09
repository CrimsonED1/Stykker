using Stykker.Shared.Sampling;

namespace Stykker.Shared.Tests;

public class ViewerLoopTests
{
    private sealed record Reading(int Number);

    private sealed class CountingSampler : ISampler<Reading>
    {
        public int Resumes { get; private set; }
        public int Samples { get; private set; }
        public void Resume() => Resumes++;
        public Reading Sample() => new(++Samples);
    }

    [Fact]
    public void TheFirstViewerStartsTheLoopAndAFastSecondAskReusesTheReading()
    {
        var sampler = new CountingSampler();
        using var loop = new ViewerLoop<Reading>(sampler);

        var first = loop.Snapshot();
        var second = loop.Snapshot();

        Assert.Equal(1, sampler.Resumes);
        Assert.Equal(1, sampler.Samples);   // der Takt (1 s) ist noch nicht gekommen
        Assert.Same(first, second);
    }

    [Fact]
    public async Task TheLoopStopsWhenNobodyAsksAndResumesWithTheNextViewer()
    {
        var sampler = new CountingSampler();
        using var loop = new ViewerLoop<Reading>(sampler, TimeSpan.FromMilliseconds(100));

        loop.Snapshot();
        await Task.Delay(TimeSpan.FromMilliseconds(2500));   // der Takt sieht niemanden und hält an
        int samplesAtStop = sampler.Samples;
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        Assert.Equal(samplesAtStop, sampler.Samples);

        loop.Snapshot();
        Assert.Equal(2, sampler.Resumes);
    }

    [Fact]
    public async Task TheLoopKeepsSamplingWhileSomebodyWatches()
    {
        var sampler = new CountingSampler();
        using var loop = new ViewerLoop<Reading>(sampler, TimeSpan.FromSeconds(5));

        loop.Snapshot();
        for (int i = 0; i < 11; i++)
        {
            await Task.Delay(200);
            loop.Snapshot();
        }

        Assert.True(sampler.Samples >= 2, $"samples: {sampler.Samples}");
        Assert.Equal(1, sampler.Resumes);
    }
}
