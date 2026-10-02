using System.Collections;
using Stykker.NanoCut.Geometry3D;
using Xunit.Abstractions;

namespace Stykker.NanoCut.Tests;

/// <summary>Tests that change the global MaxParallelism and assert parallelism-dependent behaviour run alone.</summary>
[CollectionDefinition("Round5Serial", DisableParallelization = true)]
public sealed class Round5SerialCollection;

/// <summary>
/// Adversarial verification of optimisation round 5 (f2a4524): Solid.SubtractInOrder (tool construction overlapped with
/// the cut), Solid.UnionAll (balanced parallel tree, replaces Process3.UnionTree) and the public MaxParallelism.
/// </summary>
[Collection("Round5Serial")]
public class OptimizationVerification5Tests(ITestOutputHelper output)
{
    private const long Mm = 1_000_000;

    private static T WithParallelism<T>(int p, Func<T> f)
    {
        int old = Solid.MaxParallelism;
        Solid.MaxParallelism = p;
        try { return f(); }
        finally { Solid.MaxParallelism = old; }
    }

    private static string Fp(Solid s) => Rev4Workload.Fingerprint(s);

    /// <summary>Convex hull of ~200 points rounded to the grid on a sphere: some 400 triangles (parallel hull faces).</summary>
    private static Solid BigHull(Random rng, Vec3 c, long r)
    {
        var pts = new List<Vec3>();
        for (int i = 0; i < 200; i++)
        {
            double u = rng.NextDouble() * 2 - 1, ph = rng.NextDouble() * 2 * Math.PI, s = Math.Sqrt(1 - u * u);
            pts.Add(new Vec3(c.X + (long)(r * s * Math.Cos(ph)), c.Y + (long)(r * s * Math.Sin(ph)), c.Z + (long)(r * u)));
        }
        return ConvexHull3.Compute(pts);
    }

    /// <summary>A deterministic list of tool factories (lattice hulls, tetrahedra, big hulls) cutting a 10 mm box.</summary>
    private static List<Func<Solid>> Factories(int seed, int count)
    {
        var list = new List<Func<Solid>>();
        for (int i = 0; i < count; i++)
        {
            int s = seed * 1000 + i;
            int kind = i % 5;
            list.Add(() =>
            {
                var rng = new Random(s);
                return kind switch
                {
                    0 => BigHull(rng, new Vec3(rng.Next(0, 11) * Mm, rng.Next(0, 11) * Mm, 10 * Mm), 3 * Mm),
                    _ => Rev4Workload.LatticeHull(rng, rng.Next(-1, 9) * Mm + 17, rng.Next(-1, 9) * Mm - 3, rng.Next(6, 9) * Mm, 3),
                };
            });
        }
        return list;
    }

    private static Solid Block() => Solid.Box(new Vec3(0, 0, 0), new Vec3(10 * Mm, 10 * Mm, 10 * Mm));

    private static Solid Sequential(Solid w, IEnumerable<Func<Solid>> tools)
    {
        foreach (var t in tools) w -= t();
        return w;
    }

    // ------------------------------------------------------------------------------------------- SubtractInOrder

    [Fact]
    public void SubtractInOrderIsExactlySequentialAtAnyParallelism()
    {
        for (int seed = 1; seed <= 3; seed++)
        {
            var tools = Factories(seed, 25);
            var reference = WithParallelism(1, () => Sequential(Block(), tools));
            string fp = Fp(reference);
            var vol = OptimizationVerificationTests.Volume6(reference);
            foreach (int p in new[] { 1, 2, 8 })
            {
                var r = WithParallelism(p, () => Solid.SubtractInOrder(Block(), tools));
                Assert.Equal(fp, Fp(r));
                Assert.Equal(vol, OptimizationVerificationTests.Volume6(r));
                // Sequential differences at the same parallelism, too.
                Assert.Equal(fp, Fp(WithParallelism(p, () => Sequential(Block(), tools))));
            }
            output.WriteLine($"seed {seed}: {reference.FaceCount} faces");
        }
    }

    /// <summary>The next factory returns the very tool object being subtracted (shared faces, lazy PlanesD).</summary>
    [Fact]
    public void SubtractInOrderWithRepeatedAndSharedToolObjects()
    {
        var rng = new Random(77);
        var shared = Enumerable.Range(0, 6).Select(_ => BigHull(rng, new Vec3(rng.Next(0, 11) * Mm, rng.Next(0, 11) * Mm, 10 * Mm), 3 * Mm)).ToArray();
        var block = Block();
        // Tools that reuse the workpiece's own faces (block ∩ x) and the same tool object twice in a row.
        var tools = new List<Func<Solid>>();
        foreach (var t in shared)
        {
            tools.Add(() => t);
            tools.Add(() => t);
            tools.Add(() => block & t);
        }
        var reference = WithParallelism(1, () => Sequential(block, tools));
        var r = WithParallelism(8, () => Solid.SubtractInOrder(block, tools));
        Assert.Equal(Fp(reference), Fp(r));
    }

