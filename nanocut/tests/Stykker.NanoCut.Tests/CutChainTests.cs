using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Tests;

public class CutChainTests
{
    private static List<Func<Solid>> BallSteps(int count)
    {
        var steps = new List<Func<Solid>>();
        for (int i = 0; i < count; i++)
        {
            double x0 = -4 + 1.5 * i, x1 = x0 + 1.5;
            steps.Add(() => ConvexHull3.Compute(Ball(x0).Concat(Ball(x1))));
        }
        return steps;

        static IEnumerable<Vec3> Ball(double x)
        {
            for (int i = 0; i <= 8; i++)
                for (int j = 0; j < 16; j++)
                {
                    double th = Math.PI * i / 8, ph = 2 * Math.PI * j / 16;
                    yield return Vec3.Mm(x + 2 * Math.Sin(th) * Math.Cos(ph), 5 + 2 * Math.Sin(th) * Math.Sin(ph), 9.5 + 2 * Math.Cos(th));
                }
        }
    }

    [Fact]
    public void SubtractInOrderEqualsSequentialDifferences()
    {
        var block = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(20, 10, 10));
        var steps = BallSteps(12);
        var sequential = block;
        foreach (var make in steps) sequential -= make();
        var chained = Solid.SubtractInOrder(block, steps);
        Assert.Equal(sequential.VolumeMm3, chained.VolumeMm3, 9);
        Assert.Equal(sequential.FaceCount, chained.FaceCount);
    }

    [Fact]
    public void SubtractInOrderIsIndependentOfParallelism()
    {
        var block = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(20, 10, 10));
        int saved = Solid.MaxParallelism;
        try
        {
            Solid.MaxParallelism = 1;
            var one = Solid.SubtractInOrder(block, BallSteps(10));
            Solid.MaxParallelism = 8;
            var many = Solid.SubtractInOrder(block, BallSteps(10));
            Assert.Equal(one.VolumeMm3, many.VolumeMm3, 12);
            Assert.Equal(one.FaceCount, many.FaceCount);
        }
        finally { Solid.MaxParallelism = saved; }
    }

    [Fact]
    public void SubtractInOrderRethrowsTheFactoryException()
    {
        var block = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(20, 10, 10));
        var steps = BallSteps(3);
        steps.Insert(1, () => throw new InvalidOperationException("bad tool"));
        var ex = Assert.Throws<InvalidOperationException>(() => Solid.SubtractInOrder(block, steps));
        Assert.Equal("bad tool", ex.Message);
        Assert.Same(block, Solid.SubtractInOrder(block, []));
    }

    [Fact]
    public void UnionAllEqualsChainedUnion()
    {
        var parts = BallSteps(9).Select(f => f()).ToList();
        var chained = parts[0];
        for (int i = 1; i < parts.Count; i++) chained |= parts[i];
        Assert.Equal(chained.VolumeMm3, Solid.UnionAll(parts).VolumeMm3, 9);
        Assert.True(Solid.UnionAll([]).IsEmpty);
    }
}
