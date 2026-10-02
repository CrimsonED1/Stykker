namespace Stykker.NanoCut.Geometry3D;

/// <summary>Axis-aligned box in nm (double, conservatively inflated).</summary>
internal readonly record struct Box3(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
{
    public static readonly Box3 Empty = new(double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue);

    public bool Overlaps(in Box3 o) =>
        MinX <= o.MaxX && o.MinX <= MaxX && MinY <= o.MaxY && o.MinY <= MaxY && MinZ <= o.MaxZ && o.MinZ <= MaxZ;

    public Box3 Union(in Box3 o) => new(
        Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Min(MinZ, o.MinZ),
        Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY), Math.Max(MaxZ, o.MaxZ));

    public double Center(int axis) => axis switch { 0 => (MinX + MaxX) / 2, 1 => (MinY + MaxY) / 2, _ => (MinZ + MaxZ) / 2 };
}

/// <summary>
/// A convex planar polygon in plane-based representation: a supporting plane plus one plane per edge
/// (polygon interior on the negative side of every edge plane). Every plane is defined by grid points,
/// so splitting never creates new coordinates – new vertices are exact three-plane intersections.
/// </summary>
internal sealed class Face3
{
    public Face3(Plane3 support, Plane3[] edges, Point3[] vertices)
    {
        Support = support;
        Edges = edges;
        Vertices = vertices;
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
        double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        foreach (var v in vertices)
        {
            x0 = Math.Min(x0, v.X); y0 = Math.Min(y0, v.Y); z0 = Math.Min(z0, v.Z);
            x1 = Math.Max(x1, v.X); y1 = Math.Max(y1, v.Y); z1 = Math.Max(z1, v.Z);
        }
        const double pad = 1.0; // nm; approximations of exact vertices are far more accurate
        Box = new Box3(x0 - pad, y0 - pad, z0 - pad, x1 + pad, y1 + pad, z1 + pad);
    }

    public Plane3 Support { get; }

    /// <summary>Edges[i] runs from Vertices[i] to Vertices[i + 1].</summary>
    public Plane3[] Edges { get; }

    public Point3[] Vertices { get; }

    public Box3 Box { get; }

    private double[]? _planesD;

    /// <summary>Plane coefficients as doubles for the floating-point filters: support first, then the edges (4 each).</summary>
    public double[] PlanesD
    {
        get
        {
            if (_planesD is not null) return _planesD;
            var d = new double[4 * (Edges.Length + 1)];
            Put(0, Support);
            for (int i = 0; i < Edges.Length; i++) Put(4 * (i + 1), Edges[i]);
            return _planesD = d;

            void Put(int o, in Plane3 p)
            {
                d[o] = (double)p.Nx; d[o + 1] = (double)p.Ny; d[o + 2] = (double)p.Nz; d[o + 3] = (double)p.D;
            }
        }
    }

    /// <summary>
    /// Creates a face from coplanar grid points in counter-clockwise order (seen from the outside).
    /// Collinear vertices are allowed; the polygon must be convex.
    /// </summary>
    public static Face3 FromGrid(IReadOnlyList<Vec3> pts)
    {
        int n = pts.Count;
        if (n < 3) throw new ArgumentException("A face needs at least three vertices.");
        Plane3 support = default;
        bool found = false;
        for (int i = 1; i + 1 < n && !found; i++)
        {
            support = Plane3.FromPoints(pts[0], pts[i], pts[i + 1]);
            found = !support.IsDegenerate;
        }
        if (!found) throw new ArgumentException("Degenerate face (all vertices collinear).");
        support = support.Canonical();
        foreach (var p in pts)
            if (Predicates.Side(support, p) != 0) throw new ArgumentException("Face vertices are not coplanar.");

        // Edge plane through a, b and a + e_k, where k is the axis most aligned with the face normal.
        int k = Abs(support.Nx) >= Abs(support.Ny) && Abs(support.Nx) >= Abs(support.Nz) ? 0
              : Abs(support.Ny) >= Abs(support.Nz) ? 1 : 2;
        var edges = new Plane3[n];
        var verts = new Point3[n];
        for (int i = 0; i < n; i++)
        {
            Vec3 a = pts[i], b = pts[(i + 1) % n];
            if (a == b) throw new ArgumentException("Duplicate consecutive vertices.");
            Vec3 off = k switch { 0 => new Vec3(a.X + 1, a.Y, a.Z), 1 => new Vec3(a.X, a.Y + 1, a.Z), _ => new Vec3(a.X, a.Y, a.Z + 1) };
            var e = Plane3.FromPoints(a, b, off).Canonical();
            // Interior on the negative side: test with a vertex not on this edge's line.
            int side = 0;
            for (int j = 0; j < n && side == 0; j++) side = Predicates.Side(e, pts[j]);
            if (side > 0) e = e.Flipped();
            if (side == 0) throw new ArgumentException("Degenerate face.");
            edges[i] = e;
            verts[i] = new Point3(a);
        }
        return new Face3(support, edges, verts);
    }

