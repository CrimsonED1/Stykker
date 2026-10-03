using Stykker.NanoCut.Demo.Services;

namespace Stykker.NanoCut.Tests;

/// <summary>The compute queue of the demo's server host: how many jobs run at once, and what a cancel does.</summary>
public class ComputeRunnerTests
{
    [Fact]
    public async Task NeverRunsMoreJobsAtOnceThanAllowed()
    {
        // The jobs hold their slot until the gate opens, so the count does not depend on timing or on how busy the
        // thread pool is: exactly two start, the other three wait.
        using var runner = new QueuedComputeRunner(2);
        using var gate = new ManualResetEventSlim();
        int started = 0;
        int Job()
        {
            Interlocked.Increment(ref started);
            gate.Wait();
            return 1;
        }

        var jobs = Enumerable.Range(0, 5).Select(_ => runner.Run(Job)).ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while ((Volatile.Read(ref started) < 2 || runner.Waiting < 3) && clock.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(5);
        await Task.Delay(100);
        Assert.Equal(2, Volatile.Read(ref started));
        Assert.Equal(3, runner.Waiting);

        gate.Set();
        Assert.Equal(5, (await Task.WhenAll(jobs)).Sum());
        Assert.Equal(5, Volatile.Read(ref started));
        Assert.Equal(5, runner.Jobs);
        Assert.Equal(0, runner.Waiting);
    }

    [Fact]
    public async Task ACancelledWaiterIsDroppedAndTheQueueKeepsWorking()
    {
        using var runner = new QueuedComputeRunner(1);
        using var release = new ManualResetEventSlim();
        var blocking = runner.Run(() => { release.Wait(); return 1; });
        while (runner.Jobs == 0) await Task.Delay(5);

        using var cancel = new CancellationTokenSource();
        bool ran = false;
        var waiting = runner.Run(() => { ran = true; return 2; }, cancel.Token);
        while (runner.Waiting == 0) await Task.Delay(5);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, runner.Waiting);

        release.Set();
        Assert.Equal(1, await blocking);
        Assert.False(ran);
        Assert.Equal(3, await runner.Run(() => 3));
    }

    [Fact]
    public async Task AFailingJobReleasesItsSlot()
    {
        using var runner = new QueuedComputeRunner(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.Run<int>(() => throw new InvalidOperationException()));
        Assert.Equal(7, await runner.Run(() => 7));
    }

    [Fact]
    public async Task TheBrowserRunnerRunsInline()
    {
        var runner = new InlineComputeRunner();
        int thread = Environment.CurrentManagedThreadId;
        Assert.Equal(thread, await runner.Run(() => Environment.CurrentManagedThreadId));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => { _ = runner.Run(() => 1, cancel.Token); });
    }
}
