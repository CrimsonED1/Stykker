namespace Stykker.NanoCut.Geometry3D;

/// <summary>Exact 3D convex hull of grid points (incremental, exact orient3d).</summary>
public static class ConvexHull3
{
    private const double Epsilon = 1.0 / (1L << 53);

/// <summary>Convex hull as a solid with triangular faces. Throws if the points do not span a volume.</summary>
    public static Solid Compute(IEnumerable<Vec3> points)
    {
        var tris = Triangles(points, out var pts);
        return new Solid(FacesFromTriangles(pts, tris));
    }

    /// <summary>
    /// Turns a soup of outward-oriented triangles over shared vertices into faces, merging coplanar neighbours into one
    /// convex polygon each. Two triangles are coplanar iff the far vertex of one lies on the other's plane (exact
    /// orient3d); union-find joins them, and each group's boundary -- the directed edges whose twin is not in the group --
    /// is walked into a polygon.
    ///
    /// This is the step that decides how many faces a result has. Without it a triangulated cap stays one face per
    /// triangle: Solid.Extrude fed 107 328 profile vertices through here and got 428 800 faces.
    /// </summary>
    /// <param name="pts">Vertices, each present once.</param>
    /// <param name="tris">Outward-oriented triangles as indices into <paramref name="pts"/>.</param>
    /// <param name="strict">
    /// A convex hull's surface is closed and convex, so a group whose boundary is not a single convex loop is a bug
    /// there and is thrown. A general triangle soup (an extruded cap, a re-triangulated solid after a Boolean) can have
    /// T-junctions or a boundary that folds back, and such a group keeps its triangles instead.
    /// </param>
    internal static List<Face3> FacesFromTriangles(Vec3[] pts, List<(int A, int B, int C)> tris, bool strict = true)
    {
        long n = pts.Length;
        var edgeTri = (_scratch ??= new Scratch()).EdgeTri;
        edgeTri.Clear();
        for (int i = 0; i < tris.Count; i++)
        {
            var (a, b, c) = tris[i];
            edgeTri[Key(a, b, n)] = i; edgeTri[Key(b, c, n)] = i; edgeTri[Key(c, a, n)] = i;
        }
        var parent = new int[tris.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (int i = 0; i < tris.Count; i++)
        {
            var (a, b, c) = tris[i];
            Join(i, b, a, c); Join(i, c, b, a); Join(i, a, c, b);
        }
        void Join(int i, int u, int v, int own)
        {
            // A convex hull is closed, so the twin edge always exists. A general triangle soup can have a boundary edge
            // with no twin; there is then simply no neighbour to join with.
            if (!edgeTri.TryGetValue(Key(u, v, n), out int j)) return;
            if (j < i) return; // each shared edge once
            var (x, y, z) = tris[j];
            int far = x != u && x != v ? x : y != u && y != v ? y : z;
            // Two triangles agreeing on two vertices share more than one edge, so the third is not a far vertex.
            if (far == u || far == v) return;
            var (a, b, c) = tris[i];
            if (Predicates.Orient3D(pts[a], pts[b], pts[c], pts[far]) == 0) parent[Find(i)] = Find(j);
        }
        // Single triangles become faces directly; only real coplanar groups are collected. For a ball hull almost every
        // triangle is its own group (a sphere's quads and meridians are not coplanar), so the group lists must not be
        // built for them: 2256 of 2304 groups for a 48-segment ball would be 2256 throwaway lists.
        var size = new int[tris.Count];
        for (int i = 0; i < tris.Count; i++) size[Find(i)]++;
        var groups = new Dictionary<int, List<(int A, int B, int C)>>();
        var single = new List<int>(tris.Count);
        for (int i = 0; i < tris.Count; i++)
        {
            int r = Find(i);
            if (size[r] == 1) { single.Add(i); continue; }
            if (!groups.TryGetValue(r, out var g)) groups[r] = g = [];
            g.Add(tris[i]);
        }
        // Face construction (exact planes, GCD normalisation) is independent per triangle: build in parallel by index,
        // so the face order and the result do not depend on scheduling.
        var built = new Face3[single.Count];
        var pp = pts;
        if (single.Count >= ParallelThreshold && SolidBoolean.MaxParallelism > 1)
            Parallel.For(0, built.Length, new ParallelOptions { MaxDegreeOfParallelism = SolidBoolean.MaxParallelism },
                k => built[k] = Triangle(pp, tris[single[k]]));
        else
            for (int k = 0; k < built.Length; k++) built[k] = Triangle(pp, tris[single[k]]);
        var faces = new List<Face3>(built.Length + groups.Count);
        faces.AddRange(built);
        var inner = new HashSet<long>();
        var loop = new List<Vec3>();
        foreach (var g in groups.Values)
        {
            inner.Clear();
            foreach (var (a, b, c) in g) { inner.Add(Key(a, b, n)); inner.Add(Key(b, c, n)); inner.Add(Key(c, a, n)); }
            loop.Clear();
            // A group can have more than one boundary loop. Loops that face the same way as the group are separate
            // pieces that touch (islands meeting at a vertex), and each becomes its own face. A loop that faces the
            // other way bounds a hole: Face3 has no notion of a hole, and a face from the outer loop would lie over the
            // hole with a reversed face from the hole loop under it, two coincident sheets that are right in volume
            // but wrong in every mesh and in later Booleans. Such a group keeps its triangles.
            var boundary = new Dictionary<int, int>();
            foreach (var (a, b, c) in g)
            {
                if (!inner.Contains(Key(b, a, n))) boundary[a] = b;
                if (!inner.Contains(Key(c, b, n))) boundary[b] = c;
                if (!inner.Contains(Key(a, c, n))) boundary[c] = a;
            }
            var orientation = GroupPlane(g, pts);
            var loopFaces = new List<Face3>();
            bool ok = !orientation.IsDegenerate;
            foreach (int seed in boundary.Keys.ToArray())
            {
                if (!ok) break;
                if (!boundary.ContainsKey(seed)) continue;   // consumed by an earlier loop
                loop.Clear();
                int v = seed;
                do
                {
                    loop.Add(pts[v]);
                    // Consume the directed edge v -> next. A closed walk ends when it arrives back at the seed.
                    if (!boundary.Remove(v, out int next)) break;   // dangling: no edge leaves v any more
                    v = next;
                } while (v != seed);
                if (v != seed) { ok = false; break; }
                // The boundary can close without being convex (a folded or self-touching walk, or simply a concave cap such
                // as an L or a gear outline). Face3.FromGrid checks convexity only against its first few vertices, so the
                // full check is made here: a concave loop accepted as a face would break every later Boolean.
                var clean = WithoutCollinear(loop);
                if (!IsConvexLoop(clean, orientation)) { ok = false; break; }
                Face3 face;
                try { face = Face3.FromGrid(clean); }
                catch (ArgumentException) { ok = false; break; }
                if (!SameOrientation(face.Support, orientation)) { ok = false; break; }   // a hole
                loopFaces.Add(face);
            }
            // All loops or none: a group whose later loop fails must not keep the faces of its earlier loops as well as
            // its triangles.
            if (ok)
            {
                faces.AddRange(loopFaces);
                continue;
            }
            if (strict) throw new InvalidOperationException("Face boundary is not a single loop.");
            foreach (var (a, b, c) in g) faces.Add(Face3.FromTriangle(pts[a], pts[b], pts[c]));
        }
        return faces;
    }

    /// <summary>The plane of the first non-degenerate triangle of a coplanar group, oriented as the triangles are.</summary>
    private static Plane3 GroupPlane(List<(int A, int B, int C)> group, Vec3[] pts)
    {
        foreach (var (a, b, c) in group)
        {
            var plane = Plane3.FromPoints(pts[a], pts[b], pts[c]);
            if (!plane.IsDegenerate) return plane;
        }
        return default;
    }

    /// <summary>
    /// Whether a boundary loop (collinear corners removed) is one convex polygon turning the way its group faces. Every
    /// corner must turn the same way in the projection that drops the dominant axis of the normal, exactly in Int128 (the
    /// coordinate differences fit 33 bits), and no vertex may repeat: a loop that touches itself at a vertex is not one
    /// convex polygon. The boundary of a triangulated planar region does not cross itself, so turns of one sign at
    /// distinct vertices mean the loop is convex. O(n), so it can check every vertex of a large cap.
    /// </summary>
    internal static bool IsConvexLoop(List<Vec3> loop, in Plane3 orientation)
    {
        int n = loop.Count;
        if (n < 3) return false;
        Int128 ax = Int128.Abs(orientation.Nx), ay = Int128.Abs(orientation.Ny), az = Int128.Abs(orientation.Nz);
        int k = ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
        // (u × v)_k for a convex loop around the normal has the sign of the normal's k-th component; the projection keeps
        // the cyclic order (y, z), (z, x), (x, y), so the 2D cross product below is exactly that component.
        int want = Int128.Sign(k == 0 ? orientation.Nx : k == 1 ? orientation.Ny : orientation.Nz);
        var seen = new HashSet<Vec3>(n);
        for (int i = 0; i < n; i++)
        {
            Vec3 a = loop[i], b = loop[(i + 1) % n], c = loop[(i + 2) % n];
            if (!seen.Add(a)) return false;
            var (ux, uy) = Project(b, k);
            var (px, py) = Project(a, k);
            var (vx, vy) = Project(c, k);
            Int128 cross = (Int128)(ux - px) * (vy - uy) - (Int128)(uy - py) * (vx - ux);
            if (Int128.Sign(cross) != want) return false;
        }
        return true;
    }

    private static (long U, long V) Project(Vec3 p, int k) => k switch { 0 => (p.Y, p.Z), 1 => (p.Z, p.X), _ => (p.X, p.Y) };

    /// <summary>
    /// Whether two planes of one coplanar group face the same way. Their normals are parallel, so the first component
    /// that is not zero decides, exactly and without forming a product that could overflow Int128.
    /// </summary>
    private static bool SameOrientation(in Plane3 a, in Plane3 b)
    {
        if (a.Nx != 0 || b.Nx != 0) return Int128.Sign(a.Nx) == Int128.Sign(b.Nx);
        if (a.Ny != 0 || b.Ny != 0) return Int128.Sign(a.Ny) == Int128.Sign(b.Ny);
        return Int128.Sign(a.Nz) == Int128.Sign(b.Nz);
    }

    // Edge key a·n + b. (Not (a << 32) | b: Int64's hash folds the halves with XOR, which collides for a ^ b.)
    private static long Key(int a, int b, long n) => a * n + b;

    private const int ParallelThreshold = 256;

    private const double MaxHullCoordinate = 1L << 40;

    private static Face3 Triangle(Vec3[] pts, (int A, int B, int C) t) => Face3.FromTriangle(pts[t.A], pts[t.B], pts[t.C]);

    [ThreadStatic] private static Scratch? _scratch;

    private sealed class Scratch
    {
        public readonly List<(int A, int B, int C)> Faces = [];
        public readonly List<bool> Alive = [];
        public readonly List<int> Head = [];
        public int[] NextPoint = [];
        public readonly Dictionary<long, int> EdgeFace = [];
        public readonly List<(double X, double Y, double Z, double D, double Bound)> PlanesD = [];
        public readonly List<int> Rest = [];
        public readonly Stack<int> Pending = new();
        public readonly Stack<int> Stack = new();
        public readonly List<int> Visible = [];
        public readonly HashSet<int> VisibleSet = [];
        public readonly List<(int, int)> Horizon = [];
        public readonly List<int> Created = [];
        public readonly Dictionary<long, int> EdgeTri = [];

        public void Clear()
        {
            Faces.Clear(); Alive.Clear(); Head.Clear(); EdgeFace.Clear(); PlanesD.Clear(); Rest.Clear();
            Pending.Clear(); Stack.Clear(); Visible.Clear(); VisibleSet.Clear(); Horizon.Clear(); Created.Clear();
        }
    }

    // Removes vertices that lie on the line through their neighbours (exact cross product; differences < 2^33).
    private static List<Vec3> WithoutCollinear(List<Vec3> loop)
    {
        var r = new List<Vec3>(loop.Count);
        int n = loop.Count;
        for (int i = 0; i < n; i++)
        {
            Vec3 a = loop[(i - 1 + n) % n], b = loop[i], c = loop[(i + 1) % n];
            long ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - b.X, vy = c.Y - b.Y, vz = c.Z - b.Z;
            bool collinear = (Int128)uy * vz == (Int128)uz * vy && (Int128)uz * vx == (Int128)ux * vz && (Int128)ux * vy == (Int128)uy * vx;
            if (!collinear) r.Add(b);
        }
        return r;
    }

    /// <summary>Hull triangles (indices into <paramref name="pts"/>), counter-clockwise seen from outside.</summary>
    internal static List<(int A, int B, int C)> Triangles(IEnumerable<Vec3> points, out Vec3[] pts)
    {
        pts = points.Distinct().ToArray();
        if (pts.Length < 4) throw new ArgumentException("A hull needs at least four points.");
        long n = pts.Length;
        var p = pts;

        // Largest |coordinate| of the input: the filter bound must hold for it (Vec3 itself is not range-checked).
        double maxCoord = 1;
        foreach (var v in p) maxCoord = Math.Max(maxCoord, Math.Max(Math.Abs((double)v.X), Math.Max(Math.Abs((double)v.Y), Math.Abs((double)v.Z))));
        // Exact planes through the points fit Int128 only for |coordinate| ≤ 2^40 (|n| ≤ 2^83, |d| ≤ 3·2^123).
        if (maxCoord > MaxHullCoordinate)
            throw new ArgumentOutOfRangeException(nameof(points), "Hull coordinates must stay within ±2^40 nm.");

        // Initial tetrahedron.
        int i0 = 0, i1 = -1, i2 = -1, i3 = -1;
        for (int i = 1; i < p.Length && i1 < 0; i++) if (p[i] != p[i0]) i1 = i;
        for (int i = 1; i < p.Length && i2 < 0; i++)
            if (i != i1 && !Plane3.FromPoints(p[i0], p[i1], p[i]).IsDegenerate) i2 = i;
        if (i2 < 0) throw new ArgumentException("Points are collinear.");
        for (int i = 1; i < p.Length && i3 < 0; i++)
            if (i != i1 && i != i2 && Predicates.Orient3D(p[i0], p[i1], p[i2], p[i]) != 0) i3 = i;
        if (i3 < 0) throw new ArgumentException("Points are coplanar.");

        // Working buffers are reused per thread: they exceed the large-object threshold for big inputs, and fresh large
        // objects cost a page fault and kernel zeroing per page on every call.
        var w = _scratch ??= new Scratch();
        w.Clear();
        var faces = w.Faces;
        var alive = w.Alive;
        // Outside (conflict) sets as linked lists over arrays: head[f] is the first waiting point, nextPoint[q] the next.
        var head = w.Head;
        if (w.NextPoint.Length < p.Length) w.NextPoint = new int[p.Length];
        var nextPoint = w.NextPoint;
        var edgeFace = w.EdgeFace;
        // Per face: double approximation of the plane for the filtered side test (the exact plane is recomputed on demand).
        var planesD = w.PlanesD;

        int AddFace(int a, int b, int c)
        {
            int id = faces.Count;
            faces.Add((a, b, c));
            alive.Add(true);
            head.Add(-1);
            edgeFace[Key(a, b, n)] = id;
            edgeFace[Key(b, c, n)] = id;
            edgeFace[Key(c, a, n)] = id;
            // Same orientation as Orient3D(a, b, c, q): positive above the counter-clockwise triangle (outside).
            var pl = Plane3.FromPoints(p[a], p[b], p[c]);
            double nx = (double)pl.Nx, ny = (double)pl.Ny, nz = (double)pl.Nz;
            // |error| ≤ rounding of n and d (relative 2^-53 each) plus rounding in the sum: 8 ulp of the magnitude.
            planesD.Add((nx, ny, nz, (double)pl.D, (Math.Abs(nx) + Math.Abs(ny) + Math.Abs(nz)) * maxCoord));
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
            bool certain = v > bound || v < -bound;
            if (KernelStats.Counting)
            {
                var s = KernelStats.Mine;
                s.AboveCalls++;
                // The fallback recomputes the exact plane (three cross products plus canonical gcd), so its share
                // is the cost of the filter bound, not just of Int128.
                if (!certain) s.AboveExact++;
            }
            if (certain) return v > bound;
            var (a, b, c) = faces[f];
            return Predicates.Orient3D(p[a], p[b], p[c], p[q]) > 0; // same sign as the face plane (outward normal)
        }

        // Orient the tetrahedron so that every face has the fourth point below it.
        if (Predicates.Orient3D(p[i0], p[i1], p[i2], p[i3]) > 0) (i1, i2) = (i2, i1);
        var initial = new List<int> { AddFace(i0, i1, i2), AddFace(i0, i3, i1), AddFace(i1, i3, i2), AddFace(i2, i3, i0) };

        // Quickhull with conflict lists: every point waits in the outside set of one face it lies strictly above.
        void Assign(List<int> points, List<int> candidates)
        {
            foreach (int q in points)
                foreach (int f in candidates)
                    if (Above(f, q))
                    {
                        nextPoint[q] = head[f];
                        head[f] = q;
                        break;
                    }
        }
        var rest = w.Rest;
        for (int i = 0; i < p.Length; i++) if (i != i0 && i != i1 && i != i2 && i != i3) rest.Add(i);
        Assign(rest, initial);

        var pending = w.Pending;
        foreach (int f in initial) pending.Push(f);
        var stack = w.Stack;
        var visible = w.Visible;
        var visibleSet = w.VisibleSet;
        var horizon = w.Horizon;
        var orphans = rest;
        var created = w.Created;
        while (pending.Count > 0)
        {
            int f0 = pending.Pop();
            if (!alive[f0] || head[f0] < 0) continue;
            // Farthest point (largest height; all heights refer to the same face, so they compare directly).
            int q = head[f0];
            double best = Height(f0, q);
            for (int r = nextPoint[q]; r >= 0; r = nextPoint[r])
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
                if (!visibleSet.Contains(edgeFace[Key(b, a, n)])) horizon.Add((a, b));
                if (!visibleSet.Contains(edgeFace[Key(c, b, n)])) horizon.Add((b, c));
                if (!visibleSet.Contains(edgeFace[Key(a, c, n)])) horizon.Add((c, a));
                for (int r = head[f]; r >= 0; r = nextPoint[r]) if (r != q) orphans.Add(r);
                head[f] = -1;
            }
            foreach (int f in visible)
            {
                alive[f] = false;
                var (a, b, c) = faces[f];
                edgeFace.Remove(Key(a, b, n)); edgeFace.Remove(Key(b, c, n)); edgeFace.Remove(Key(c, a, n));
            }
            created.Clear();
            foreach (var (u, v) in horizon) created.Add(AddFace(u, v, q));
            Assign(orphans, created);
            foreach (int f in created) if (head[f] >= 0) pending.Push(f);

            void Visit(int u, int v)
            {
                int g = edgeFace[Key(u, v, n)];
                if (visibleSet.Contains(g) || !Above(g, q)) return;
                visibleSet.Add(g);
                stack.Push(g);
            }
        }
        return faces.Where((_, i) => alive[i]).ToList();
    }
}
