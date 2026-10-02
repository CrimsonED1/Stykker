using System.Numerics;

namespace Stykker.NanoCut.Geometry3D;

/// <summary>Location of a face fragment relative to the other solid.</summary>
internal enum Location
{
    Outside,
    Inside,
    OnSame,     // on the other solid's surface, same orientation
    OnOpposite, // on the other solid's surface, opposite orientation
}

/// <summary>
/// Exact plane-based Boolean kernel for closed polyhedra (after Bernstein &amp; Fussell / EMBER principle).
/// <list type="number">
/// <item>Candidate face pairs come from a BVH. A face is split by the plane of every face of the other solid whose
/// plane strictly crosses it and which touches or crosses its own plane (exact side tests); coplanar faces
/// contribute their edge planes.</item>
/// <item>Splitting creates vertices as exact three-plane intersections. All planes are defined by grid points,
/// so the bit budget of docs/bit-budget.md holds and nothing is ever rounded.</item>
/// <item>Each fragment is classified by an exact ray cast (+x) from an interior point with symbolic
/// perturbation for rays through edges or vertices; coplanar overlaps are detected explicitly.</item>
/// </list>
/// </summary>
internal static class SolidBoolean
{

    /// <summary>
    /// A face and the pieces it was split into. Leaves carry the classification. When every leaf below a node ends
    /// up in the result (or none does), the node's own face is used instead of its pieces – exact, because the pieces
    /// partition it – so splits that create no boundary leave no fragments behind.
    /// </summary>
    internal sealed class Node(Face3 face)
    {
        public Face3 Face { get; } = face;
        public Node? Front { get; set; }
        public Node? Back { get; set; }
        public Location Loc { get; set; }
        public bool IsLeaf => Front is null;
    }

    internal sealed record Classified(List<Node> A, List<Node> B);

    public static Classified Classify(IReadOnlyList<Face3> a, IReadOnlyList<Face3> b)
    {
        var bvhA = new Bvh3(a);
        var bvhB = new Bvh3(b);
        return new Classified(Process(a, bvhB), Process(b, bvhA));
    }

    public static List<Face3> Assemble(Classified c, SolidOp op)
    {
        var result = new List<Face3>();
        Func<Location, bool> keepA = op switch
        {
            SolidOp.Union => loc => loc is Location.Outside or Location.OnSame,
            SolidOp.Intersection => loc => loc is Location.Inside or Location.OnSame,
            SolidOp.Difference => loc => loc is Location.Outside or Location.OnOpposite,
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };
        Func<Location, bool> keepB = op switch
        {
            SolidOp.Union => loc => loc == Location.Outside,
            _ => loc => loc == Location.Inside,
        };
        foreach (var n in c.A) Emit(n, keepA, reverse: false, result);
        foreach (var n in c.B) Emit(n, keepB, reverse: op == SolidOp.Difference, result);
        // Merge coplanar neighbours across the whole result (pieces of one face and faces from both operands alike).
        return FaceMerge.MergeAll(result);
    }

    private static bool AllKept(Node n, Func<Location, bool> keep) =>
        n.IsLeaf ? keep(n.Loc) : AllKept(n.Front!, keep) && AllKept(n.Back!, keep);

    private static void Emit(Node n, Func<Location, bool> keep, bool reverse, List<Face3> result)
    {
        if (AllKept(n, keep))
        {
            result.Add(reverse ? n.Face.Reversed() : n.Face);
            return;
        }
        if (n.IsLeaf) return;
        Emit(n.Front!, keep, reverse, result);
        Emit(n.Back!, keep, reverse, result);
    }

