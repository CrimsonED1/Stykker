namespace Stykker.NanoCut.Geometry2D;

/// <summary>
/// Exact 2D Boolean kernel on the 1 nm grid.
/// <list type="number">
/// <item>All edges of both operands are collected with their winding multiplicities.</item>
/// <item>A sweep over x finds crossings, T-junctions and collinear overlaps. Crossings are resolved by snap
/// rounding with hot pixels (every edge is routed through the centre of each crossing or end-point pixel it
/// meets, ≤ 0.71 nm), T-junctions and overlaps by exact splits. Grid points are never moved again, so the
/// error does not accumulate.</item>
/// <item>A half-edge structure is built (exact angular order around each vertex), faces are traced and
/// the winding numbers of both operands are propagated face to face, starting from one exact ray test
/// per connected component.</item>
/// <item>Edges that separate an inside face from an outside face (per fill rule and operation) are linked
/// into result contours: outer contours counter-clockwise, holes clockwise.</item>
/// </list>
/// All decisions use exact integer predicates (<see cref="Int128"/>).
/// </summary>
internal static class BooleanKernel
{
    private const int MaxSplitIterations = 64;

    private struct Edge
    {
        public Vec2 A, B; // canonical: A < B lexicographically
        public int WA, WB; // winding multiplicity of subject / clip in direction A→B
    }

    public static List<Vec2[]> Execute(
        IEnumerable<IReadOnlyList<Vec2>> subject, FillRule subjectFill,
        IEnumerable<IReadOnlyList<Vec2>> clip, FillRule clipFill,
        BooleanOp op)
    {
        var raw = new List<Edge>();
        AddContours(raw, subject, isSubject: true);
        AddContours(raw, clip, isSubject: false);
        List<Edge> edges = Merge(raw);
        edges = SplitUntilPlanar(edges);
        return BuildResult(edges, subjectFill, clipFill, op);
    }

    private static void AddContours(List<Edge> list, IEnumerable<IReadOnlyList<Vec2>> contours, bool isSubject)
    {
        foreach (var c in contours)
        {
            int n = c.Count;
            if (n < 2) continue;
            for (int i = 0; i < n; i++)
            {
                Vec2 a = c[i], b = c[(i + 1) % n];
                if (a == b) continue;
                list.Add(Canonical(a, b, isSubject ? 1 : 0, isSubject ? 0 : 1));
            }
        }
    }

    private static Edge Canonical(Vec2 a, Vec2 b, int wa, int wb) =>
        a.CompareTo(b) < 0
            ? new Edge { A = a, B = b, WA = wa, WB = wb }
            : new Edge { A = b, B = a, WA = -wa, WB = -wb };

    /// <summary>Combines identical edges and drops edges whose multiplicities cancel.</summary>
    private static List<Edge> Merge(List<Edge> raw)
    {
        var map = new Dictionary<(Vec2, Vec2), (int WA, int WB)>(raw.Count);
        foreach (var e in raw)
        {
            map.TryGetValue((e.A, e.B), out var w);
            map[(e.A, e.B)] = (w.WA + e.WA, w.WB + e.WB);
        }
        var result = new List<Edge>(map.Count);
        foreach (var kv in map)
        {
            if (kv.Value.WA == 0 && kv.Value.WB == 0) continue;
            result.Add(new Edge { A = kv.Key.Item1, B = kv.Key.Item2, WA = kv.Value.WA, WB = kv.Value.WB });
        }
        // Deterministic order independent of hashing.
        result.Sort((p, q) =>
        {
            int c = p.A.CompareTo(q.A);
            return c != 0 ? c : p.B.CompareTo(q.B);
        });
        return result;
    }

    private static List<Edge> SplitUntilPlanar(List<Edge> edges)
    {
        for (int iter = 0; iter < MaxSplitIterations; iter++)
        {
            var (touch, hot) = FindSplitPoints(edges);
            if (hot.Count > 0)
            {
                edges = SnapRound(edges, hot);
                continue;
            }
            if (touch.Count == 0) return edges;
            edges = ApplySplits(edges, touch);
        }
        throw new InvalidOperationException(
            $"Boolean kernel: arrangement did not become planar after {MaxSplitIterations} iterations.");
    }

