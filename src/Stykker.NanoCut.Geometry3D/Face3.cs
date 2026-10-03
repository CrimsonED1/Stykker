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
        // The box must enclose the exact face, but a homogeneous vertex is only known through its double approximation
        // X/W, which carries about four ulps of relative error. With the documented coordinate bound |c| <= 2^31 that is
        // 2^31 * 4 * 2^-52 nm <= 1.9e-6 nm, so a pad of 1e-5 nm is a safe bound with a factor of five in hand.
        //
        // It used to be 1.0 nm -- five decades more than needed. Because neighbouring faces' boxes then overlapped by
        // 2 nm, the BVH handed the candidate loop faces that Separated immediately discarded: the counters showed that
        // only 55.5 % of the fetched candidates survived to a real test, so nearly half of the candidate work was
        // induced by this constant alone. The pad is load-bearing in three places (the BVH query, the outside decision
        // in SolidBoolean.ProcessFace and the split decision in Face3.Split), so it is derived, not guessed.
        const double pad = 1e-5;
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
            // Published atomically: classification runs in parallel and several threads may fill the cache at once.
            return Interlocked.CompareExchange(ref _planesD, d, null) ?? d;

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
        if (KernelStats.Counting)
        {
            var st = KernelStats.Mine;
            st.HullFaces++;
            st.HullFromGridEdges += n;
        }
        for (int i = 0; i < n; i++)
        {
            Vec3 a = pts[i], b = pts[(i + 1) % n];
            if (a == b) throw new ArgumentException("Duplicate consecutive vertices.");
            var e = Plane3.EdgePlane(a, b, k);
            int next = i + 1 == n ? 0 : i + 1, side = 0;
            // Bounded convexity check. FromGrid is documented to take a convex polygon and has always taken that on
            // trust: the side search stopped at the first vertex off the plane, so a polygon concave past that vertex
            // produced a face whose plane held fewer corners than the polygon, silently. Full checking is O(n^2) per
            // face, so only the first ConvexCheckLimit vertices are compared; this catches a careless caller, it does not
            // prove convexity (a concave polygon whose first vertices lie in its kernel passes). A caller that needs the
            // proof, like the coplanar merge in ConvexHull3.FacesFromTriangles, checks the whole loop itself.
            int check = n <= ConvexCheckLimit ? n : ConvexCheckLimit;
            for (int j = 0; j < check; j++)
            {
                if (j == i || j == next) continue;   // on the edge plane by construction
                int s = Predicates.Side(e, pts[j]);
                if (s == 0) continue;                // collinear vertices are allowed
                if (side == 0) side = s;
                else if (s != side) throw new ArgumentException("Face polygon is not convex.");
            }
            // The side itself is still searched over every vertex, as before the bounded check: a polygon whose first
            // vertices all lie on this edge's line is not degenerate.
            for (int j = check; j < n && side == 0; j++)
                if (j != i && j != next) side = Predicates.Side(e, pts[j]);
            if (side == 0) throw new ArgumentException("Degenerate face.");
            if (side > 0) e = e.Flipped();
            edges[i] = e;
            verts[i] = new Point3(a);
        }
        return new Face3(support, edges, verts);
    }

    /// <summary>How many vertices of a face polygon are tested for convexity (see the call site for why it is bounded).</summary>
    private const int ConvexCheckLimit = 12;

    /// <summary>
    /// The single-triangle case. Identical to <see cref="FromGrid"/> on three points, but without allocating the
    /// three-element array and without the interface-dispatched access -- this is one call per hull triangle, so it
    /// dominates the hull's face-building phase when the hull is finely tessellated.
    /// </summary>
    public static Face3 FromTriangle(Vec3 a, Vec3 b, Vec3 c)
    {
        if (a == b || b == c || c == a) throw new ArgumentException("Duplicate consecutive vertices.");
        var support = Plane3.FromPoints(a, b, c).Canonical();
        int k = Abs(support.Nx) >= Abs(support.Ny) && Abs(support.Nx) >= Abs(support.Nz) ? 0
              : Abs(support.Ny) >= Abs(support.Nz) ? 1 : 2;
        Span<Vec3> v = [a, b, c];
        var edges = new Plane3[3]
        {
            OrientedEdgePlane(Plane3.EdgePlane(a, b, k), v, 0),
            OrientedEdgePlane(Plane3.EdgePlane(b, c, k), v, 1),
            OrientedEdgePlane(Plane3.EdgePlane(c, a, k), v, 2),
        };
        if (KernelStats.Counting)
        {
            var st = KernelStats.Mine;
            st.HullFaces++;
            st.HullFromGridEdges += 3;
        }
        return new Face3(support, edges, [new Point3(a), new Point3(b), new Point3(c)]);
    }

    /// <summary>
    /// Orients an edge plane so that the polygon interior is on its negative side. Vertices <c>i</c> and <c>i+1</c>
    /// lie on the plane by construction, so the side search skips them: for a triangle that leaves one Int128 test
    /// instead of three, and it never has to scan the whole polygon.
    /// </summary>
    private static Plane3 OrientedEdgePlane(Plane3 e, ReadOnlySpan<Vec3> pts, int i)
    {
        int n = pts.Length, next = i + 1 == n ? 0 : i + 1;
        for (int j = 0; j < n; j++)
        {
            if (j == i || j == next) continue;
            int side = Predicates.Side(e, pts[j]);
            if (side > 0) return e.Flipped();
            if (side < 0) return e;
        }
        throw new ArgumentException("Degenerate face.");
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
        double pnx = (double)plane.Nx, pny = (double)plane.Ny, pnz = (double)plane.Nz, pd = (double)plane.D;
        bool pos = false, neg = false;
        for (int i = 0; i < n; i++)
        {
            s[i] = Vertices[i].SideOf(plane, pnx, pny, pnz, pd);
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
                else if (e2 < 0) { e2 = i; x2 = new Point3(h); }
                else throw new InvalidOperationException("Face is not convex (more than two crossings with a plane).");
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