    [Fact]
    public void ConcurrentIndependentCutChainsSharingToolsAreExact()
    {
        var rng = new Random(5);
        var shared = Enumerable.Range(0, 10).Select(i => i % 2 == 0
            ? BigHull(rng, new Vec3(rng.Next(0, 11) * Mm, rng.Next(0, 11) * Mm, 10 * Mm), 3 * Mm)
            : Rev4Workload.LatticeHull(rng, rng.Next(-1, 9) * Mm, rng.Next(-1, 9) * Mm, rng.Next(6, 9) * Mm, 3)).ToArray();
        // Chain c uses the shared tools in a rotated order.
        List<Func<Solid>> Chain(int c) => Enumerable.Range(0, shared.Length).Select(k => (Func<Solid>)(() => shared[(k + c) % shared.Length])).ToList();
        var block = Block();
        const int chains = 6;
        var expected = WithParallelism(1, () => Enumerable.Range(0, chains).Select(c => Fp(Sequential(block, Chain(c)))).ToArray());
        var got = WithParallelism(8, () =>
        {
            var res = new string[chains];
            var threads = Enumerable.Range(0, chains).Select(c => new Thread(() => res[c] = Fp(Solid.SubtractInOrder(block, Chain(c))))).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            return res;
        });
        for (int c = 0; c < chains; c++) Assert.True(expected[c] == got[c], $"chain {c} differs");
    }

    /// <summary>A factory exception at any position: same exception (type, message, identity semantics) as sequential.</summary>
    [Fact]
    public void FactoryExceptionsAtEveryPositionMatchSequential()
    {
        const int n = 6;
        for (int pos = 0; pos < n; pos++)
        {
            foreach (int p in new[] { 1, 2, 8 })
            {
                var calls = new List<int>();
                var tools = Factories(9, n).Select((f, i) => (Func<Solid>)(() =>
                {
                    lock (calls) calls.Add(i);
                    if (i == pos) throw new InvalidOperationException($"bad {i}");
                    return f();
                })).ToList();
                var ex = Assert.Throws<InvalidOperationException>(() => WithParallelism(p, () => Solid.SubtractInOrder(Block(), tools)));
                Assert.Equal($"bad {pos}", ex.Message);
                // No factory after the throwing one is invoked (the next is only started after the current one is built).
                Assert.Equal(Enumerable.Range(0, pos + 1), calls.OrderBy(i => i));
            }
        }
    }

    private sealed class TrackingEnumerable(IReadOnlyList<Func<Solid>> items, int throwAt) : IEnumerable<Func<Solid>>
    {
        public int Disposed;