    /// <summary>Splits edges at exact grid points lying on them (T-junctions, collinear overlaps).</summary>
    private static List<Edge> ApplySplits(List<Edge> edges, Dictionary<int, List<Vec2>> splits)
    {
        var next = new List<Edge>(edges.Count + splits.Count * 2);
        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            if (!splits.TryGetValue(i, out var pts))
            {
                next.Add(e);
                continue;
            }
            AddPolyline(next, e, pts);
        }
        return Merge(next);
    }

    private static void AddPolyline(List<Edge> output, in Edge e, List<Vec2> pts)
    {
        Vec2 d = e.B - e.A;
        var a = e.A;
        pts.Sort((p, q) => Vec2.Dot(p - a, d).CompareTo(Vec2.Dot(q - a, d)));
        Vec2 prev = e.A;
        foreach (var p in pts)
        {
            if (p == prev || p == e.A || p == e.B) continue;
            output.Add(Canonical(prev, p, e.WA, e.WB));
            prev = p;
        }
        if (prev != e.B) output.Add(Canonical(prev, e.B, e.WA, e.WB));
    }

    /// <summary>
    /// Snap rounding with hot pixels (Hobby; Guibas &amp; Marimont): every crossing point and every edge end point
    /// defines a hot pixel (the half-open unit square around a grid point); each edge is rerouted through the
    /// centres of all hot pixels it meets. This moves edges by at most √2/2 nm and, unlike rounding crossings one by
    /// one, cannot create new crossings, so it terminates.
    /// </summary>
    private static List<Edge> SnapRound(List<Edge> edges, HashSet<Vec2> crossings)
    {
        var hot = new HashSet<Vec2>(crossings);
        foreach (var e in edges) { hot.Add(e.A); hot.Add(e.B); }
        var pixels = hot.ToArray();
        Array.Sort(pixels);
        var xs = pixels.Select(p => p.X).ToArray();

        var next = new List<Edge>(edges.Count * 2);
        var hits = new List<Vec2>();
        foreach (var e in edges)
        {
            long y0 = Math.Min(e.A.Y, e.B.Y) - 1, y1 = Math.Max(e.A.Y, e.B.Y) + 1;
            int i = LowerBound(xs, e.A.X - 1);
            hits.Clear();
            for (; i < pixels.Length && pixels[i].X <= e.B.X + 1; i++)
            {
                var h = pixels[i];
                if (h.Y < y0 || h.Y > y1 || h == e.A || h == e.B) continue;
                if (SegmentHitsPixel(e.A, e.B, h)) hits.Add(h);
            }
            if (hits.Count == 0) next.Add(e);
            else AddPolyline(next, e, hits);
        }
        return Merge(next);
    }

    private static int LowerBound(long[] xs, long x)
    {
        int lo = 0, hi = xs.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (xs[mid] < x) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Exact test whether segment ab meets the half-open pixel [h − ½, h + ½)².</summary>
    internal static bool SegmentHitsPixel(Vec2 a, Vec2 b, Vec2 h)
    {
        // Doubled coordinates make the pixel bounds integral. Parameter interval t ∈ [lo, hi] as fractions.
        Int128 loN = 0, loD = 1, hiN = 1, hiD = 1;
        bool loOpen = false, hiOpen = false;
        return Clip(2 * a.X, 2 * (b.X - a.X), 2 * h.X - 1, 2 * h.X + 1)
            && Clip(2 * a.Y, 2 * (b.Y - a.Y), 2 * h.Y - 1, 2 * h.Y + 1)
            && (Compare(loN, loD, hiN, hiD) < 0 || (Compare(loN, loD, hiN, hiD) == 0 && !loOpen && !hiOpen));

        // Constraint low ≤ p + t·d < high.
        bool Clip(long p, long d, long low, long high)
        {
            if (d == 0) return low <= p && p < high;
            if (d > 0)
            {
                Lower(low - p, d, open: false);
                Upper(high - p, d, open: true);
            }
            else
            {
                Upper(p - low, -d, open: false);
                Lower(p - high, -d, open: true);
            }
            return true;
        }

        void Lower(Int128 n, Int128 d, bool open)
        {
            int c = Compare(n, d, loN, loD);
            if (c > 0 || (c == 0 && open)) { loN = n; loD = d; loOpen = open || (c == 0 && loOpen); }
        }

        void Upper(Int128 n, Int128 d, bool open)
        {
            int c = Compare(n, d, hiN, hiD);
            if (c < 0 || (c == 0 && open)) { hiN = n; hiD = d; hiOpen = open || (c == 0 && hiOpen); }
        }

        static int Compare(Int128 an, Int128 ad, Int128 bn, Int128 bd) => (an * bd).CompareTo(bn * ad);
    }

    private static (Dictionary<int, List<Vec2>> Touch, HashSet<Vec2> Hot) FindSplitPoints(List<Edge> edges)
    {
        int n = edges.Count;
        // Edges are sorted by A (lexicographic), and A.X == min X of the edge.
        var touch = new Dictionary<int, List<Vec2>>();
        var hot = new HashSet<Vec2>();
        for (int i = 0; i < n; i++)
        {
            var p = edges[i];
            long pMinY = Math.Min(p.A.Y, p.B.Y), pMaxY = Math.Max(p.A.Y, p.B.Y);
            for (int j = i + 1; j < n; j++)
            {
                var q = edges[j];
                if (q.A.X > p.B.X) break;
                long qMinY = Math.Min(q.A.Y, q.B.Y), qMaxY = Math.Max(q.A.Y, q.B.Y);
                if (qMinY > pMaxY || qMaxY < pMinY) continue;
                Intersect(p, i, q, j, touch, hot);
            }
        }
        return (touch, hot);
    }

    private static void Intersect(in Edge p, int i, in Edge q, int j, Dictionary<int, List<Vec2>> splits, HashSet<Vec2> hot)
    {
        int d1 = Predicates.Orient2D(q.A, q.B, p.A);
        int d2 = Predicates.Orient2D(q.A, q.B, p.B);
        int d3 = Predicates.Orient2D(p.A, p.B, q.A);
        int d4 = Predicates.Orient2D(p.A, p.B, q.B);

        if (d1 * d2 < 0 && d3 * d4 < 0)
        {
            hot.Add(RoundedIntersection(p.A, p.B, q.A, q.B));
            return;
        }
        // Touching / collinear cases: split at the endpoint lying strictly inside the other edge (exact).
        if (d1 == 0 && StrictlyInside(q, p.A)) Add(splits, j, p.A);
        if (d2 == 0 && StrictlyInside(q, p.B)) Add(splits, j, p.B);
        if (d3 == 0 && StrictlyInside(p, q.A)) Add(splits, i, q.A);
        if (d4 == 0 && StrictlyInside(p, q.B)) Add(splits, i, q.B);
    }

    private static void Add(Dictionary<int, List<Vec2>> splits, int edge, Vec2 point)
    {
        if (!splits.TryGetValue(edge, out var list))
            splits[edge] = list = new List<Vec2>(2);
        list.Add(point);
    }

    // Precondition: point is collinear with the edge.
    private static bool StrictlyInside(in Edge e, Vec2 point) =>
        point != e.A && point != e.B && Vec2.Dot(e.A - point, e.B - point) < 0;

    /// <summary>
    /// Intersection of two properly crossing segments, rounded to the grid point whose half-open pixel contains it
    /// (halves round up). x = a.x + dx·t with t = num/den; all products stay below 2^99.
    /// </summary>
    internal static Vec2 RoundedIntersection(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        Vec2 r = b - a, s = d - c;
        Int128 den = Vec2.Cross(r, s);
        Int128 num = Vec2.Cross(c - a, s);
        if (den < 0) { den = -den; num = -num; }
        long x = a.X + RoundDiv((Int128)r.X * num, den);
        long y = a.Y + RoundDiv((Int128)r.Y * num, den);
        return new Vec2(x, y);
    }

    /// <summary>floor(n/d + ½) for d &gt; 0 (nearest integer, halves up – matches half-open hot pixels).</summary>
    internal static long RoundDiv(Int128 n, Int128 d)
    {
        Int128 num = 2 * n + d, den = 2 * d;
        (Int128 q, Int128 rem) = Int128.DivRem(num, den); // truncates toward zero
        if (rem < 0) q -= 1;
        return (long)q;
    }

    private sealed class Graph
    {
        public required Vec2[] Vertices;
        public required int[] Origin;      // per half-edge
        public required int[] WA, WB;      // per half-edge, multiplicity in its direction
        public required int[][] Outgoing;  // per vertex, half-edges sorted counter-clockwise from +x
        public required int[] PosInOrigin; // per half-edge, index within Outgoing[Origin]

        public int Dest(int h) => Origin[h ^ 1];

        /// <summary>Next half-edge along the face on the left of <paramref name="h"/>.</summary>
        public int Next(int h)
        {
            int[] list = Outgoing[Dest(h)];
            int idx = PosInOrigin[h ^ 1];
            return list[(idx - 1 + list.Length) % list.Length];
        }
    }

    private static Graph BuildGraph(List<Edge> edges)
    {
        var index = new Dictionary<Vec2, int>(edges.Count * 2);
        var verts = new List<Vec2>();
        int VertexOf(Vec2 p)
        {
            if (!index.TryGetValue(p, out int k))
            {
                k = verts.Count;
                verts.Add(p);
                index[p] = k;
            }
            return k;
        }

        int m = edges.Count;
        var origin = new int[2 * m];
        var wa = new int[2 * m];
        var wb = new int[2 * m];
        for (int e = 0; e < m; e++)
        {
            var ed = edges[e];
            origin[2 * e] = VertexOf(ed.A);
            origin[2 * e + 1] = VertexOf(ed.B);
            wa[2 * e] = ed.WA; wb[2 * e] = ed.WB;
            wa[2 * e + 1] = -ed.WA; wb[2 * e + 1] = -ed.WB;
        }

        var vertices = verts.ToArray();
        var counts = new int[vertices.Length];
        foreach (int o in origin) counts[o]++;
        var outgoing = new int[vertices.Length][];
        for (int v = 0; v < vertices.Length; v++) outgoing[v] = new int[counts[v]];
        var fill = new int[vertices.Length];
        for (int h = 0; h < origin.Length; h++) outgoing[origin[h]][fill[origin[h]]++] = h;

        var pos = new int[origin.Length];
        for (int v = 0; v < vertices.Length; v++)
        {
            Vec2 pv = vertices[v];
            var list = outgoing[v];
            Array.Sort(list, (h1, h2) => CompareAngle(vertices[origin[h1 ^ 1]] - pv, vertices[origin[h2 ^ 1]] - pv));
            for (int k = 0; k < list.Length; k++) pos[list[k]] = k;
        }

        return new Graph { Vertices = vertices, Origin = origin, WA = wa, WB = wb, Outgoing = outgoing, PosInOrigin = pos };
    }

    /// <summary>Exact counter-clockwise angle order of direction vectors, starting at +x.</summary>
    private static int CompareAngle(Vec2 a, Vec2 b)
    {
        int ha = UpperHalf(a) ? 0 : 1, hb = UpperHalf(b) ? 0 : 1;
        if (ha != hb) return ha.CompareTo(hb);
        return -Int128.Sign(Vec2.Cross(a, b));
    }

    private static bool UpperHalf(Vec2 d) => d.Y > 0 || (d.Y == 0 && d.X > 0);

    private static List<Vec2[]> BuildResult(List<Edge> edges, FillRule subjectFill, FillRule clipFill, BooleanOp op)
    {
        var result = new List<Vec2[]>();
        if (edges.Count == 0) return result;
        var g = BuildGraph(edges);
        int hCount = g.Origin.Length;

        // Trace faces (face of h = face on the left of h).
        var face = new int[hCount];
        Array.Fill(face, -1);
        var faceEdges = new List<List<int>>();
        for (int h = 0; h < hCount; h++)
        {
            if (face[h] >= 0) continue;
            int f = faceEdges.Count;
            var list = new List<int>();
            int cur = h;
            do
            {
                face[cur] = f;
                list.Add(cur);
                cur = g.Next(cur);
            } while (cur != h);
            faceEdges.Add(list);
        }

        // Connected components over vertices.
        var parent = new int[g.Vertices.Length];
        for (int v = 0; v < parent.Length; v++) parent[v] = v;
        int Find(int v)
        {
            while (parent[v] != v) v = parent[v] = parent[parent[v]];
            return v;
        }
        for (int h = 0; h < hCount; h += 2)
        {
            int a = Find(g.Origin[h]), b = Find(g.Origin[h + 1]);
            if (a != b) parent[a] = b;
        }
        // Leftmost-lowest vertex per component.
        var lowest = new Dictionary<int, int>();
        for (int v = 0; v < g.Vertices.Length; v++)
        {
            int r = Find(v);
            if (!lowest.TryGetValue(r, out int best) || g.Vertices[v].CompareTo(g.Vertices[best]) < 0)
                lowest[r] = v;
        }

        // Winding numbers per face, seeded by one exact ray test per component.
        var windA = new int[faceEdges.Count];
        var windB = new int[faceEdges.Count];
        var known = new bool[faceEdges.Count];
        var queue = new Queue<int>();
        foreach (int v in lowest.Values)
        {
            int[] outs = g.Outgoing[v];
            // The face on the -x side of v is left of the last outgoing edge in the upper half-plane
            // (all directions at the leftmost-lowest vertex lie in (-90°, 90°]).
            int hOuter = outs[^1];
            for (int k = outs.Length - 1; k >= 0; k--)
            {
                if (UpperHalf(g.Vertices[g.Dest(outs[k])] - g.Vertices[v])) { hOuter = outs[k]; break; }
            }
            int f0 = face[hOuter];
            (windA[f0], windB[f0]) = WindingLeftOf(edges, g.Vertices[v]);
            known[f0] = true;
            queue.Enqueue(f0);
            while (queue.Count > 0)
            {
                int f = queue.Dequeue();
                foreach (int h in faceEdges[f])
                {
                    int t = face[h ^ 1];
                    if (known[t]) continue;
                    // left(h) = right(h) + w(h)
                    windA[t] = windA[f] - g.WA[h];
                    windB[t] = windB[f] - g.WB[h];
                    known[t] = true;
                    queue.Enqueue(t);
                }
            }
        }

        var inside = new bool[faceEdges.Count];
        for (int f = 0; f < inside.Length; f++)
        {
            bool a = IsInside(windA[f], subjectFill);
            bool b = IsInside(windB[f], clipFill);
            inside[f] = op switch
            {
                BooleanOp.Intersection => a && b,
                BooleanOp.Union => a || b,
                BooleanOp.Difference => a && !b,
                BooleanOp.Xor => a ^ b,
                _ => throw new ArgumentOutOfRangeException(nameof(op)),
            };
        }

        var kept = new bool[hCount];
        for (int h = 0; h < hCount; h++) kept[h] = inside[face[h]] && !inside[face[h ^ 1]];

        var used = new bool[hCount];
        var pts = new List<Vec2>();
        for (int h = 0; h < hCount; h++)
        {
            if (!kept[h] || used[h]) continue;
            pts.Clear();
            int cur = h;
            do
            {
                used[cur] = true;
                pts.Add(g.Vertices[g.Origin[cur]]);
                cur = NextKept(g, kept, cur);
            } while (cur != h);
            var loop = RemoveCollinear(pts);
            if (loop.Length >= 3) result.Add(loop);
        }
        return result;
    }

    private static int NextKept(Graph g, bool[] kept, int h)
    {
        int[] list = g.Outgoing[g.Dest(h)];
        int idx = g.PosInOrigin[h ^ 1];
        for (int step = 1; step <= list.Length; step++)
        {
            int cand = list[((idx - step) % list.Length + list.Length) % list.Length];
            if (kept[cand]) return cand;
        }
        throw new InvalidOperationException("Boolean kernel: open result contour.");
    }

    /// <summary>
    /// Winding numbers of subject and clip at the point (v.x - ε, v.y), where v is the leftmost-lowest
    /// vertex of a component. Counts edges crossing the downward ray; edges of v's own component never cross it.
    /// </summary>
    private static (int A, int B) WindingLeftOf(List<Edge> edges, Vec2 v)
    {
        int wa = 0, wb = 0;
        foreach (var e in edges)
        {
            // Canonical edges run in +x direction (A.X <= B.X).
            if (!(e.A.X < v.X && v.X <= e.B.X)) continue;
            int o = Predicates.Orient2D(e.A, e.B, v);
            if (o > 0) { wa += e.WA; wb += e.WB; }
            else if (o == 0)
                throw new InvalidOperationException("Boolean kernel: vertex on foreign edge after splitting.");
        }
        return (wa, wb);
    }

    private static bool IsInside(int winding, FillRule rule) => rule switch
    {
        FillRule.EvenOdd => (winding & 1) != 0,
        FillRule.NonZero => winding != 0,
        FillRule.Positive => winding > 0,
        FillRule.Negative => winding < 0,
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };

    /// <summary>Removes vertices that lie on the straight line between their neighbours (lossless).</summary>
    internal static Vec2[] RemoveCollinear(List<Vec2> pts)
    {
        var list = new List<Vec2>(pts);
        bool changed = true;
        while (changed && list.Count >= 3)
        {
            changed = false;
            for (int i = 0; i < list.Count && list.Count >= 3; i++)
            {
                Vec2 prev = list[(i - 1 + list.Count) % list.Count];
                Vec2 next = list[(i + 1) % list.Count];
                if (Predicates.Orient2D(prev, list[i], next) == 0)
                {
                    list.RemoveAt(i);
                    i--;
                    changed = true;
                }
            }
        }
        return list.ToArray();
    }
}
