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

    // Eine Messung, die beim ersten Mal wirft, dann langsam ist; sie zählt, ob sie freigegeben wurde.
    private sealed class MoodySampler : ISampler<Reading>, IDisposable
    {
        public bool FailFirst = true;
        public int SlowMs;
        public int Samples, Disposes;
        public void Resume() { }
        public Reading Sample()
        {
            if (FailFirst) { FailFirst = false; throw new InvalidOperationException("counters not ready"); }
            if (SlowMs > 0) Thread.Sleep(SlowMs);
            return new(Interlocked.Increment(ref Samples));
        }
        public void Dispose() => Disposes++;
    }

    [Fact]
    public void AFailedFirstReadingDoesNotFreezeTheLoop()
    {
        var sampler = new MoodySampler();
        using var loop = new ViewerLoop<Reading>(sampler);
        Assert.Throws<InvalidOperationException>(() => loop.Snapshot());
        Assert.Equal(1, loop.Snapshot().Number);   // der nächste Abruf misst neu und startet die Schleife
    }

    [Fact]
    public async Task AskingDoesNotWaitForASlowReading()
    {
        var sampler = new MoodySampler { FailFirst = false };
        using var loop = new ViewerLoop<Reading>(sampler);
        loop.Snapshot();
        sampler.SlowMs = 1500;                      // die Takte messen ab jetzt langsam
        await Task.Delay(1200);                      // ein Takt hat begonnen
        var watch = System.Diagnostics.Stopwatch.StartNew();
        loop.Snapshot();
        Assert.True(watch.ElapsedMilliseconds < 300, $"waited {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void DisposeReleasesTheSamplerOnce()
    {
        var sampler = new MoodySampler { FailFirst = false };
        var loop = new ViewerLoop<Reading>(sampler);
        loop.Snapshot();
        loop.Dispose();
        loop.Dispose();
        Assert.Equal(1, sampler.Disposes);
    }
}
