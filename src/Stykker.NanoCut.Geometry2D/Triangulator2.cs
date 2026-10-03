namespace Stykker.NanoCut.Geometry2D;

/// <summary>
/// Exact triangulation and convex decomposition of normalised regions (outer contours counter-clockwise,
/// holes clockwise). Holes are joined to their outer contour by bridges, the resulting weakly simple polygon is
/// ear-clipped with exact predicates, and Hertel–Mehlhorn merging turns the triangles into few convex pieces.
/// Every result is checked: the pieces' exact areas must add up to the region's exact area.
/// </summary>
public static class Triangulator2
{
    /// <summary>Triangles (counter-clockwise) covering the region exactly.</summary>
    public static List<Vec2[]> Triangulate(Region2 region)
    {
        var result = new List<Vec2[]>();
        var norm = region.Normalize();
        foreach (var poly in JoinHoles(norm))
            result.AddRange(EarClip(poly));
        Check(norm, result);
        return result;
    }

    /// <summary>Convex polygons (counter-clockwise, no collinear vertices) covering the region exactly.</summary>
    public static List<Vec2[]> ConvexParts(Region2 region)
    {
        var norm = region.Normalize();
        var result = new List<Vec2[]>();
        foreach (var poly in JoinHoles(norm))
            result.AddRange(MergeConvex(EarClip(poly)));
        Check(norm, result);
        return result;
    }

    private static void Check(Region2 region, List<Vec2[]> pieces)
    {
        Int128 sum = 0;
        foreach (var p in pieces)
        {
            Int128 a = TwiceArea(p);
            if (a <= 0) throw new InvalidOperationException("Triangulation produced a degenerate or inverted piece.");
            sum += a;
        }
        if (sum != region.TwiceAreaNm2)
            throw new InvalidOperationException("Triangulation does not cover the region exactly.");
    }

    private static Int128 TwiceArea(IReadOnlyList<Vec2> p)
    {
        Int128 s = 0;
        for (int i = 0; i < p.Count; i++) s += Vec2.Cross(p[i], p[(i + 1) % p.Count]);
        return s;
    }

    /// <summary>One weakly simple counter-clockwise polygon per outer contour, with its holes bridged in.</summary>
    private static List<List<Vec2>> JoinHoles(Region2 region)
    {
        var outers = region.Contours.Where(c => c.IsCounterClockwise).ToList();
        var holes = region.Contours.Where(c => !c.IsCounterClockwise).ToList();
        var holesOf = outers.ToDictionary(o => o, _ => new List<Contour2>());
        foreach (var h in holes)
        {
            Contour2? best = null;
            foreach (var o in outers)
            {
                if (!Contains(o, h)) continue;
                if (best is null || o.TwiceSignedAreaNm2 < best.TwiceSignedAreaNm2) best = o;
            }
            if (best is null) throw new InvalidOperationException("Hole without enclosing outer contour.");
            holesOf[best].Add(h);
        }

        var result = new List<List<Vec2>>();
        foreach (var o in outers)
        {
            var poly = o.Points.ToArray().ToList();
            var pending = holesOf[o].OrderByDescending(h => h.Points.ToArray().Max(p => p.X)).ToList();
            while (pending.Count > 0)
            {
                var h = pending[0];
                pending.RemoveAt(0);
                poly = Bridge(poly, h.Points.ToArray(), pending);
            }
            result.Add(poly);
        }
        return result;
    }

    // True if contour h lies inside contour o (decided at a vertex of h that is not on o).
    private static bool Contains(Contour2 o, Contour2 h)
    {
        foreach (var p in h.Points)
        {
            int loc = Locate(o, p);
            if (loc != 0) return loc > 0;
        }
        return false;
    }

    // +1 inside, -1 outside, 0 on the boundary.
    private static int Locate(IReadOnlyList<Vec2> c, Vec2 p)
    {
        int winding = 0;
        for (int i = 0; i < c.Count; i++)
        {
            Vec2 a = c[i], b = c[(i + 1) % c.Count];
            int o = Predicates.Orient2D(a, b, p);
            if (o == 0 && Vec2.Dot(a - p, b - p) <= 0) return 0;
            if (a.Y <= p.Y) { if (b.Y > p.Y && o > 0) winding++; }
            else if (b.Y <= p.Y && o < 0) winding--;
        }
        return winding != 0 ? 1 : -1;
    }

