using System.Globalization;
using System.Numerics;

namespace Stykker.NanoCut;

/// <summary>
/// Signed 384-bit two's-complement integer (6 × 64 bit) for the exact 3D predicates.
/// All arithmetic is checked: results that do not fit throw <see cref="OverflowException"/>
/// instead of wrapping, so an exceeded bit budget can never produce a wrong sign silently.
/// </summary>
public readonly struct Int384 : IEquatable<Int384>, IComparable<Int384>
{
    private const int Limbs = 6;

    // Little-endian limbs, two's complement.
    private readonly ulong _l0, _l1, _l2, _l3, _l4, _l5;

    private Int384(ulong l0, ulong l1, ulong l2, ulong l3, ulong l4, ulong l5)
    {
        _l0 = l0; _l1 = l1; _l2 = l2; _l3 = l3; _l4 = l4; _l5 = l5;
    }

    private Int384(ReadOnlySpan<ulong> l) : this(l[0], l[1], l[2], l[3], l[4], l[5]) { }

    /// <summary>Zero.</summary>
    public static Int384 Zero => default;

    /// <summary>One.</summary>
    public static Int384 One => new(1, 0, 0, 0, 0, 0);

    /// <summary>Largest value, 2^383 - 1.</summary>
    public static Int384 MaxValue => new(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, long.MaxValue);

    /// <summary>Smallest value, -2^383.</summary>
    public static Int384 MinValue => new(0, 0, 0, 0, 0, 1UL << 63);

    /// <summary>-1, 0 or +1.</summary>
    public int Sign
    {
        get
        {
            if ((long)_l5 < 0) return -1;
            return (_l0 | _l1 | _l2 | _l3 | _l4 | _l5) == 0 ? 0 : 1;
        }
    }

    /// <summary>True if the value is zero.</summary>
    public bool IsZero => (_l0 | _l1 | _l2 | _l3 | _l4 | _l5) == 0;

    /// <summary>Number of bits of the magnitude (0 for zero).</summary>
    public int BitLength
    {
        get
        {
            Span<ulong> m = stackalloc ulong[Limbs];
            Magnitude(m);
            for (int i = Limbs - 1; i >= 0; i--)
                if (m[i] != 0) return i * 64 + 64 - BitOperations.LeadingZeroCount(m[i]);
            return 0;
        }
    }

    private void Store(Span<ulong> d)
    {
        d[0] = _l0; d[1] = _l1; d[2] = _l2; d[3] = _l3; d[4] = _l4; d[5] = _l5;
    }

    // Writes |this| into m. |MinValue| = 2^383 fits as an unsigned magnitude.
    private void Magnitude(Span<ulong> m)
    {
        Store(m);
        if ((long)_l5 < 0) NegateInPlace(m);
    }

    private static void NegateInPlace(Span<ulong> m)
    {
        ulong carry = 1;
        for (int i = 0; i < m.Length; i++)
        {
            ulong v = ~m[i] + carry;
            carry = (carry != 0 && v == 0) ? 1UL : 0UL;
            m[i] = v;
        }
    }

    /// <summary>Converts a 64-bit integer.</summary>
    public static implicit operator Int384(long v)
    {
        ulong ext = v < 0 ? ulong.MaxValue : 0;
        return new((ulong)v, ext, ext, ext, ext, ext);
    }

    /// <summary>Converts a 128-bit integer.</summary>
    public static implicit operator Int384(Int128 v)
    {
        ulong lo = (ulong)(v & ulong.MaxValue);
        ulong hi = (ulong)(v >> 64);
        ulong ext = v < 0 ? ulong.MaxValue : 0;
        return new(lo, hi, ext, ext, ext, ext);
    }

    /// <summary>Size of the binary representation in bytes.</summary>
    public const int ByteCount = 48;

    /// <summary>Writes the value as 48 bytes, two's complement, little-endian.</summary>
    public void WriteBytes(Span<byte> destination)
    {
        if (destination.Length < ByteCount) throw new ArgumentException("Destination too small.", nameof(destination));
        Span<ulong> l = stackalloc ulong[Limbs];
        Store(l);
        for (int i = 0; i < Limbs; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(i * 8, 8), l[i]);
    }

    /// <summary>Reads a value written by <see cref="WriteBytes"/>.</summary>
    public static Int384 ReadBytes(ReadOnlySpan<byte> source)
    {
        if (source.Length < ByteCount) throw new ArgumentException("Source too small.", nameof(source));
        Span<ulong> l = stackalloc ulong[Limbs];
        for (int i = 0; i < Limbs; i++)
            l[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(i * 8, 8));
        return new Int384(l);
    }

    /// <summary>Converts from <see cref="BigInteger"/>; throws if out of range.</summary>
    public static explicit operator Int384(BigInteger v)
    {
        if (v.GetBitLength() > 383 && v != -(BigInteger.One << 383))
            throw new OverflowException("Value does not fit in Int384.");
        Span<byte> bytes = stackalloc byte[48];
        bytes.Fill(v.Sign < 0 ? (byte)0xFF : (byte)0);
        if (!v.TryWriteBytes(bytes, out _, isUnsigned: false, isBigEndian: false))
            throw new OverflowException("Value does not fit in Int384.");
        Span<ulong> l = stackalloc ulong[Limbs];
        for (int i = 0; i < Limbs; i++)
            l[i] = BitConverter.ToUInt64(bytes.Slice(i * 8, 8));
        return new Int384(l);
    }

    /// <summary>Converts to <see cref="BigInteger"/>.</summary>
    public static implicit operator BigInteger(Int384 v)
    {
        Span<byte> bytes = stackalloc byte[48];
        Span<ulong> l = stackalloc ulong[Limbs];
        v.Store(l);
        for (int i = 0; i < Limbs; i++)
            BitConverter.TryWriteBytes(bytes.Slice(i * 8, 8), l[i]);
        return new BigInteger(bytes, isUnsigned: false, isBigEndian: false);
    }

    /// <summary>Approximate double value (relative error below 2^-52; for display and filters only).</summary>
    public static explicit operator double(Int384 v)
    {
        Span<ulong> m = stackalloc ulong[Limbs];
        v.Magnitude(m);
        int k = UsedLimbs(m) - 1;
        if (k < 0) return 0;
        double r = m[k];
        if (k >= 1) r = r * 18446744073709551616.0 + m[k - 1];
        if (k >= 2) r = r * 18446744073709551616.0 + m[k - 2];
        int lowLimbs = Math.Max(0, k - 2);
        r = Math.ScaleB(r, 64 * lowLimbs);
        return v.Sign < 0 ? -r : r;
    }

    /// <summary>Checked addition.</summary>
    public static Int384 operator +(Int384 a, Int384 b)
    {
        Span<ulong> x = stackalloc ulong[Limbs];
        Span<ulong> y = stackalloc ulong[Limbs];
        a.Store(x);
        b.Store(y);
        ulong carry = 0;
        for (int i = 0; i < Limbs; i++)
        {
            ulong s = x[i] + y[i];
            ulong c1 = s < x[i] ? 1UL : 0UL;
            ulong s2 = s + carry;
            ulong c2 = s2 < s ? 1UL : 0UL;
            x[i] = s2;
            carry = c1 | c2;
        }
        var r = new Int384(x);
        bool sa = (long)a._l5 < 0, sb = (long)b._l5 < 0, sr = (long)r._l5 < 0;
        if (sa == sb && sr != sa) throw new OverflowException("Int384 addition overflow.");
        return r;
    }

    /// <summary>Checked negation.</summary>
    public static Int384 operator -(Int384 a)
    {
        if (a == MinValue) throw new OverflowException("Int384 negation overflow.");
        Span<ulong> x = stackalloc ulong[Limbs];
        a.Store(x);
        NegateInPlace(x);
        return new Int384(x);
    }

    /// <summary>Checked subtraction.</summary>
    public static Int384 operator -(Int384 a, Int384 b)
    {
        if (b == MinValue)
        {
            // a - MinValue = a + 2^383 only fits for negative a.
            if (a.Sign >= 0) throw new OverflowException("Int384 subtraction overflow.");
            return (a + MaxValue) + One;
        }
        return a + (-b);
    }

    /// <summary>Checked multiplication.</summary>
    public static Int384 operator *(Int384 a, Int384 b)
    {
        Span<ulong> x = stackalloc ulong[Limbs];
        Span<ulong> y = stackalloc ulong[Limbs];
        Span<ulong> p = stackalloc ulong[2 * Limbs];
        a.Magnitude(x);
        b.Magnitude(y);
        p.Clear();
        int nx = UsedLimbs(x), ny = UsedLimbs(y);
        for (int i = 0; i < nx; i++)
        {
            ulong carry = 0;
            ulong xi = x[i];
            if (xi == 0) continue;
            for (int j = 0; j < ny; j++)
            {
                ulong hi = Math.BigMul(xi, y[j], out ulong lo);
                ulong t = p[i + j] + lo;
                hi += t < lo ? 1UL : 0UL;
                t += carry;
                hi += t < carry ? 1UL : 0UL;
                p[i + j] = t;
                carry = hi;
            }
            int k = i + ny;
            while (carry != 0)
            {
                ulong t = p[k] + carry;
                carry = t < carry ? 1UL : 0UL;
                p[k] = t;
                k++;
            }
        }
        for (int i = Limbs; i < 2 * Limbs; i++)
            if (p[i] != 0) throw new OverflowException("Int384 multiplication overflow.");
        bool negative = (a.Sign < 0) != (b.Sign < 0);
        Span<ulong> r = p[..Limbs];
        if ((long)r[Limbs - 1] < 0)
        {
            // Magnitude ≥ 2^383: only -2^383 is representable.
            bool isMin = r[Limbs - 1] == 1UL << 63 && r[0] == 0 && r[1] == 0 && r[2] == 0 && r[3] == 0 && r[4] == 0;
            if (!(negative && isMin)) throw new OverflowException("Int384 multiplication overflow.");
            return MinValue;
        }
        if (negative) NegateInPlace(r);
        return new Int384(r);
    }

    private static int UsedLimbs(ReadOnlySpan<ulong> m)
    {
        int n = m.Length;
        while (n > 0 && m[n - 1] == 0) n--;
        return n;
    }

    /// <summary>Exact product of two 128-bit integers.</summary>
    public static Int384 Multiply(Int128 a, Int128 b) => (Int384)a * b;

    /// <inheritdoc />
    public int CompareTo(Int384 other)
    {
        int sa = Sign, sb = other.Sign;
        if (sa != sb) return sa.CompareTo(sb);
        // Same sign: two's complement compares like unsigned.
        Span<ulong> x = stackalloc ulong[Limbs];
        Span<ulong> y = stackalloc ulong[Limbs];
        Store(x);
        other.Store(y);
        for (int i = Limbs - 1; i >= 0; i--)
            if (x[i] != y[i]) return x[i] < y[i] ? -1 : 1;
        return 0;
    }

    /// <inheritdoc />
    public bool Equals(Int384 other) =>
        _l0 == other._l0 && _l1 == other._l1 && _l2 == other._l2 &&
        _l3 == other._l3 && _l4 == other._l4 && _l5 == other._l5;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Int384 o && Equals(o);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_l0, _l1, _l2, _l3, _l4, _l5);

    /// <summary>Equality.</summary>
    public static bool operator ==(Int384 a, Int384 b) => a.Equals(b);

    /// <summary>Inequality.</summary>
    public static bool operator !=(Int384 a, Int384 b) => !a.Equals(b);

    /// <summary>Less than.</summary>
    public static bool operator <(Int384 a, Int384 b) => a.CompareTo(b) < 0;

    /// <summary>Greater than.</summary>
    public static bool operator >(Int384 a, Int384 b) => a.CompareTo(b) > 0;

    /// <summary>Less than or equal.</summary>
    public static bool operator <=(Int384 a, Int384 b) => a.CompareTo(b) <= 0;

    /// <summary>Greater than or equal.</summary>
    public static bool operator >=(Int384 a, Int384 b) => a.CompareTo(b) >= 0;

    /// <inheritdoc />
    public override string ToString() => ((BigInteger)this).ToString(CultureInfo.InvariantCulture);
}