    private static List<Node> Process(IReadOnlyList<Face3> faces, Bvh3 other)
    {
        var result = new List<Node>(faces.Count);
        var otherBox = other.Bounds;
        var candidates = new List<Face3>();
        var rayScratch = new List<Face3>();
        foreach (var p in faces)
        {
            var root = new Node(p);
            result.Add(root);
            if (!p.Box.Overlaps(otherBox))
            {
                root.Loc = Location.Outside;
                continue;
            }
            other.Query(p.Box, candidates);
            var planes = new List<Plane3>();
            var seen = new Dictionary<Plane3, int>();
            var reach = new List<Box3>();
            var coplanar = new List<Face3>();
            foreach (var q in candidates)
            {
                int ps = SideSummary(q.Support, p.Vertices);
                if (ps == 2)
                {
                    coplanar.Add(q);
                    foreach (var e in q.Edges) AddPlane(e, q.Box, planes, seen, reach);
                }
                else if (ps == 0 && TouchesOrCrosses(p.Support, q.Vertices))
                {
                    // q's plane crosses p. q must at least touch p's plane: a face touching it only along an
                    // edge can still be where the other surface passes through p (two touching faces from
                    // opposite sides), so only faces strictly on one side are skipped.
                    AddPlane(q.Support, q.Box, planes, seen, reach);
                }
            }

            var fragments = new List<Node> { root };
            for (int pi = 0; pi < planes.Count; pi++)
            {
                var plane = planes[pi];
                var next = new List<Node>(fragments.Count + 4);
                foreach (var n in fragments)
                {
                    // Only fragments that can touch one of the faces spanning this plane need the cut.
                    if (!n.Face.Box.Overlaps(reach[pi])) { next.Add(n); continue; }
                    if (n.Face.Split(plane, out var front, out var back, out _))
                    {
                        n.Front = new Node(front!);
                        n.Back = new Node(back!);
                        next.Add(n.Front);
                        next.Add(n.Back);
                    }
                    else next.Add(n);
                }
                fragments = next;
            }
            foreach (var n in fragments)
                n.Loc = Locate(n.Face, other, coplanar, rayScratch);
        }
        return result;
    }

    private static void AddPlane(in Plane3 plane, in Box3 box, List<Plane3> planes, Dictionary<Plane3, int> seen, List<Box3> reach)
    {
        // Treat a plane and its flip as the same splitter; remember the region of the faces spanning it.
        bool positive = plane.Nx > 0 || (plane.Nx == 0 && (plane.Ny > 0 || (plane.Ny == 0 && plane.Nz > 0)));
        var key = positive ? plane : plane.Flipped();
        if (seen.TryGetValue(key, out int i))
        {
            reach[i] = reach[i].Union(box);
            return;
        }
        seen[key] = planes.Count;
        planes.Add(key);
        reach.Add(box);
    }

    /// <summary>False if all points lie strictly on one side of the plane.</summary>
    private static bool TouchesOrCrosses(in Plane3 plane, Point3[] pts)
    {
        bool pos = false, neg = false;
        foreach (var v in pts)
        {
            int s = v.SideOf(plane);
            if (s == 0) return true;
            pos |= s > 0;
            neg |= s < 0;
        }
        return pos && neg;
    }

    /// <summary>+1 all on the positive side or on, -1 all negative or on, 0 crossing, 2 all on the plane.</summary>
    private static int SideSummary(in Plane3 plane, Point3[] pts)
    {
        bool pos = false, neg = false;
        foreach (var v in pts)
        {
            int s = v.SideOf(plane);
            pos |= s > 0;
            neg |= s < 0;
            if (pos && neg) return 0;
        }
        return pos ? 1 : neg ? -1 : 2;
    }