    private static List<Vec2> Bridge(List<Vec2> poly, Vec2[] hole, List<Contour2> otherHoles)
    {
        int m = 0;
        for (int i = 1; i < hole.Length; i++)
            if (hole[i].X > hole[m].X || (hole[i].X == hole[m].X && hole[i].Y > hole[m].Y)) m = i;
        Vec2 mp = hole[m];

        var candidates = Enumerable.Range(0, poly.Count)
            .OrderBy(i => (double)(poly[i].X - mp.X) * (poly[i].X - mp.X) + (double)(poly[i].Y - mp.Y) * (poly[i].Y - mp.Y))
            .ToList();
        foreach (int k in candidates)
        {
            Vec2 v = poly[k];
            if (v == mp) continue;
            if (!InWedge(poly[(k - 1 + poly.Count) % poly.Count], v, poly[(k + 1) % poly.Count], mp)) continue;
            if (!InWedge(hole[(m - 1 + hole.Length) % hole.Length], mp, hole[(m + 1) % hole.Length], v)) continue;
            if (Blocked(v, mp, poly) || Blocked(v, mp, hole) || otherHoles.Any(h => Blocked(v, mp, h.Points.ToArray()))) continue;

            var merged = new List<Vec2>(poly.Count + hole.Length + 2);
            merged.AddRange(poly.Take(k + 1));
            for (int i = 0; i <= hole.Length; i++) merged.Add(hole[(m + i) % hole.Length]);
            merged.Add(v);
            merged.AddRange(poly.Skip(k + 1));
            return merged;
        }
        throw new InvalidOperationException("No bridge found for hole.");
    }

    // Direction v→target lies strictly inside the interior wedge at v (interior left of a→v and v→b).
    private static bool InWedge(Vec2 a, Vec2 v, Vec2 b, Vec2 target)
    {
        int left1 = Predicates.Orient2D(a, v, target);
        int left2 = Predicates.Orient2D(v, b, target);
        bool convex = Predicates.Orient2D(a, v, b) > 0;
        return convex ? left1 > 0 && left2 > 0 : left1 > 0 || left2 > 0;
    }

    // True if segment pq crosses an edge of the contour or passes through one of its vertices.
    private static bool Blocked(Vec2 p, Vec2 q, IReadOnlyList<Vec2> c)
    {
        for (int i = 0; i < c.Count; i++)
        {
            Vec2 a = c[i], b = c[(i + 1) % c.Count];
            if (a != p && a != q && Predicates.Orient2D(p, q, a) == 0 && Vec2.Dot(p - a, q - a) < 0) return true;
            if (a == p || a == q || b == p || b == q) continue;
            int d1 = Predicates.Orient2D(p, q, a), d2 = Predicates.Orient2D(p, q, b);
            int d3 = Predicates.Orient2D(a, b, p), d4 = Predicates.Orient2D(a, b, q);
            if (d1 * d2 < 0 && d3 * d4 < 0) return true;
        }
        return false;
    }

