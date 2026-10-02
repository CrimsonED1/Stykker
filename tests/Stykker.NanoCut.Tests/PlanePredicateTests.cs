using System.Numerics;

namespace Stykker.NanoCut.Tests;

/// <summary>Bit budget of the 3D plane predicates (docs/bit-budget.md), verified with BigInteger.</summary>
public class PlanePredicateTests
{
    private const long M = Units.MaxCoordinate;

    // Proven bounds for |coordinate| ≤ 2^31.
    private const int NormalBits = 66;     // |n_i| ≤ 2^65
    private const int OffsetBits = 98;     // |d| ≤ 3·2^96 < 2^98
    private const int WBits = 198;         // |W| ≤ 6·2^195 < 2^198
    private const int XBits = 231;         // |X| ≤ 6·2^228 < 2^231
    private const int SideBits = 298;      // |n·X + d·W| ≤ 4·2^296 = 2^298

    private static long Coord(Random rng) => rng.Next(3) switch
    {
        0 => rng.NextInt64(-M, M + 1),
        1 => rng.Next(2) == 0 ? M : -M,
        _ => rng.NextInt64(-1000, 1001),
    };

    private static Vec3 P(Random r) => new(Coord(r), Coord(r), Coord(r));

    private static (BigInteger X, BigInteger Y, BigInteger Z, BigInteger W) IntersectBig(Plane3 p, Plane3 q, Plane3 r)
    {
        BigInteger[,] a =
        {
            { (BigInteger)p.Nx, (BigInteger)p.Ny, (BigInteger)p.Nz, -(BigInteger)p.D },
            { (BigInteger)q.Nx, (BigInteger)q.Ny, (BigInteger)q.Nz, -(BigInteger)q.D },
            { (BigInteger)r.Nx, (BigInteger)r.Ny, (BigInteger)r.Nz, -(BigInteger)r.D },
        };
        BigInteger Det(int c0, int c1, int c2) =>
            a[0, c0] * (a[1, c1] * a[2, c2] - a[1, c2] * a[2, c1])
            - a[0, c1] * (a[1, c0] * a[2, c2] - a[1, c2] * a[2, c0])
            + a[0, c2] * (a[1, c0] * a[2, c1] - a[1, c1] * a[2, c0]);
        return (Det(3, 1, 2), Det(0, 3, 2), Det(0, 1, 3), Det(0, 1, 2));
    }

    [Fact]
    public void PlanesFromPointsStayInBudget()
    {
        var rng = new Random(11);
        for (int i = 0; i < 100_000; i++)
        {
            Vec3 a = P(rng), b = P(rng), c = P(rng), d = P(rng);
            var pl = Plane3.FromPoints(a, b, c);
            Assert.True(BigInteger.Abs((BigInteger)pl.Nx).GetBitLength() <= NormalBits);
            Assert.True(BigInteger.Abs((BigInteger)pl.D).GetBitLength() <= OffsetBits);
            // The plane contains its points; Side agrees with orient3d.
            Assert.Equal(0, Predicates.Side(pl, a));
            Assert.Equal(0, Predicates.Side(pl, b));
            Assert.Equal(0, Predicates.Side(pl, c));
            Assert.Equal(Predicates.Orient3D(a, b, c, d), Predicates.Side(pl, d));
        }
    }

    [Fact]
    public void ThreePlaneIntersectionIsExactAndWithinBitBudget()
    {
        var rng = new Random(12);
        int checkedCases = 0;
        int maxW = 0, maxX = 0, maxSide = 0;
        for (int i = 0; i < 100_000; i++)
        {
            var p = Plane3.FromPoints(P(rng), P(rng), P(rng));
            var q = Plane3.FromPoints(P(rng), P(rng), P(rng));
            var r = Plane3.FromPoints(P(rng), P(rng), P(rng));
            var s = Plane3.FromPoints(P(rng), P(rng), P(rng));
            var hp = Plane3.Intersect(p, q, r);
            var exact = IntersectBig(p, q, r);
            if (hp is null)
            {
                Assert.True(exact.W.IsZero);
                continue;
            }
            var h = hp.Value;
            Assert.Equal(exact.X, (BigInteger)h.X);
            Assert.Equal(exact.Y, (BigInteger)h.Y);
            Assert.Equal(exact.Z, (BigInteger)h.Z);
            Assert.Equal(exact.W, (BigInteger)h.W);

            // The point lies on all three planes.
            Assert.Equal(0, Predicates.Side(p, h));
            Assert.Equal(0, Predicates.Side(q, h));
            Assert.Equal(0, Predicates.Side(r, h));

            BigInteger side = (BigInteger)s.Nx * exact.X + (BigInteger)s.Ny * exact.Y + (BigInteger)s.Nz * exact.Z + (BigInteger)s.D * exact.W;
            Assert.Equal(side.Sign * exact.W.Sign, Predicates.Side(s, h));

            maxW = Math.Max(maxW, (int)BigInteger.Abs(exact.W).GetBitLength());
            maxX = Math.Max(maxX, (int)new[] { exact.X, exact.Y, exact.Z }.Max(v => BigInteger.Abs(v).GetBitLength()));
            maxSide = Math.Max(maxSide, (int)BigInteger.Abs(side).GetBitLength());
            checkedCases++;
        }
        Assert.True(checkedCases > 90_000);
        Assert.True(maxW <= WBits, $"W uses {maxW} bits");
        Assert.True(maxX <= XBits, $"X uses {maxX} bits");
        Assert.True(maxSide <= SideBits, $"side test uses {maxSide} bits");
    }

    [Fact]
    public void ExtremeConfigurationDoesNotOverflow()
    {
        // Planes through the corners of the full coordinate cube produce the largest magnitudes.
        Vec3 c000 = new(-M, -M, -M), c100 = new(M, -M, -M), c010 = new(-M, M, -M), c001 = new(-M, -M, M);
        Vec3 c111 = new(M, M, M), c110 = new(M, M, -M), c011 = new(-M, M, M), c101 = new(M, -M, M);
        var p = Plane3.FromPoints(c100, c010, c001);
        var q = Plane3.FromPoints(c000, c110, c011);
        var r = Plane3.FromPoints(c111, c001, c010);
        var s = Plane3.FromPoints(c101, c011, c110);
        var h = Plane3.Intersect(p, q, r);
        Assert.NotNull(h);
        _ = Predicates.Side(s, h.Value); // must not throw
        Assert.True(h.Value.W.BitLength <= WBits);
    }
}
