using System.Diagnostics;

namespace Stykker.NanoCut.Demo.Services;

/// <summary>
/// Runs the heavy part of a page: a cut, a scene, a section. The pages await it between their progress updates, so the
/// same page code works whether the geometry is computed in the browser or on a server.
/// </summary>
public interface IComputeRunner
{
    /// <summary>Where the geometry runs, for the page footer: "C# on WebAssembly" or "native .NET on the server".</summary>
    string Where { get; }

    /// <summary>Jobs waiting for a slot right now (always 0 in the browser).</summary>
    int Waiting { get; }

    /// <summary>
    /// Runs <paramref name="work"/> and returns its result. <paramref name="cancel"/> drops a job that is still
    /// waiting for a slot; a job that has started runs to the end of this call (the pages check their own stop flag
    /// between calls, so a cut is never left half applied).
    /// </summary>
    Task<T> Run<T>(Func<T> work, CancellationToken cancel = default);
}

/// <summary>Convenience overloads.</summary>
public static class ComputeRunnerExtensions
{
    /// <summary>Runs work without a result.</summary>
    public static Task Run(this IComputeRunner runner, Action work, CancellationToken cancel = default) =>
        runner.Run(() => { work(); return true; }, cancel);
}

/// <summary>
/// The browser: WebAssembly runs on the UI thread, so the work runs inline, exactly as the pages did before the runner
/// existed; the pages' own <c>Task.Delay</c> between steps lets the browser draw.
/// </summary>
public sealed class InlineComputeRunner : IComputeRunner
{
    /// <inheritdoc />
    public string Where => "C# on WebAssembly";

    /// <inheritdoc />
    public int Waiting => 0;

    /// <inheritdoc />
    public Task<T> Run<T>(Func<T> work, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        return Task.FromResult(work());
    }
}

/// <summary>
/// The server: every job runs on the thread pool, off the circuit's synchronisation context, so a long cut does not
/// block that user's UI (the Stop button and the progress stay live). At most <see cref="MaxConcurrentJobs"/> jobs run
/// at once across all users; each of them already uses every core (<c>SolidBoolean.MaxParallelism</c> stays at its
/// default), so more would only make every job slower. The others wait for a free slot.
/// </summary>
public sealed class QueuedComputeRunner : IComputeRunner, IDisposable
{
    private readonly SemaphoreSlim _slots;
    private int _waiting;
    private long _jobs, _queuedTicks;

    /// <summary>Creates the queue.</summary>
    /// <param name="maxConcurrentJobs">Jobs that may run at once, at least 1.</param>
    public QueuedComputeRunner(int maxConcurrentJobs)
    {
        MaxConcurrentJobs = Math.Max(1, maxConcurrentJobs);
        _slots = new SemaphoreSlim(MaxConcurrentJobs, MaxConcurrentJobs);
    }

    /// <summary>Jobs that may run at once.</summary>
    public int MaxConcurrentJobs { get; }

    /// <inheritdoc />
    public string Where => "native .NET on the server";

    /// <inheritdoc />
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>Jobs started so far.</summary>
    public long Jobs => Interlocked.Read(ref _jobs);

    /// <summary>Total time jobs spent waiting for a slot.</summary>
    public TimeSpan TimeQueued => TimeSpan.FromTicks(Interlocked.Read(ref _queuedTicks));

    /// <inheritdoc />
    public async Task<T> Run<T>(Func<T> work, CancellationToken cancel = default)
    {
        long start = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _waiting);
        try { await _slots.WaitAsync(cancel).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _waiting); }
        Interlocked.Add(ref _queuedTicks, Stopwatch.GetElapsedTime(start).Ticks);
        Interlocked.Increment(ref _jobs);
        try
        {
            // No ConfigureAwait(false) on the caller's side: the page continues on its circuit after the await.
            return await Task.Run(work, cancel).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _slots.Dispose();
}
