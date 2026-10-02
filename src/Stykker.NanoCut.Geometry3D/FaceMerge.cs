namespace Stykker.NanoCut.Geometry3D;

/// <summary>
/// Merges coplanar convex pieces of one face that share an edge whenever their union is convex. Exact: vertices are
/// compared exactly, convexity is decided with the (filtered) exact side test, and the merged polygon keeps the
/// original edge planes, so no coordinate changes.
/// </summary>
internal static class FaceMerge
{
    /// <summary>Merges faces of a solid that share their supporting plane (all planes at once).</summary>
    public static List<Face3> MergeAll(List<Face3> faces)
    {
        var groups = new Dictionary<Plane3, List<Face3>>();
        foreach (var f in faces)
        {
            if (!groups.TryGetValue(f.Support, out var g)) groups[f.Support] = g = [];
            g.Add(f);
        }
        if (groups.Count == faces.Count) return faces;
        var result = new List<Face3>(faces.Count);
        foreach (var g in groups.Values)
        {
            if (g.Count > 1) MergeCoplanar(g);
            result.AddRange(g);
        }
        return result;
    }

    public static void MergeCoplanar(List<Face3> pieces)
    {
        bool mergedAny = true;
        while (mergedAny && pieces.Count > 1)
        {
            mergedAny = false;
            // Index directed edges by an approximate key; candidates are confirmed exactly.
            var byEdge = new Dictionary<(long, long, long, long, long, long), List<(int Piece, int Edge)>>();
            for (int i = 0; i < pieces.Count; i++)
            {
                var v = pieces[i].Vertices;
                for (int k = 0; k < v.Length; k++)
                {
                    var key = Key(v[k], v[(k + 1) % v.Length]);
                    if (!byEdge.TryGetValue(key, out var list)) byEdge[key] = list = [];
                    list.Add((i, k));
                }
            }
            var dead = new bool[pieces.Count];
            var touched = new bool[pieces.Count];
            for (int i = 0; i < pieces.Count; i++)
            {
                if (dead[i] || touched[i]) continue;
                var a = pieces[i];
                bool done = false;
                for (int k = 0; k < a.Vertices.Length && !done; k++)
                {
                    var u = a.Vertices[k];
                    var w = a.Vertices[(k + 1) % a.Vertices.Length];
                    if (!byEdge.TryGetValue(Key(w, u), out var twins)) continue;
                    foreach (var (j, m) in twins)
                    {
                        if (j == i || dead[j] || touched[j]) continue;
                        var b = pieces[j];
                        var bu = b.Vertices[m];
                        var bw = b.Vertices[(m + 1) % b.Vertices.Length];
                        if (!bu.SameAs(w) || !bw.SameAs(u)) continue;
                        var joined = Join(a, k, b, m);
                        if (joined is null) continue;
                        // Indices of the merged face are stale for this pass: mark it, merge further next pass.
                        pieces[i] = joined;
                        dead[j] = true;
                        touched[i] = true;
                        mergedAny = done = true;
                        break;
                    }
                }
            }
            if (mergedAny)
            {
                var next = new List<Face3>(pieces.Count);
                for (int i = 0; i < pieces.Count; i++) if (!dead[i]) next.Add(pieces[i]);
                pieces.Clear();
                pieces.AddRange(next);
            }
        }
    }

    private static (long, long, long, long, long, long) Key(in Point3 p, in Point3 q) =>
        (Q(p.X), Q(p.Y), Q(p.Z), Q(q.X), Q(q.Y), Q(q.Z));

    // Coordinates of exact vertices are accurate to far below 1/64 nm, so this quantisation is stable for equal points
    // (a rare boundary case only costs a missed merge).
    private static long Q(double v) => (long)Math.Round(v * 64);

    /// <summary>Union of a and b across a's edge k (= b's edge m reversed), or null if it is not convex.</summary>
    private static Face3? Join(Face3 a, int k, Face3 b, int m)
    {
        int na = a.Vertices.Length, nb = b.Vertices.Length;
        var verts = new List<Point3>(na + nb - 2);
        var edges = new List<Plane3>(na + nb - 2);
        // a from the end of the shared edge (k + 1) round to a(k - 1), each vertex with the edge leaving it ...
        for (int t = 1; t < na; t++)
        {
            int idx = (k + t) % na;
            verts.Add(a.Vertices[idx]);
            edges.Add(a.Edges[idx]);
        }
        // ... then b from b(m + 1) = a(k) round to b(m - 1).
        for (int t = 1; t < nb; t++)
        {
            int idx = (m + t) % nb;
            verts.Add(b.Vertices[idx]);
            edges.Add(b.Edges[idx]);
        }
        // Result: a(k+1) … a(k-1), a(k) = b(m+1), b(m+2) … b(m-1), each with the edge plane leaving it; the edge
        // from b(m-1) ends at b(m) = a(k+1), closing the loop. The shared edge is gone.

        // Drop collinear junction vertices: v[i] lies on the line of its incoming edge if its successor does too.
        for (int i = 0; i < verts.Count && verts.Count > 3; i++)
        {
            int prev = (i - 1 + verts.Count) % verts.Count, next = (i + 1) % verts.Count;
            if (verts[next].SideOf(edges[prev]) == 0)
            {
                verts.RemoveAt(i);
                edges.RemoveAt(i);
                i = -1;
            }
        }
        // Convex iff every vertex is on the inner side of every edge plane.
        for (int e = 0; e < edges.Count; e++)
            foreach (var v in verts)
                if (v.SideOf(edges[e]) > 0) return null;
        return new Face3(a.Support, edges.ToArray(), verts.ToArray());
    }
}
