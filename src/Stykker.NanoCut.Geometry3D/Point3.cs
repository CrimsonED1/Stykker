using System.Numerics;

namespace Stykker.NanoCut.Geometry3D;

/// <summary>
/// An exact vertex: either a grid point (input vertices) or the intersection of three planes
/// (vertices created by Booleans, stored homogeneously and never rounded).
/// </summary>
public readonly struct Point3
{
    private readonly Vec3 _grid;
    private readonly Exact? _exact;

    private sealed class Exact(HomogeneousPoint3 h)
    {
        public readonly HomogeneousPoint3 H = h;
        public readonly double X = (double)h.X / (double)h.W;
        public readonly double Y = (double)h.Y / (double)h.W;
        public readonly double Z = (double)h.Z / (double)h.W;
    }

    /// <summary>A grid vertex.</summary>
    public Point3(Vec3 grid)
    {
        _grid = grid;
        _exact = null;
    }

    /// <summary>A homogeneous vertex (W must be positive).</summary>
    public Point3(HomogeneousPoint3 h)
    {
        if (h.W.Sign <= 0) throw new ArgumentException("W must be positive.", nameof(h));
        _grid = default;
        _exact = new Exact(h);
    }

    /// <summary>Reference identity of an exact vertex (shared between fragments created by the same split), or null.</summary>
    internal object? Identity => _exact;

    /// <summary>True if the vertex is a grid point.</summary>
    public bool IsGrid => _exact is null;

    /// <summary>The grid point (only valid if <see cref="IsGrid"/>).</summary>
    public Vec3 Grid => _grid;

    /// <summary>X in nm (approximate for homogeneous vertices, relative error ~1e-16).</summary>
    public double X => _exact?.X ?? _grid.X;

    /// <summary>Y in nm (approximate for homogeneous vertices).</summary>
    public double Y => _exact?.Y ?? _grid.Y;

    /// <summary>Z in nm (approximate for homogeneous vertices).</summary>
    public double Z => _exact?.Z ?? _grid.Z;

    /// <summary>Exact homogeneous coordinates (W &gt; 0).</summary>
    public HomogeneousPoint3 Homogeneous => _exact?.H ?? new HomogeneousPoint3(_grid.X, _grid.Y, _grid.Z, 1);

    /// <summary>Exact homogeneous coordinates as big integers (W &gt; 0).</summary>
    public (BigInteger X, BigInteger Y, BigInteger Z, BigInteger W) Big
    {
        get
        {
            if (_exact is null) return (_grid.X, _grid.Y, _grid.Z, BigInteger.One);
            var h = _exact.H;
            return (h.X, h.Y, h.Z, h.W);
        }
    }

    /// <summary>Exact side of the vertex relative to a plane: +1, 0, -1.</summary>
    public int SideOf(in Plane3 plane) =>
        _exact is null ? Predicates.Side(plane, _grid) : Predicates.Side(plane, _exact.H);

    /// <summary>Exact equality of the represented points.</summary>
    public bool SameAs(in Point3 o)
    {
        if (_exact is null && o._exact is null) return _grid == o._grid;
        var a = Big;
        var b = o.Big;
        return a.X * b.W == b.X * a.W && a.Y * b.W == b.Y * a.W && a.Z * b.W == b.Z * a.W;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"({Units.NmToMm(X):0.#########}, {Units.NmToMm(Y):0.#########}, {Units.NmToMm(Z):0.#########}) mm{(IsGrid ? "" : " (exact)")}";
}