    private static List<Vec2[]> EarClip(List<Vec2> polygon)
    {
        Vec2[] pts = [.. polygon];
        int n = pts.Length;
        var next = new int[n];
        var prev = new int[n];
        var alive = new bool[n];
        for (int i = 0; i < n; i++) { next[i] = (i + 1) % n; prev[i] = (i - 1 + n) % n; alive[i] = true; }
        var result = new List<Vec2[]>(n);
        int remaining = n, cur = 0, sinceLastEar = 0;
        // Testing a candidate ear against every remaining vertex is O(n^2) over the whole polygon. On a gear profile
        // (107 085 vertices) that is 1.1e10 exact side tests, and it measured as 55 of the 89 seconds of the extrude
        // scene. The grid restricts the test to the vertices that can lie inside the candidate's own bounding box.
        var grid = new BandGrid(pts, n);
        int[] seen = new int[n];
        int stamp = 0;
        while (remaining > 3)
        {
            int a = prev[cur], c = next[cur];
            if (IsEar(pts, a, cur, c, next, alive, grid, seen, ref stamp))
            {
                result.Add([pts[a], pts[cur], pts[c]]);
                Remove(cur);
                cur = c;
                sinceLastEar = 0;
                continue;
            }
            if (++sinceLastEar > remaining)
            {
                // No ear: drop a degenerate (collinear) vertex, which carries no area.
                int start = cur, k = cur;
                bool removed = false;
                do
                {
                    if (Predicates.Orient2D(pts[prev[k]], pts[k], pts[next[k]]) == 0)
                    {
                        cur = next[k];
                        Remove(k);
                        removed = true;
                        break;
                    }
                    k = next[k];
                } while (k != start);
                if (!removed) throw new InvalidOperationException("Ear clipping failed (polygon not simple).");
                sinceLastEar = 0;
                continue;
            }
            cur = c;
        }
        if (remaining == 3 && Predicates.Orient2D(pts[prev[cur]], pts[cur], pts[next[cur]]) > 0)
            result.Add([pts[prev[cur]], pts[cur], pts[next[cur]]]);
        return result;

        void Remove(int i)
        {
            next[prev[i]] = next[i];
            prev[next[i]] = prev[i];
            alive[i] = false;
            remaining--;
        }
    }

    /// <summary>
    /// Uniform grid over the polygon's bounding box, mapping a box to the vertices that can lie in it. The ear test asks
    /// "is any other vertex inside this triangle", so a uniform grid answers that with the vertices of the overlapping
    /// cells instead of all of them. Cells hold point indices; <c>seen</c> de-duplicates a vertex that falls into several
    /// queried cells.
    /// </summary>
    private sealed class BandGrid
    {
        private readonly Vec2[] _pts;
        private readonly double _x0, _y0, _inv;
        private readonly int _side;
        private readonly int[] _start;
        private readonly int[] _items;

        public BandGrid(Vec2[] pts, int n)
        {
            _pts = pts;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            // A cell holds about one vertex on average; the constant keeps the per-cell overhead low for tiny polygons.
            _side = Math.Clamp((int)Math.Ceiling(Math.Sqrt(n)), 1, 1024);
            _x0 = minX;
            _y0 = minY;
            double w = Math.Max(maxX - minX, 1), h = Math.Max(maxY - minY, 1);
            _inv = _side / Math.Max(w, h);
            _start = new int[_side * _side + 1];
            for (int i = 0; i < n; i++)
            {
                CellOf(i, out int cx, out int cy);
                _start[cy * _side + cx + 1]++;
            }
            for (int i = 0; i < _start.Length - 1; i++) _start[i + 1] += _start[i];
            _items = new int[n];
            var fill = new int[_side * _side];
            for (int i = 0; i < n; i++)
            {
                CellOf(i, out int cx, out int cy);
                _items[_start[cy * _side + cx] + fill[cy * _side + cx]++] = i;
            }
        }

        private void CellOf(int i, out int cx, out int cy)
        {
            cx = Math.Clamp((int)((_pts[i].X - _x0) * _inv), 0, _side - 1);
            cy = Math.Clamp((int)((_pts[i].Y - _y0) * _inv), 0, _side - 1);
        }