    private static Location Locate(Face3 f, Bvh3 other, List<Face3> coplanar, List<Face3> scratch)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var c = new Probe(f, attempt);
            foreach (var q in coplanar)
            {
                if (StrictlyInsideCoplanar(q, c))
                    return q.Support == f.Support ? Location.OnSame : Location.OnOpposite;
            }
            int? w = RayWinding(c, f.Support, other, scratch);
            if (w is int winding) return winding > 0 ? Location.Inside : Location.Outside;
        }
        throw new InvalidOperationException("Could not classify a face fragment (degenerate configuration).");
    }

    /// <summary>
    /// An interior point of a fragment: approximate doubles for the filters, the exact homogeneous point only on demand.
    /// </summary>
    private sealed class Probe
    {
        private readonly Face3 _f;
        private readonly int _k;
        private readonly int[] _w;
        private BigPoint? _big;

        public Probe(Face3 f, int attempt)
        {
            _f = f;
            var v = f.Vertices;
            var fans = new List<int>();
            for (int i = 1; i + 1 < v.Length; i++)
                if (!CollinearFiltered(v[0], v[i], v[i + 1])) fans.Add(i);
            if (fans.Count == 0) throw new InvalidOperationException("Degenerate face fragment.");
            _k = fans[(attempt / Weights.Length) % fans.Count];
            _w = Weights[attempt % Weights.Length];
            double sum = _w[0] + _w[1] + _w[2];
            Point3 a = v[0], b = v[_k], c = v[_k + 1];
            X = (_w[0] * a.X + _w[1] * b.X + _w[2] * c.X) / sum;
            Y = (_w[0] * a.Y + _w[1] * b.Y + _w[2] * c.Y) / sum;
            Z = (_w[0] * a.Z + _w[1] * b.Z + _w[2] * c.Z) / sum;
            // Averaging cancels: the error is relative to the vertices, not to the (possibly small) result.
            double max = 1;
            foreach (var p in new[] { a, b, c })
                max = Math.Max(max, Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))));
            Err = 16 * Math.ScaleB(1, -53) * max;
        }

        /// <summary>Bound on the absolute error of <see cref="X"/>, <see cref="Y"/>, <see cref="Z"/>.</summary>
        public double Err { get; }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        public BigPoint Big => _big ??= Exact();

        private BigPoint Exact()
        {
            var v = _f.Vertices;
            var p0 = v[0].Big;
            var p1 = v[_k].Big;
            var p2 = v[_k + 1].Big;
            BigInteger w12 = p1.W * p2.W, w02 = p0.W * p2.W, w01 = p0.W * p1.W;
            return new BigPoint(
                _w[0] * p0.X * w12 + _w[1] * p1.X * w02 + _w[2] * p2.X * w01,
                _w[0] * p0.Y * w12 + _w[1] * p1.Y * w02 + _w[2] * p2.Y * w01,
                _w[0] * p0.Z * w12 + _w[1] * p1.Z * w02 + _w[2] * p2.Z * w01,
                (_w[0] + _w[1] + _w[2]) * p0.W * w12);
        }
    }

    private readonly record struct BigPoint(BigInteger X, BigInteger Y, BigInteger Z, BigInteger W);

    private static readonly int[][] Weights = [[1, 1, 1], [1, 2, 3], [3, 1, 2], [2, 3, 1], [1, 3, 5], [5, 1, 3], [3, 5, 1], [2, 2, 3]];

    /// <summary>Collinearity with a floating-point filter: clearly non-zero cross products decide without BigInteger.</summary>
    private static bool CollinearFiltered(in Point3 a, in Point3 b, in Point3 c)
    {
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        double scale = (Math.Abs(ux) + Math.Abs(uy) + Math.Abs(uz)) * (Math.Abs(vx) + Math.Abs(vy) + Math.Abs(vz));
        // Coordinates are accurate to ~1e-15 relative to their magnitude; allow generously for that.
        double mag = Math.Max(Math.Max(Math.Abs(a.X), Math.Abs(a.Y)), Math.Max(Math.Abs(a.Z), 1));
        double bound = 1e-11 * (scale + mag * (Math.Abs(ux) + Math.Abs(uy) + Math.Abs(uz) + Math.Abs(vx) + Math.Abs(vy) + Math.Abs(vz)));
        if (Math.Abs(nx) > bound || Math.Abs(ny) > bound || Math.Abs(nz) > bound) return false;
        return Collinear(a.Big, b.Big, c.Big);
    }

    private static bool Collinear(
        (BigInteger X, BigInteger Y, BigInteger Z, BigInteger W) a,
        (BigInteger X, BigInteger Y, BigInteger Z, BigInteger W) b,
        (BigInteger X, BigInteger Y, BigInteger Z, BigInteger W) c)
    {
        // Differences scaled by positive W products: u = b - a, v = c - a.
        BigInteger ux = b.X * a.W - a.X * b.W, uy = b.Y * a.W - a.Y * b.W, uz = b.Z * a.W - a.Z * b.W;
        BigInteger vx = c.X * a.W - a.X * c.W, vy = c.Y * a.W - a.Y * c.W, vz = c.Z * a.W - a.Z * c.W;
        return (uy * vz - uz * vy).IsZero && (uz * vx - ux * vz).IsZero && (ux * vy - uy * vx).IsZero;
    }

    private static int Eval(in Plane3 p, in BigPoint c) =>
        ((BigInteger)p.Nx * c.X + (BigInteger)p.Ny * c.Y + (BigInteger)p.Nz * c.Z + (BigInteger)p.D * c.W).Sign;

    private static bool StrictlyInsideCoplanar(Face3 q, Probe c)
    {
        // q comes from the coplanar list: it lies in the fragment's plane, so the probe is on q's plane by construction.
        var k = q.PlanesD;
        for (int i = 0; i < q.Edges.Length; i++)
        {
            int side = Filter.Sign(k, 4 * (i + 1), c.X, c.Y, c.Z, c.Err);
            if (side == Filter.Uncertain) side = Eval(q.Edges[i], c.Big);
            if (side >= 0) return false;
        }
        return true;
    }

    /// <summary>
    /// Winding number of the other solid at c, counted along the ray c + t·(1, 0, 0), t &gt; 0, with c
    /// perturbed symbolically by (0, ε, ε²). Returns null if c lies on the other solid's surface.
    /// </summary>
    private static int? RayWinding(Probe probe, in Plane3 own, Bvh3 other, List<Face3> scratch)
    {
        var ownFlipped = own.Flipped();
        double cx = probe.X, cy = probe.Y, cz = probe.Z;
        other.QueryRayX(cx - 1, cy, cz, scratch);
        int winding = 0;
        foreach (var q in scratch)
        {
            var s = q.Support;
            // A face in the fragment's own plane: the probe lies on that plane by construction. Strictly inside such
            // a face was already reported by the coplanar test (q is a candidate of the fragment's parent face), and
            // the fragment cannot end on q's boundary (it was split by q's edge planes), so the face never counts.
            if (s == own || s == ownFlipped) continue;
            var k = q.PlanesD;
            int sx = Int128.Sign(s.Nx);
            int sp = Filter.Sign(k, 0, cx, cy, cz, probe.Err);
            if (sp == Filter.Uncertain) sp = Eval(s, probe.Big);
            if (sp == 0)
            {
                // c on the plane of q: on the surface if inside or on the boundary of q.
                var c0 = probe.Big;
                bool closedInside = true;
                foreach (var e in q.Edges)
                    if (Eval(e, c0) > 0) { closedInside = false; break; }
                if (closedInside) return null;
                continue;
            }
            if (sx == 0) continue;                // ray parallel to the face
            if (sp * sx > 0) continue;            // plane hit lies behind c (x_hit < c_x)

            // Hit point h = (x_hit, c_y, c_z) with x_hit = -(S_y c_y + S_z c_z + S_d) / S_x.
            BigInteger? t = null;
            bool inside = true;
            for (int ei = 0; ei < q.Edges.Length; ei++)
            {
                var e = q.Edges[ei];
                int sign = Filter.EdgeAtHit(k, 4 * (ei + 1), cy, cz, probe.Err);
                if (sign == Filter.Uncertain)
                {
                    var c = probe.Big;
                    t ??= (BigInteger)s.Ny * c.Y + (BigInteger)s.Nz * c.Z + (BigInteger)s.D * c.W;
                    BigInteger g = -(BigInteger)e.Nx * t.Value + (BigInteger)s.Nx * ((BigInteger)e.Ny * c.Y + (BigInteger)e.Nz * c.Z + (BigInteger)e.D * c.W);
                    sign = g.Sign * sx;
                    if (sign == 0)
                    {
                        // Perturbation c_y += ε, c_z += ε².
                        BigInteger alpha = (BigInteger)e.Ny * s.Nx - (BigInteger)e.Nx * s.Ny;
                        sign = (alpha.IsZero ? ((BigInteger)e.Nz * s.Nx - (BigInteger)e.Nx * s.Nz).Sign : alpha.Sign) * sx;
                    }
                }
                if (sign >= 0) { inside = false; break; }
            }
            if (inside) winding += sx;
        }
        return winding;
    }
}
