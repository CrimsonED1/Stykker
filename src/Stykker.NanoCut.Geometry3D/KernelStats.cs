using System.Collections.Concurrent;

namespace Stykker.NanoCut.Geometry3D;

/// <summary>
/// Optional instrumentation for the kernel hot paths, used by the benchmark to answer the questions that
/// <c>docs/native-speed-plan.md</c> leaves open: how often the floating-point filters fall through to exact
/// arithmetic, and what the convex hull spends its time on.
///
/// Disabled by default. When <see cref="Counting"/> is false every guarded site compiles down to one
/// branch-predictable test, so the shipping code pays almost nothing; when it is on the counters are per thread,
/// so a parallel Boolean does not pay for atomics. The timings in <c>bench/results-*.html</c> are produced with
/// counting off, and the cost of counting is verifiable with <see cref="CostOfCountingMs"/>.
/// </summary>
internal static class KernelStats
{
    /// <summary>Per-thread counters. Never touched concurrently: one thread writes, the reader aggregates.</summary>
    internal sealed class Counts
    {
        public long FilterCalls, FilterUncertain;
        public long SideGrid, SideExact;
        public long AboveCalls, AboveExact;
        public long HullPoints, HullTris, HullFaces, HullFromGridEdges;
        public long MergePasses, MergeKeys;

        public void Reset()
        {
            FilterCalls = FilterUncertain = 0;
            SideGrid = SideExact = 0;
            AboveCalls = AboveExact = 0;
            HullPoints = HullTris = HullFaces = HullFromGridEdges = 0;
            MergePasses = MergeKeys = 0;
        }
    }

    private static readonly ConcurrentDictionary<int, Counts> Registry = new();

    [ThreadStatic]
    private static Counts? _mine;

    /// <summary>When false (the default) the guarded sites skip the counters entirely.</summary>
    internal static bool Counting { get; set; }

    /// <summary>The calling thread's counter block, registered on first use.</summary>
    internal static Counts Mine => _mine ??= Register();

    private static Counts Register()
    {
        var c = new Counts();
        Registry[Environment.CurrentManagedThreadId] = c;
        return c;
    }

    internal static void Reset()
    {
        Registry.Clear();
        _mine = null;
        Counting = false;
    }

    /// <summary>Aggregated snapshot, summed over all threads that ever touched the kernel.</summary>
    internal static (long FilterCalls, long FilterUncertain, long SideGrid, long SideExact, long AboveCalls,
        long AboveExact, long HullPoints, long HullTris, long HullFaces, long HullFromGridEdges, long MergePasses,
        long MergeKeys) Snapshot()
    {
        long fc = 0, fu = 0, sg = 0, se = 0, ac = 0, ae = 0, hp = 0, ht = 0, hf = 0, he = 0, mp = 0, mk = 0;
        foreach (var c in Registry.Values)
        {
            fc += c.FilterCalls; fu += c.FilterUncertain;
            sg += c.SideGrid; se += c.SideExact;
            ac += c.AboveCalls; ae += c.AboveExact;
            hp += c.HullPoints; ht += c.HullTris; hf += c.HullFaces; he += c.HullFromGridEdges;
            mp += c.MergePasses; mk += c.MergeKeys;
        }
        return (fc, fu, sg, se, ac, ae, hp, ht, hf, he, mp, mk);
    }

    /// <summary>Wall-clock cost of the counting itself, in ms, measured on <paramref name="work"/>.</summary>
    internal static double CostOfCountingMs(Action work, int passes = 3)
    {
        double best = double.MaxValue;
        for (int i = 0; i < passes; i++)
        {
            Counting = false;
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            work();
            double off = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            Counting = true;
            t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            work();
            double on = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            best = Math.Min(best, on - off);
        }
        Counting = false;
        return best;
    }

    internal static string Report()
    {
        var s = Snapshot();
        string Pct(long part, long total) => total > 0 ? $" {100.0 * part / total:F2} %" : "";
        return $"filter {s.FilterCalls} calls, {s.FilterUncertain} uncertain{Pct(s.FilterUncertain, s.FilterCalls)}; " +
               $"plane-side {s.SideGrid} grid / {s.SideExact} exact{Pct(s.SideExact, s.SideGrid + s.SideExact)}; " +
               $"hull-Above {s.AboveCalls} calls, {s.AboveExact} exact{Pct(s.AboveExact, s.AboveCalls)}; " +
               $"hull {s.HullTris} tris -> {s.HullFaces} faces, {s.HullFromGridEdges} edge planes; " +
               $"merging {s.MergePasses} passes over {s.MergeKeys} keys";
    }
}