        /// <summary>Calls <paramref name="visit"/> for every vertex whose cell overlaps the box, possibly more than once.</summary>
        public void Query(double minX, double minY, double maxX, double maxY, Action<int> visit)
        {
            int cx0 = Math.Clamp((int)((minX - _x0) * _inv), 0, _side - 1);
            int cx1 = Math.Clamp((int)((maxX - _x0) * _inv), 0, _side - 1);
            int cy0 = Math.Clamp((int)((minY - _y0) * _inv), 0, _side - 1);
            int cy1 = Math.Clamp((int)((maxY - _y0) * _inv), 0, _side - 1);
            for (int cy = cy0; cy <= cy1; cy++)
            {
                int row = cy * _side;
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    int cell = row + cx;
                    for (int k = _start[cell]; k < _start[cell + 1]; k++) visit(_items[k]);
                }
            }
        }
    }

    private static bool IsEar(Vec2[] pts, int a, int b, int c, int[] next, bool[] alive, BandGrid grid,
        int[] seen, ref int stamp)
    {
        Vec2 pa = pts[a], pb = pts[b], pc = pts[c];
        if (Predicates.Orient2D(pa, pb, pc) <= 0) return false;
        // A vertex can only be inside the triangle if it lies in the triangle's bounding box, so the grid query over that
        // box is exact: nothing outside can block the ear.
        double minX = Math.Min(pa.X, pc.X), maxX = Math.Max(pa.X, pc.X);
        double minY = Math.Min(pa.Y, pc.Y), maxY = Math.Max(pa.Y, pc.Y);
        if (pb.X < minX) minX = pb.X;
        if (pb.X > maxX) maxX = pb.X;
        if (pb.Y < minY) minY = pb.Y;
        if (pb.Y > maxY) maxY = pb.Y;
        ++stamp;
        int mark = stamp;
        bool blocked = false;
        grid.Query(minX, minY, maxX, maxY, i =>
        {
            if (blocked || !alive[i] || seen[i] == mark) return;
            seen[i] = mark;
            // Compared by value, not by index: JoinHoles bridges a hole by inserting both bridge endpoints twice, so the
            // polygon legitimately contains duplicate points and a value copy of a, b or c must not block the ear.
            Vec2 p = pts[i];
            if (p == pa || p == pb || p == pc) return;
            if (Predicates.Orient2D(pa, pb, p) >= 0 && Predicates.Orient2D(pb, pc, p) >= 0 && Predicates.Orient2D(pc, pa, p) >= 0)
                blocked = true;
        });
        return !blocked;
    }

    /// <summary>Hertel–Mehlhorn: removes diagonals while both merged corners stay convex.</summary>
    private static List<Vec2[]> MergeConvex(List<Vec2[]> triangles)
    {
        var pieces = triangles.Select(t => new List<Vec2>(t)).ToList();
        var alive = Enumerable.Repeat(true, pieces.Count).ToList();
        var owner = new Dictionary<(Vec2, Vec2), int>();
        for (int i = 0; i < pieces.Count; i++)
            for (int k = 0; k < 3; k++)
                owner[(pieces[i][k], pieces[i][(k + 1) % 3])] = i;

        var diagonals = owner.Keys.Where(e => owner.ContainsKey((e.Item2, e.Item1)) && e.Item1.CompareTo(e.Item2) < 0).ToList();
        foreach (var (u, v) in diagonals)
        {
            if (!owner.TryGetValue((u, v), out int pa) || !owner.TryGetValue((v, u), out int pb) || pa == pb) continue;
            var a = pieces[pa];
            var b = pieces[pb];
            int ia = IndexOfEdge(a, u, v), ib = IndexOfEdge(b, v, u);
            if (ia < 0 || ib < 0) continue;
            // Merged: a from v around to u, then b from u around to v.
            var merged = new List<Vec2>(a.Count + b.Count - 2);
            for (int k = 0; k < a.Count; k++) merged.Add(a[(ia + 1 + k) % a.Count]);       // v ... u
            for (int k = 2; k < b.Count; k++) merged.Add(b[(ib + k) % b.Count]);           // after u ... before v
            if (!IsConvex(merged)) continue;
            int id = pa;
            pieces[id] = merged;
            alive[pb] = false;
            owner.Remove((u, v));
            owner.Remove((v, u));
            for (int k = 0; k < merged.Count; k++) owner[(merged[k], merged[(k + 1) % merged.Count])] = id;
        }
        return pieces.Where((_, i) => alive[i]).Select(p => BooleanKernel.RemoveCollinear(p)).ToList();
    }

    private static int IndexOfEdge(List<Vec2> p, Vec2 u, Vec2 v)
    {
        for (int k = 0; k < p.Count; k++)
            if (p[k] == u && p[(k + 1) % p.Count] == v) return k;
        return -1;
    }

    private static bool IsConvex(List<Vec2> p)
    {
        for (int k = 0; k < p.Count; k++)
            if (Predicates.Orient2D(p[k], p[(k + 1) % p.Count], p[(k + 2) % p.Count]) < 0) return false;
        return true;
    }
}