    private static Int128 Abs(Int128 v) => v < 0 ? -v : v;

    /// <summary>The same face with opposite orientation.</summary>
    public Face3 Reversed()
    {
        int n = Vertices.Length;
        var v = new Point3[n];
        var e = new Plane3[n];
        for (int j = 0; j < n; j++)
        {
            v[j] = Vertices[n - 1 - j];
            e[j] = Edges[(2 * n - 2 - j) % n];
        }
        return new Face3(Support.Flipped(), e, v);
    }

    /// <summary>
    /// Splits the face by <paramref name="plane"/>. Returns false if the plane does not cross the face;
    /// then the face lies entirely on side <paramref name="side"/> (+1, -1, or 0 if coplanar).
    /// </summary>
    public bool Split(in Plane3 plane, out Face3? front, out Face3? back, out int side)
    {
        int n = Vertices.Length;
        Span<int> s = n <= 64 ? stackalloc int[n] : new int[n];
        bool pos = false, neg = false;
        for (int i = 0; i < n; i++)
        {
            s[i] = Vertices[i].SideOf(plane);
            pos |= s[i] > 0;
            neg |= s[i] < 0;
        }
        front = back = null;
        if (!(pos && neg))
        {
            side = pos ? 1 : neg ? -1 : 0;
            return false;
        }
        side = 0;
        // A convex polygon crosses a plane at exactly two edges (strict sign changes).
        int e1 = -1, e2 = -1;
        Point3 x1 = default, x2 = default;
        for (int i = 0; i < n; i++)
        {
            int j = i + 1 == n ? 0 : i + 1;
            if ((s[i] < 0 && s[j] > 0) || (s[i] > 0 && s[j] < 0))
            {
                var h = Plane3.Intersect(Support, Edges[i], plane)
                        ?? throw new InvalidOperationException("Edge parallel to a crossing plane.");
                if (e1 < 0) { e1 = i; x1 = new Point3(h); }
                else { e2 = i; x2 = new Point3(h); }
            }
        }
        back = Piece(s, 1, plane, e1, x1, e2, x2);
        front = Piece(s, -1, plane.Flipped(), e1, x1, e2, x2);
        return true;
    }

    // Keeps the part where sign·s <= 0; the cut edge gets plane k (interior on its negative side). Crossing points x1, x2
    // lie on edges e1, e2 (-1 if absent). Arrays are sized exactly (count first, then fill).
    private Face3 Piece(ReadOnlySpan<int> s, int sign, in Plane3 k, int e1, in Point3 x1, int e2, in Point3 x2)
    {
        int n = Vertices.Length;
        int count = (e1 >= 0 ? 1 : 0) + (e2 >= 0 ? 1 : 0);
        for (int i = 0; i < n; i++) if (sign * s[i] <= 0) count++;
        var verts = new Point3[count];
        var edges = new Plane3[count];
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            int j = i + 1 == n ? 0 : i + 1;
            int si = sign * s[i], sj = sign * s[j];
            if (si <= 0)
            {
                verts[m] = Vertices[i];
                edges[m++] = si == 0 && sj > 0 ? k : Edges[i];
            }
            if (i == e1 || i == e2)
            {
                verts[m] = i == e1 ? x1 : x2;
                edges[m++] = si < 0 ? k : Edges[i];
            }
        }
        return new Face3(Support, edges, verts);
    }
}
