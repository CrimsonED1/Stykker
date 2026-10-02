namespace Stykker.NanoCut;

/// <summary>
/// Minimal signed 256-bit integer (two's complement, four 64-bit limbs) for the fixed-size products of the
/// plane-intersection fast path. Every operation is checked; callers stay far inside the range by the bit budget.
/// </summary>
internal readonly struct Int256
{
    public readonly ulong L0, L1, L2, L3;

    public Int256(ulong l0, ulong l1, ulong l2, ulong l3)
    {
        L0 = l0; L1 = l1; L2 = l2; L3 = l3;
    }

    public bool IsNegative => (long)L3 < 0;

    public bool IsZero => (L0 | L1 | L2 | L3) == 0;

    public Int256 Negate()
    {
        ulong r0 = ~L0 + 1;
        ulong c = r0 == 0 ? 1UL : 0UL;
        ulong r1 = ~L1 + c;
        c = c != 0 && r1 == 0 ? 1UL : 0UL;
        ulong r2 = ~L2 + c;
        c = c != 0 && r2 == 0 ? 1UL : 0UL;
        ulong r3 = ~L3 + c;
        var r = new Int256(r0, r1, r2, r3);
        if (IsNegative && r.IsNegative) throw new OverflowException("Int256 negation overflow.");
        return r;
    }

    public static Int256 operator +(Int256 a, Int256 b)
    {
        ulong r0 = a.L0 + b.L0;
        ulong c = r0 < a.L0 ? 1UL : 0UL;
        ulong t = a.L1 + b.L1;
        ulong c1 = t < a.L1 ? 1UL : 0UL;
        ulong r1 = t + c;
        c = c1 | (r1 < t ? 1UL : 0UL);
        t = a.L2 + b.L2;
        c1 = t < a.L2 ? 1UL : 0UL;
        ulong r2 = t + c;
        c = c1 | (r2 < t ? 1UL : 0UL);
        ulong r3 = a.L3 + b.L3 + c;
        var r = new Int256(r0, r1, r2, r3);
        if (a.IsNegative == b.IsNegative && r.IsNegative != a.IsNegative) throw new OverflowException("Int256 addition overflow.");
        return r;
    }

    public static Int256 operator -(Int256 a, Int256 b) => a + b.Negate();

    /// <summary>Exact product of two 128-bit integers (|a|, |b| &lt; 2^127).</summary>
    public static Int256 Product(Int128 a, Int128 b)
    {
        if (a == Int128.MinValue || b == Int128.MinValue) throw new OverflowException("Int256 product operand out of range.");
        bool negative = (a < 0) != (b < 0);
        UInt128 x = (UInt128)(a < 0 ? -a : a), y = (UInt128)(b < 0 ? -b : b);
        Mul((ulong)x, (ulong)(x >> 64), 0, 0, (ulong)y, (ulong)(y >> 64), out var m);
        return negative ? m.Negate() : m;
    }

    /// <summary>Exact product a·b; throws if it does not fit.</summary>
    public static Int256 operator *(Int256 a, Int128 b)
    {
        if (b == Int128.MinValue) throw new OverflowException("Int256 product operand out of range.");
        bool negative = a.IsNegative != (b < 0);
        var x = a.IsNegative ? a.Negate() : a;
        UInt128 y = (UInt128)(b < 0 ? -b : b);
        Mul(x.L0, x.L1, x.L2, x.L3, (ulong)y, (ulong)(y >> 64), out var m);
        return negative ? m.Negate() : m;
    }

    // Magnitude product (x0..x3) · (y0, y1); throws unless the result is below 2^255.
    private static void Mul(ulong x0, ulong x1, ulong x2, ulong x3, ulong y0, ulong y1, out Int256 result)
    {
        Span<ulong> r = stackalloc ulong[6];
        r.Clear();
        MulAdd(r, 0, x0, y0); MulAdd(r, 1, x0, y1);
        MulAdd(r, 1, x1, y0); MulAdd(r, 2, x1, y1);
        MulAdd(r, 2, x2, y0); MulAdd(r, 3, x2, y1);
        MulAdd(r, 3, x3, y0); MulAdd(r, 4, x3, y1);
        if (r[4] != 0 || r[5] != 0 || (long)r[3] < 0) throw new OverflowException("Int256 multiplication overflow.");
        result = new Int256(r[0], r[1], r[2], r[3]);
    }

    // r[k..] += a·b with carry propagation.
    private static void MulAdd(Span<ulong> r, int k, ulong a, ulong b)
    {
        if (a == 0 || b == 0) return;
        ulong hi = Math.BigMul(a, b, out ulong lo);
        ulong t = r[k] + lo;
        ulong carry = hi + (t < lo ? 1UL : 0UL);
        r[k] = t;
        for (int i = k + 1; carry != 0; i++)
        {
            t = r[i] + carry;
            carry = t < carry ? 1UL : 0UL;
            r[i] = t;
        }
    }

    /// <summary>Sign-extended conversion.</summary>
    public Int384 ToInt384() => Int384.FromLimbs(L0, L1, L2, L3, IsNegative ? ulong.MaxValue : 0, IsNegative ? ulong.MaxValue : 0);
}
