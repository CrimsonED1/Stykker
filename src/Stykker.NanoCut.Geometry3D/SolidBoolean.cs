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

    /// <summary>Maximum number of threads used to classify faces (1 = sequential). Defaults to the processor count.</summary>
    public static int MaxParallelism { get; set; } = Environment.ProcessorCount;

    // Below this many faces the thread start-up costs more than it saves.
    private const int ParallelThreshold = 64;

    private sealed class Buffers
    {
        public readonly List<Face3> Candidates = [];
        public readonly List<Face3> RayScratch = [];
        public readonly List<Plane3> Planes = [];
        public readonly Dictionary<Plane3, int> Seen = [];
        public readonly List<Box3> Reach = [];
        public readonly List<Face3> Coplanar = [];
    }

    /// <summary>
    /// Splits and classifies every face against the other solid. Faces are independent, so they are processed in
    /// parallel (one buffer set per thread); results are stored by index, so the output does not depend on scheduling.
    /// </summary>
    private static List<Node> Process(IReadOnlyList<Face3> faces, Bvh3 other)
    {
        var nodes = new Node[faces.Count];
        var otherBox = other.Bounds;
        if (MaxParallelism <= 1 || faces.Count < ParallelThreshold)
        {
            var b = new Buffers();
            for (int i = 0; i < nodes.Length; i++) nodes[i] = ProcessFace(faces[i], other, otherBox, b);
        }
        else
        {
            try
            {
                Parallel.For(0, nodes.Length, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism },
                    () => new Buffers(),
                    (i, _, b) => { nodes[i] = ProcessFace(faces[i], other, otherBox, b); return b; },
                    _ => { });
            }
            catch (AggregateException ae) when (ae.InnerExceptions.Count > 0)
            {
                // Same exception type as the sequential path, independent of face count and parallelism.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw();
                throw;
            }
        }
        return [.. nodes];
    }

    private static Node ProcessFace(Face3 p, Bvh3 other, in Box3 otherBox, Buffers b)
    {
        var root = new Node(p);
        if (!p.Box.Overlaps(otherBox))
        {
            root.Loc = Location.Outside;
            return root;
        }
        other.Query(p.Box, b.Candidates);
        b.Planes.Clear();
        b.Seen.Clear();
        b.Reach.Clear();
        b.Coplanar.Clear();
        if (KernelStats.Counting) KernelStats.Mine.FaceCandidates += b.Candidates.Count;
        foreach (var q in b.Candidates)
        {
            int ps = SideSummary(q.Support, p.Vertices);
            // Faces that cannot meet need no cut: one lies strictly outside an edge plane of the other.
            if (ps != 2 && (Separated(q, p.Vertices) || Separated(p, q.Vertices))) continue;
            if (KernelStats.Counting) KernelStats.Mine.PairsTested++;
            if (ps == 2)
            {
                b.Coplanar.Add(q);
                foreach (var e in q.Edges) AddPlane(e, q.Box, b.Planes, b.Seen, b.Reach);
            }
            else if (ps == 0 && TouchesOrCrosses(p.Support, q.Vertices))
            {
                // q's plane crosses p. q must at least touch p's plane: a face touching it only along an
                // edge can still be where the other surface passes through p (two touching faces from
                // opposite sides), so only faces strictly on one side are skipped.
                AddPlane(q.Support, q.Box, b.Planes, b.Seen, b.Reach);
            }
        }

        if (b.Planes.Count == 0)
        {
            root.Loc = Locate(p, other, b.Coplanar, b.RayScratch);
            return root;
        }
        var fragments = new List<Node> { root };
        for (int pi = 0; pi < b.Planes.Count; pi++)
        {
            var plane = b.Planes[pi];
            var next = new List<Node>(fragments.Count + 4);
            foreach (var n in fragments)
            {
                // Only fragments that can touch one of the faces spanning this plane need the cut.
                if (!n.Face.Box.Overlaps(b.Reach[pi])) { next.Add(n); continue; }
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
            n.Loc = Locate(n.Face, other, b.Coplanar, b.RayScratch);
        return root;
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

    /// <summary>
    /// True if all points lie strictly outside one edge plane of <paramref name="f"/>: then their convex hull cannot meet
    /// the face (the face lies in the closed inner half-space of every edge plane).
    /// </summary>
    private static bool Separated(Face3 f, Point3[] pts)
    {
        if (KernelStats.Counting) KernelStats.CountVertices(pts);
        foreach (var e in f.Edges)
        {
            bool all = true;
            foreach (var v in pts)
                if (v.SideOf(e) <= 0) { all = false; break; }
            if (all) return true;
        }
        return false;
    }

    /// <summary>False if all points lie strictly on one side of the plane.</summary>
    private static bool TouchesOrCrosses(in Plane3 plane, Point3[] pts)
    {
        if (KernelStats.Counting) KernelStats.CountVertices(pts);
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
        if (KernelStats.Counting) KernelStats.CountVertices(pts);
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
        for (int attempt = 0; attempt < Weights.Length; attempt++)
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
            // First non-degenerate fan triangle (v0, v_k, v_k+1); the attempts (< Weights.Length) vary the weights.
            _k = -1;
            for (int i = 1; i + 1 < v.Length && _k < 0; i++)
                if (!CollinearFiltered(v[0], v[i], v[i + 1])) _k = i;
            if (_k < 0) throw new InvalidOperationException("Degenerate face fragment.");
            _w = Weights[attempt % Weights.Length];
            double sum = _w[0] + _w[1] + _w[2];
            Point3 a = v[0], b = v[_k], c = v[_k + 1];
            X = (_w[0] * a.X + _w[1] * b.X + _w[2] * c.X) / sum;
            Y = (_w[0] * a.Y + _w[1] * b.Y + _w[2] * c.Y) / sum;
            Z = (_w[0] * a.Z + _w[1] * b.Z + _w[2] * c.Z) / sum;
            // Averaging cancels: the error is relative to the vertices, not to the (possibly small) result.
            double max = Math.Max(1, Math.Max(MaxAbs(a), Math.Max(MaxAbs(b), MaxAbs(c))));
            Err = 16 * Math.ScaleB(1, -53) * max;
        }

        private static double MaxAbs(in Point3 p) => Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z)));

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

    /// <summary>
    /// Filtered sign of edge plane e (offset in k) where the ray v = cv, w = cw (canonical frame of ax) meets the support
    /// plane (offset 0, S_u ≠ 0); see <see cref="Filter.EdgeAtHit(double[], int, double, double, double)"/>.
    /// </summary>
    private static int EdgeAtHit(double[] k, int e, in RayAxis ax, double cv, double cw, double pointErr)
    {
        double su = ax.K(k, 0, 0), sv = ax.K(k, 0, 1) * cv, sw = ax.K(k, 0, 2) * cw, sd = k[3];
        double uHit = -(sv + sw + sd) / su;
        double eu = ax.K(k, e, 0), ev = ax.K(k, e, 1) * cv, ew = ax.K(k, e, 2) * cw, ed = k[e + 3];
        double g = eu * uHit + ev + ew + ed;
        double ratio = Math.Abs(eu) / Math.Abs(su);
        double bound = Filter.Rel * (Math.Abs(eu) * (Math.Abs(sv) + Math.Abs(sw) + Math.Abs(sd)) / Math.Abs(su)
                                     + Math.Abs(ev) + Math.Abs(ew) + Math.Abs(ed))
                       + 2 * pointErr * (Math.Abs(ax.K(k, e, 1)) + ratio * Math.Abs(ax.K(k, 0, 1))
                                         + Math.Abs(ax.K(k, e, 2)) + ratio * Math.Abs(ax.K(k, 0, 2)));
        return g > bound ? 1 : g < -bound ? -1 : Filter.Uncertain;
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
    /// Ray direction for a winding count: an axis (0 = x, 1 = y, 2 = z) and a sign, mapped to the canonical frame
    /// u = sign·p[A], v = p[B], w = p[C] in which the ray runs along +u. Plane values are invariant under the map.
    /// </summary>
    private readonly record struct RayAxis(int A, int B, int C, int Sign)
    {
        public static RayAxis Shortest(in Box3 box, double x, double y, double z)
        {
            // The ray leaving the other solid's box soonest meets the fewest faces.
            Span<double> d = [box.MaxX - x, x - box.MinX, box.MaxY - y, y - box.MinY, box.MaxZ - z, z - box.MinZ];
            int best = 0;
            for (int i = 1; i < 6; i++) if (d[i] < d[best]) best = i;
            int axis = best / 2, sign = best % 2 == 0 ? 1 : -1;
            return new RayAxis(axis, (axis + 1) % 3, (axis + 2) % 3, sign);
        }

        public double U(double x, double y, double z) => Sign * Pick(A, x, y, z);
        public double V(double x, double y, double z) => Pick(B, x, y, z);
        public double W(double x, double y, double z) => Pick(C, x, y, z);

        private static double Pick(int i, double x, double y, double z) => i == 0 ? x : i == 1 ? y : z;

        // Plane coefficient in the canonical frame: 0 = n_u, 1 = n_v, 2 = n_w.
        public Int128 N(in Plane3 p, int i) => i == 0 ? Sign * Comp(p, A) : Comp(p, i == 1 ? B : C);

        private static Int128 Comp(in Plane3 p, int i) => i == 0 ? p.Nx : i == 1 ? p.Ny : p.Nz;

        public double K(double[] k, int o, int i) => i == 0 ? Sign * k[o + A] : k[o + (i == 1 ? B : C)];

        public BigInteger P(in BigPoint c, int i) => i == 0 ? Sign * Comp(c, A) : Comp(c, i == 1 ? B : C);

        private static BigInteger Comp(in BigPoint c, int i) => i == 0 ? c.X : i == 1 ? c.Y : c.Z;
    }

    /// <summary>
    /// Winding number of the other solid at c, counted along an axis-parallel ray (the direction that leaves the other
    /// solid's box soonest), in the canonical frame (u, v, w) with the ray along +u and c perturbed symbolically by
    /// (0, ε, ε²). Returns null if c lies on the other solid's surface.
    /// </summary>
    private static int? RayWinding(Probe probe, in Plane3 own, Bvh3 other, List<Face3> scratch)
    {
        var ownFlipped = own.Flipped();
        var ax = RayAxis.Shortest(other.Bounds, probe.X, probe.Y, probe.Z);
        double cu = ax.U(probe.X, probe.Y, probe.Z), cv = ax.V(probe.X, probe.Y, probe.Z), cw = ax.W(probe.X, probe.Y, probe.Z);
        other.QueryRay(ax.A, ax.Sign, probe.X, probe.Y, probe.Z, scratch);
        int winding = 0;
        foreach (var q in scratch)
        {
            var s = q.Support;
            // A face in the fragment's own plane: the probe lies on that plane by construction. Strictly inside such
            // a face was already reported by the coplanar test (q is a candidate of the fragment's parent face), and
            // the fragment cannot end on q's boundary (it was split by q's edge planes), so the face never counts.
            if (s == own || s == ownFlipped) continue;
            var k = q.PlanesD;
            int su = Int128.Sign(ax.N(s, 0));
            int sp = Filter.Sign(k, 0, probe.X, probe.Y, probe.Z, probe.Err);
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
            if (su == 0) continue;                // ray parallel to the face
            if (sp * su > 0) continue;            // plane hit lies behind c (u_hit < c_u)

            // Hit point h = (u_hit, c_v, c_w) with u_hit = -(S_v c_v + S_w c_w + S_d) / S_u.
            BigInteger? t = null;
            bool inside = true;
            for (int ei = 0; ei < q.Edges.Length; ei++)
            {
                var e = q.Edges[ei];
                int sign = EdgeAtHit(k, 4 * (ei + 1), ax, cv, cw, probe.Err);
                if (sign == Filter.Uncertain)
                {
                    var c = probe.Big;
                    BigInteger snu = (BigInteger)ax.N(s, 0), snv = (BigInteger)ax.N(s, 1), snw = (BigInteger)ax.N(s, 2);
                    BigInteger enu = (BigInteger)ax.N(e, 0), env = (BigInteger)ax.N(e, 1), enw = (BigInteger)ax.N(e, 2);
                    BigInteger pv = ax.P(c, 1), pw = ax.P(c, 2);
                    t ??= snv * pv + snw * pw + (BigInteger)s.D * c.W;
                    BigInteger g = -enu * t.Value + snu * (env * pv + enw * pw + (BigInteger)e.D * c.W);
                    sign = g.Sign * su;
                    if (sign == 0)
                    {
                        // Perturbation c_v += ε, c_w += ε².
                        BigInteger alpha = env * snu - enu * snv;
                        sign = (alpha.IsZero ? (enw * snu - enu * snw).Sign : alpha.Sign) * su;
                    }
                }
                if (sign >= 0) { inside = false; break; }
            }
            if (inside) winding += su;
        }
        return winding;
    }
}
