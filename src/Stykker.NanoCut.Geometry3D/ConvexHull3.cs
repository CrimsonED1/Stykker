namespace Stykker.NanoCut.Geometry3D;

/// <summary>Exact 3D convex hull of grid points (incremental, exact orient3d).</summary>
public static class ConvexHull3
{
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
        var edgeFace = new Dictionary<(int, int), int>();

        void AddFace(int a, int b, int c)
        {
            int id = faces.Count;
            faces.Add((a, b, c));
            alive.Add(true);
            edgeFace[(a, b)] = id;
            edgeFace[(b, c)] = id;
            edgeFace[(c, a)] = id;
        }

        // Orient the tetrahedron so that every face has the fourth point below it.
        if (Predicates.Orient3D(p[i0], p[i1], p[i2], p[i3]) > 0) (i1, i2) = (i2, i1);
        AddFace(i0, i1, i2);
        AddFace(i0, i3, i1);
        AddFace(i1, i3, i2);
        AddFace(i2, i3, i0);

        var order = Enumerable.Range(0, p.Length).Where(i => i != i0 && i != i1 && i != i2 && i != i3).ToArray();
        new Random(12345).Shuffle(order);
        var visible = new List<int>();
        var horizon = new List<(int, int)>();
        foreach (int q in order)
        {
            visible.Clear();
            for (int f = 0; f < faces.Count; f++)
            {
                if (!alive[f]) continue;
                var (a, b, c) = faces[f];
                if (Predicates.Orient3D(p[a], p[b], p[c], p[q]) > 0) visible.Add(f);
            }
            if (visible.Count == 0) continue;
            var vis = new HashSet<int>(visible);
            horizon.Clear();
            foreach (int f in visible)
            {
                var (a, b, c) = faces[f];
                foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                    if (!vis.Contains(edgeFace[(v, u)])) horizon.Add((u, v));
            }
            foreach (int f in visible)
            {
                alive[f] = false;
                var (a, b, c) = faces[f];
                edgeFace.Remove((a, b)); edgeFace.Remove((b, c)); edgeFace.Remove((c, a));
            }
            foreach (var (u, v) in horizon) AddFace(u, v, q);
        }
        return faces.Where((_, i) => alive[i]).ToList();
    }
}
