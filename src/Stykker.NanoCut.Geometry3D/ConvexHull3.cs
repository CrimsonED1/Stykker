namespace Stykker.NanoCut.Geometry3D;

/// <summary>Exact 3D convex hull of grid points (incremental, exact orient3d).</summary>
public static class ConvexHull3
{
    private const double Epsilon = 1.0 / (1L << 53);
    private const double MaxCoord = Units.MaxCoordinate;

    /// <summary>Convex hull as a solid with triangular faces. Throws if the points do not span a volume.</summary>
    public static Solid Compute(IEnumerable<Vec3> points)
    {
        var tris = Triangles(points, out var pts);
        var faces = new List<Face3>(tris.Count);
        foreach (var (a, b, c) in tris) faces.Add(Face3.FromGrid([pts[a], pts[b], pts[c]]));
        return new Solid(FaceMerge.MergeAll(faces));
    }

    /// <summary>Hull triangles (indices into <paramref name="pts"/>), counter-clockwise seen from outside.</summary>
    internal static List<(int A, int B, int C)> Triangles(IEnumerable<Vec3> points, out Vec3[] pts)
    {
        pts = points.Distinct().ToArray();
        if (pts.Length < 4) throw new ArgumentException("A hull needs at least four points.");
        var p = pts;

        // Initial tetrahedron.
        int i0 = 0, i1 = -1, i2 = -1, i3 = -1;
        for (int i = 1; i < p.Length && i1 < 0; i++) if (p[i] != p[i0]) i1 = i;
        for (int i = 1; i < p.Length && i2 < 0; i++)
            if (i != i1 && !Plane3.FromPoints(p[i0], p[i1], p[i]).IsDegenerate) i2 = i;
        if (i2 < 0) throw new ArgumentException("Points are collinear.");
        for (int i = 1; i < p.Length && i3 < 0; i++)
            if (i != i1 && i != i2 && Predicates.Orient3D(p[i0], p[i1], p[i2], p[i]) != 0) i3 = i;
        if (i3 < 0) throw new ArgumentException("Points are coplanar.");

        var faces = new List<(int A, int B, int C)>();
        var alive = new List<bool>();
        var outside = new List<List<int>?>();
        var edgeFace = new Dictionary<(int, int), int>();
        // Per face: exact plane (outward normal) and its double approximation for the filtered side test.
        var planes = new List<Plane3>();
        var planesD = new List<(double X, double Y, double Z, double D, double Bound)>();

        int AddFace(int a, int b, int c)
        {
            int id = faces.Count;
            faces.Add((a, b, c));
            alive.Add(true);
            outside.Add(null);
            edgeFace[(a, b)] = id;
            edgeFace[(b, c)] = id;
            edgeFace[(c, a)] = id;
            // Same orientation as Orient3D(a, b, c, q): positive above the counter-clockwise triangle (outside).
            var pl = Plane3.FromPoints(p[a], p[b], p[c]);
            planes.Add(pl);
            double nx = (double)pl.Nx, ny = (double)pl.Ny, nz = (double)pl.Nz;
            // |error| ≤ rounding of n and d (relative 2^-53 each) plus rounding in the sum: 8 ulp of the magnitude.
            planesD.Add((nx, ny, nz, (double)pl.D, (Math.Abs(nx) + Math.Abs(ny) + Math.Abs(nz)) * MaxCoord));
            return id;
        }

        // Height of q above face f as a double (for choosing the farthest point; the sign comes from Above).
        double Height(int f, int q)
        {
            var pl = planesD[f];
            return pl.X * p[q].X + pl.Y * p[q].Y + pl.Z * p[q].Z + pl.D;
        }

        // Exact: q strictly above face f (floating-point filter, Int128 fallback).
        bool Above(int f, int q)
        {
            var pl = planesD[f];
            double v = pl.X * p[q].X + pl.Y * p[q].Y + pl.Z * p[q].Z + pl.D;
            double bound = (pl.Bound + Math.Abs(pl.D)) * 8 * Epsilon;
            if (v > bound) return true;
            if (v < -bound) return false;
            return Predicates.Side(planes[f], p[q]) > 0;
        }

        // Orient the tetrahedron so that every face has the fourth point below it.
        if (Predicates.Orient3D(p[i0], p[i1], p[i2], p[i3]) > 0) (i1, i2) = (i2, i1);
        var initial = new List<int> { AddFace(i0, i1, i2), AddFace(i0, i3, i1), AddFace(i1, i3, i2), AddFace(i2, i3, i0) };

        // Quickhull with conflict lists: every point waits in the outside set of one face it lies strictly above.
        void Assign(IEnumerable<int> points, List<int> candidates)
        {
            foreach (int q in points)
                foreach (int f in candidates)
                    if (Above(f, q))
                    {
                        (outside[f] ??= []).Add(q);
                        break;
                    }
        }
        Assign(Enumerable.Range(0, p.Length).Where(i => i != i0 && i != i1 && i != i2 && i != i3), initial);

        var pending = new Stack<int>(initial);
        var stack = new Stack<int>();
        var visible = new List<int>();
        var visibleSet = new HashSet<int>();
        var horizon = new List<(int, int)>();
        var orphans = new List<int>();
        while (pending.Count > 0)
        {
            int f0 = pending.Pop();
            if (!alive[f0] || outside[f0] is not { Count: > 0 } waiting) continue;
            // Farthest point (largest height; all heights refer to the same face, so they compare directly).
            int q = waiting[0];
            double best = Height(f0, q);
            foreach (int r in waiting)
            {
                double h = Height(f0, r);
                if (h > best) { best = h; q = r; }
            }
            // Visible region: connected set of faces with q strictly above, found by flooding from f0.
            visible.Clear();
            visibleSet.Clear();
            stack.Clear();
            stack.Push(f0);
            visibleSet.Add(f0);
            while (stack.Count > 0)
            {
                int f = stack.Pop();
                visible.Add(f);
                var (a, b, c) = faces[f];
                Visit(b, a);
                Visit(c, b);
                Visit(a, c);
            }
            horizon.Clear();
            orphans.Clear();
            foreach (int f in visible)
            {
                var (a, b, c) = faces[f];
                if (!visibleSet.Contains(edgeFace[(b, a)])) horizon.Add((a, b));
                if (!visibleSet.Contains(edgeFace[(c, b)])) horizon.Add((b, c));
                if (!visibleSet.Contains(edgeFace[(a, c)])) horizon.Add((c, a));
                if (outside[f] is { } list) foreach (int r in list) if (r != q) orphans.Add(r);
                outside[f] = null;
            }
            foreach (int f in visible)
            {
                alive[f] = false;
                var (a, b, c) = faces[f];
                edgeFace.Remove((a, b)); edgeFace.Remove((b, c)); edgeFace.Remove((c, a));
            }
            var created = new List<int>(horizon.Count);
            foreach (var (u, v) in horizon) created.Add(AddFace(u, v, q));
            Assign(orphans, created);
            foreach (int f in created) if (outside[f] is { Count: > 0 }) pending.Push(f);

            void Visit(int u, int v)
            {
                int g = edgeFace[(u, v)];
                if (visibleSet.Contains(g) || !Above(g, q)) return;
                visibleSet.Add(g);
                stack.Push(g);
            }
        }
        return faces.Where((_, i) => alive[i]).ToList();
    }
}
