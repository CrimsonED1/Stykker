using System.Numerics;

namespace Stykker.NanoCut.Tests;

public class Int384Tests
{
    private static readonly BigInteger Max = (BigInteger.One << 383) - 1;
    private static readonly BigInteger Min = -(BigInteger.One << 383);

    private static BigInteger RandomBig(Random rng, int maxBits)
    {
        int bits = rng.Next(0, maxBits + 1);
        if (bits == 0) return BigInteger.Zero;
        var bytes = new byte[(bits + 7) / 8 + 1];
        rng.NextBytes(bytes);
        bytes[^1] = 0;
        var v = new BigInteger(bytes) & ((BigInteger.One << bits) - 1);
        return rng.Next(2) == 0 ? v : -v;
    }

    [Fact]
    public void RoundTripsAndConstants()
    {
        Assert.Equal(Max, (BigInteger)Int384.MaxValue);
        Assert.Equal(Min, (BigInteger)Int384.MinValue);
        Assert.Equal(BigInteger.Zero, (BigInteger)Int384.Zero);
        Assert.Equal(new BigInteger(-1), (BigInteger)(Int384)(-1L));
        Assert.Equal((BigInteger)Int128.MinValue, (BigInteger)(Int384)Int128.MinValue);
        Assert.Equal((BigInteger)Int128.MaxValue, (BigInteger)(Int384)Int128.MaxValue);
        Assert.Equal(Max, (BigInteger)(Int384)Max);
        Assert.Equal(Min, (BigInteger)(Int384)Min);
        Assert.Throws<OverflowException>(() => (Int384)(Max + 1));
        Assert.Throws<OverflowException>(() => (Int384)(Min - 1));
    }

    [Fact]
    public void ArithmeticMatchesBigInteger()
    {
        var rng = new Random(384);
        for (int i = 0; i < 200_000; i++)
        {
            BigInteger a = RandomBig(rng, 383), b = RandomBig(rng, 383);
            Int384 x = (Int384)a, y = (Int384)b;
            Check(a + b, () => x + y);
            Check(a - b, () => x - y);
            Check(a * b, () => x * y);
            Assert.Equal(a.CompareTo(b), x.CompareTo(y));
            Assert.Equal(a.Sign, x.Sign);

            BigInteger c = RandomBig(rng, 191), d = RandomBig(rng, 191);
            Check(c * d, () => (Int384)c * (Int384)d);
        }
    }

    [Fact]
    public void BitLengthMatchesBigInteger()
    {
        var rng = new Random(7);
        for (int i = 0; i < 10_000; i++)
        {
            var a = RandomBig(rng, 383);
            Assert.Equal((int)BigInteger.Abs(a).GetBitLength(), ((Int384)a).BitLength);
        }
    }

    [Fact]
    public void EdgeCasesOverflow()
    {
        Assert.Throws<OverflowException>(() => Int384.MaxValue + Int384.One);
        Assert.Throws<OverflowException>(() => Int384.MinValue - Int384.One);
        Assert.Throws<OverflowException>(() => -Int384.MinValue);
        Assert.Throws<OverflowException>(() => Int384.MinValue * (Int384)(-1L));
        Assert.Equal(Min, (BigInteger)((Int384)(BigInteger.One << 382) * (Int384)(-2L)));
        Assert.Throws<OverflowException>(() => (Int384)(BigInteger.One << 382) * (Int384)2L);
        Assert.Equal(Max - 1 - Min + Min, (BigInteger)(Int384.MaxValue - Int384.One));
        Assert.Equal(BigInteger.MinusOne, (BigInteger)((Int384)(-1L) - Int384.Zero));
        Assert.Equal(Max, (BigInteger)((Int384)(-1L) - Int384.MinValue));
    }

    private static void Check(BigInteger expected, Func<Int384> op)
    {
        if (expected > Max || expected < Min)
            Assert.Throws<OverflowException>(() => op());
        else
            Assert.Equal(expected, (BigInteger)op());
    }
}
