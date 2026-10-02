using System.Numerics;

namespace Stykker.NanoCut.Tests;

/// <summary>Phase 1 acceptance: predicates agree in sign with BigInteger on 10^6 random cases.</summary>
public class PredicateTests
{
    private const long M = Units.MaxCoordinate;

    private static long Coord(Random rng) => rng.Next(4) switch
    {
        0 => rng.NextInt64(-M, M + 1),
        1 => rng.Next(2) == 0 ? M - rng.Next(3) : -M + rng.Next(3), // range extremes
        2 => rng.NextInt64(-1000, 1001),                             // near-degenerate small values
        _ => rng.NextInt64(-M, M + 1) / 1024 * 1024,                 // many collinear/coplanar configurations
    };

    private static Vec2 P2(Random r) => new(Coord(r), Coord(r));

    private static Vec3 P3(Random r) => new(Coord(r), Coord(r), Coord(r));

    private static BigInteger Orient2DBig(Vec2 a, Vec2 b, Vec2 c) =>
        ((BigInteger)b.X - a.X) * ((BigInteger)c.Y - a.Y) - ((BigInteger)b.Y - a.Y) * ((BigInteger)c.X - a.X);

    private static BigInteger Orient3DBig(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        BigInteger bx = (BigInteger)b.X - a.X, by = (BigInteger)b.Y - a.Y, bz = (BigInteger)b.Z - a.Z;
        BigInteger cx = (BigInteger)c.X - a.X, cy = (BigInteger)c.Y - a.Y, cz = (BigInteger)c.Z - a.Z;
        BigInteger dx = (BigInteger)d.X - a.X, dy = (BigInteger)d.Y - a.Y, dz = (BigInteger)d.Z - a.Z;
        return (by * cz - bz * cy) * dx + (bz * cx - bx * cz) * dy + (bx * cy - by * cx) * dz;
    }

    [Fact]
    public void Orient2DMatchesBigIntegerOnOneMillionCases()
    {
        var rng = new Random(2);
        int zeros = 0;
        for (int i = 0; i < 1_000_000; i++)
        {
            Vec2 a = P2(rng), b = P2(rng), c = P2(rng);
            if (i % 4 == 0)
            {
                // Exactly collinear points a, a + t1·d, a + t2·d.
                var d = new Vec2(rng.NextInt64(-100_000, 100_001), rng.NextInt64(-100_000, 100_001));
                long t1 = rng.Next(-10_000, 10_001), t2 = rng.Next(-10_000, 10_001);
                var b2 = new Vec2(a.X + d.X * t1, a.Y + d.Y * t1);
                var c2 = new Vec2(a.X + d.X * t2, a.Y + d.Y * t2);
                if (InRange(b2) && InRange(c2)) { b = b2; c = c2; }
            }
            if (i % 8 == 1) c = a;
            BigInteger exact = Orient2DBig(a, b, c);
            Assert.Equal((BigInteger)Predicates.Orient2DValue(a, b, c), exact);
            Assert.Equal(exact.Sign, Predicates.Orient2D(a, b, c));
            if (exact.IsZero) zeros++;
        }
        Assert.True(zeros > 1000, "degenerate cases must be covered");
    }

    [Fact]
    public void Orient3DMatchesBigIntegerOnOneMillionCases()
    {
        var rng = new Random(3);
        for (int i = 0; i < 1_000_000; i++)
        {
            Vec3 a = P3(rng), b = P3(rng), c = P3(rng);
            Vec3 d = i % 8 == 0 ? b : P3(rng);
            BigInteger exact = Orient3DBig(a, b, c, d);
            Assert.Equal((BigInteger)Predicates.Orient3DValue(a, b, c, d), exact);
            Assert.Equal(exact.Sign, Predicates.Orient3D(a, b, c, d));
        }
    }

    [Fact]
    public void Orient2DSmallDeterministicCases()
    {
        Assert.Equal(1, Predicates.Orient2D(new(0, 0), new(1, 0), new(0, 1)));
        Assert.Equal(-1, Predicates.Orient2D(new(0, 0), new(0, 1), new(1, 0)));
        Assert.Equal(0, Predicates.Orient2D(new(-M, -M), new(M, M), new(0, 0)));
        // A point one nanometre off a 4.3 m diagonal is still detected.
        Assert.Equal(1, Predicates.Orient2D(new(-M, -M), new(M, M), new(0, 1)));
        Assert.Equal(-1, Predicates.Orient2D(new(-M, -M), new(M, M), new(1, 0)));
    }

    private static bool InRange(Vec2 p) => Math.Abs(p.X) <= M && Math.Abs(p.Y) <= M;
}