        public IEnumerator<Func<Solid>> GetEnumerator() => Iterate().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerable<Func<Solid>> Iterate()
        {
            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (i == throwAt) throw new FormatException($"enum {i}");
                    yield return items[i];
                }
            }
            finally { Interlocked.Increment(ref Disposed); }
        }
    }

    [Fact]
    public void EnumeratorExceptionsAndDisposalMatchSequential()
    {
        var items = Factories(11, 4);
        foreach (int p in new[] { 1, 8 })
        {
            for (int at = 0; at <= 4; at++)
            {
                var e = new TrackingEnumerable(items, at);
                if (at < 4)
                {
                    var ex = Assert.Throws<FormatException>(() => WithParallelism(p, () => Solid.SubtractInOrder(Block(), e)));
                    Assert.Equal($"enum {at}", ex.Message);
                }
                else WithParallelism(p, () => Solid.SubtractInOrder(Block(), e));
                Assert.Equal(1, e.Disposed);
            }
            // Factory exception: the enumerator is disposed as well.
            var bad = items.Take(2).Append(() => throw new InvalidOperationException()).Concat(items).ToList();
            var te = new TrackingEnumerable(bad, -1);
            Assert.Throws<InvalidOperationException>(() => WithParallelism(p, () => Solid.SubtractInOrder(Block(), te)));
            Assert.Equal(1, te.Disposed);
        }
    }

    /// <summary>A factory that returns null: the Boolean throws; the same exception type in both modes.</summary>
    [Fact]
    public void NullToolThrowsTheSameExceptionAtAnyParallelism()
    {
        var items = Factories(12, 3);
        for (int pos = 0; pos < 3; pos++)
        {
            var tools = items.Select((f, i) => i == pos ? () => null! : f).ToList();
            var e1 = Record.Exception(() => WithParallelism(1, () => Solid.SubtractInOrder(Block(), tools)));
            var e8 = Record.Exception(() => WithParallelism(8, () => Solid.SubtractInOrder(Block(), tools)));
            Assert.NotNull(e1);
            Assert.Equal(e1.GetType(), e8!.GetType());
        }
    }

    /// <summary>
    /// A null factory in the sequence: the sequential path invokes it (NullReferenceException), the overlapped path
    /// passes it to Task.Run (ArgumentNullException) – the exception type depends on MaxParallelism.
    /// </summary>
    [Fact]
    public void NullFactoryThrowsTheSameExceptionAtAnyParallelism()
    {
        var tools = Factories(13, 3);
        tools[1] = null!;
        var e1 = Record.Exception(() => WithParallelism(1, () => Solid.SubtractInOrder(Block(), tools)));
        var e8 = Record.Exception(() => WithParallelism(8, () => Solid.SubtractInOrder(Block(), tools)));
        Assert.NotNull(e1);
        Assert.Equal(e1.GetType(), e8!.GetType());
    }

    [Fact]
    public void MaxParallelismClampsToOne()
    {
        WithParallelism(1, () =>
        {
            Solid.MaxParallelism = 0;
            Assert.Equal(1, Solid.MaxParallelism);
            Solid.MaxParallelism = -5;
            Assert.Equal(1, Solid.MaxParallelism);
            return 0;
        });
    }

    // ------------------------------------------------------------------------------------------------ UnionAll

    /// <summary>Process3's previous private UnionTree, verbatim.</summary>
    private static Solid OldUnionTree(List<Solid> pieces)
    {
        var level = pieces;
        while (level.Count > 1)
        {
            var next = new List<Solid>((level.Count + 1) / 2);
            for (int i = 0; i + 1 < level.Count; i += 2) next.Add(level[i] | level[i + 1]);
            if (level.Count % 2 == 1) next.Add(level[^1]);
            level = next;
        }
        return level[0];
    }

    [Fact]
    public void UnionAllEqualsOldTreeAndChainedUnionForSizes0To40()
    {
        var rng = new Random(2024);
        var pool = Enumerable.Range(0, 40).Select(i => i % 13 == 0
            ? BigHull(rng, new Vec3(rng.Next(0, 20) * Mm, rng.Next(0, 6) * Mm, rng.Next(0, 6) * Mm), 2 * Mm)
            : Rev4Workload.LatticeHull(rng, rng.Next(0, 20) * Mm, rng.Next(0, 6) * Mm, rng.Next(0, 6) * Mm, 3)).ToList();
        Assert.True(Solid.UnionAll([]).IsEmpty);
        Assert.Same(pool[0], Solid.UnionAll([pool[0]]));
        var chained = pool[0];
        for (int n = 1; n <= 40; n++)
        {
            var parts = pool.Take(n).ToList();
            if (n > 1) chained |= parts[^1];
            string old = Fp(WithParallelism(1, () => OldUnionTree(parts)));
            foreach (int p in new[] { 1, 8 })
            {
                var u = WithParallelism(p, () => Solid.UnionAll(parts));
                Assert.True(old == Fp(u), $"n = {n}, p = {p}: differs from the old tree");
            }
            Assert.Equal(OptimizationVerificationTests.Volume6(chained), OptimizationVerificationTests.Volume6(Solid.UnionAll(parts)));
        }
    }

    /// <summary>
    /// A Boolean exception inside UnionAll: sequential (MaxParallelism 1, or only one pair per level) rethrows it
    /// directly, the parallel level wraps it in an AggregateException (SolidBoolean.Process unwraps for exactly this
    /// reason). Before round 5 Process3 threw the original exception.
    /// </summary>
    [Fact]
    public void UnionAllThrowsTheSameExceptionAtAnyParallelism()
    {
        var rng = new Random(1);
        var a = Rev4Workload.LatticeHull(rng, 0, 0, 0, 3);
        var b = Rev4Workload.LatticeHull(rng, Mm, 0, 0, 3);
        Solid[] parts = [a, b, a, null!];
        var e1 = Record.Exception(() => WithParallelism(1, () => Solid.UnionAll(parts)));
        var e8 = Record.Exception(() => WithParallelism(8, () => Solid.UnionAll(parts)));
        Assert.IsType<ArgumentNullException>(e1);
        Assert.Equal(e1.GetType(), e8!.GetType());
    }
}
