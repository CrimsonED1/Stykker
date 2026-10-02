namespace Stykker.NanoCut.Geometry3D;

/// <summary>Boolean operation between two solids.</summary>
public enum SolidOp
{
    /// <summary>Points inside either solid.</summary>
    Union,

    /// <summary>Points inside both solids.</summary>
    Intersection,

    /// <summary>Points inside the first but not the second solid.</summary>
    Difference,
}

/// <summary>
/// An immutable closed polyhedral solid in exact plane-based representation. Input vertices lie on the
/// 1 nm grid; vertices created by Booleans are exact three-plane intersections and are never rounded,
/// so chains of Booleans do not drift. Curved primitives are inscribed polyhedra with a controlled chord error.
/// </summary>
public sealed class Solid
{
    internal Solid(IReadOnlyList<Face3> faces) => Faces = faces;

    internal IReadOnlyList<Face3> Faces { get; }

    /// <summary>Number of (convex) faces.</summary>
    public int FaceCount => Faces.Count;

    /// <summary>The empty solid.</summary>
    public static Solid Empty { get; } = new(Array.Empty<Face3>());

    /// <summary>True if the solid has no faces.</summary>
    public bool IsEmpty => Faces.Count == 0;

    /// <summary>Axis-aligned box between two corners.</summary>
    public static Solid Box(Vec3 min, Vec3 max, Tolerance? tol = null)
    {
        _ = tol; // exact on the grid
        long x0 = Math.Min(min.X, max.X), x1 = Math.Max(min.X, max.X);
        long y0 = Math.Min(min.Y, max.Y), y1 = Math.Max(min.Y, max.Y);
        long z0 = Math.Min(min.Z, max.Z), z1 = Math.Max(min.Z, max.Z);
        if (x0 == x1 || y0 == y1 || z0 == z1) return Empty;
        Vec3 P(long x, long y, long z) => new(x, y, z);
        var quads = new[]
        {
            new[] { P(x0, y0, z0), P(x0, y1, z0), P(x1, y1, z0), P(x1, y0, z0) }, // bottom (-z)
            new[] { P(x0, y0, z1), P(x1, y0, z1), P(x1, y1, z1), P(x0, y1, z1) }, // top (+z)
            new[] { P(x0, y0, z0), P(x1, y0, z0), P(x1, y0, z1), P(x0, y0, z1) }, // -y
            new[] { P(x0, y1, z0), P(x0, y1, z1), P(x1, y1, z1), P(x1, y1, z0) }, // +y
            new[] { P(x0, y0, z0), P(x0, y0, z1), P(x0, y1, z1), P(x0, y1, z0) }, // -x
            new[] { P(x1, y0, z0), P(x1, y1, z0), P(x1, y1, z1), P(x1, y0, z1) }, // +x
        };
        return new Solid(quads.Select(Face3.FromGrid).ToArray());
    }

    /// <summary>Sphere as an inscribed polyhedron with sagitta ≤ <see cref="Tolerance.ChordNm"/> (poles along z).</summary>
    public static Solid Sphere(Vec3 center, double radiusMm, Tolerance? tol = null)
    {
        tol ??= Tolerance.Default;
        var rings = Tessellation.SphereRings(radiusMm * Units.NmPerMm, tol.ChordNm, Frame.Around(0, 0, 1));
        var faces = new List<Face3>();
        for (int j = 0; j + 1 < rings.Count; j++)
            foreach (var (a, b, c) in Tessellation.Zip(rings[j], rings[j + 1]))
                faces.Add(Outward([center + a, center + b, center + c], center));
        return new Solid(faces);
    }

    /// <summary>
    /// Cylinder from <paramref name="a"/> to <paramref name="b"/> (centres of the end caps) as an inscribed prism
    /// with sagitta ≤ <see cref="Tolerance.ChordNm"/>. The top ring is the bottom ring translated exactly by b − a.
    /// </summary>
    public static Solid Cylinder(Vec3 a, Vec3 b, double radiusMm, Tolerance? tol = null)
    {
        tol ??= Tolerance.Default;
        Vec3 t = b - a;
        var frame = Frame.Around(t.X, t.Y, t.Z);
        var ring = Tessellation.Ring(radiusMm * Units.NmPerMm, tol.ChordNm, frame);
        int n = ring.Length;
        var mid = Midpoint(a, b);
        var faces = new List<Face3>(3 * n);
        for (int i = 0; i < n; i++)
        {
            Vec3 p = a + ring[i], q = a + ring[(i + 1) % n];
            faces.Add(Outward([p, q, q + t, p + t], mid));
            faces.Add(Outward([a, q, p], mid));
            faces.Add(Outward([b, p + t, q + t], mid));
        }
        return new Solid(faces);
    }

    /// <summary>
    /// Cone or frustum from <paramref name="a"/> (radius <paramref name="radiusAMm"/>) to <paramref name="b"/>
    /// (radius <paramref name="radiusBMm"/>, may be 0 for a tip), inscribed with sagitta ≤ <see cref="Tolerance.ChordNm"/>.
    /// </summary>
    public static Solid Cone(Vec3 a, Vec3 b, double radiusAMm, double radiusBMm, Tolerance? tol = null)
    {
        tol ??= Tolerance.Default;
        if (radiusAMm <= 0 && radiusBMm <= 0) throw new ArgumentOutOfRangeException(nameof(radiusAMm));
        Vec3 t = b - a;
        var frame = Frame.Around(t.X, t.Y, t.Z);
        double rMax = Math.Max(radiusAMm, radiusBMm) * Units.NmPerMm;
        int n = (Discretization.SegmentCount(rMax, tol.ChordNm) + 3) / 4 * 4;
        Vec3[] RingAt(Vec3 c, double rMm) => rMm <= 0
            ? [c]
            : Enumerable.Range(0, n).Select(i => c + frame.Offset(rMm * Units.NmPerMm, 2 * Math.PI * i / n, 0)).ToArray();
        var ra = RingAt(a, radiusAMm);
        var rb = RingAt(b, radiusBMm);
        var mid = Midpoint(a, b);
        var faces = new List<Face3>();
        foreach (var (p, q, r) in Tessellation.Zip(ra, rb)) faces.Add(Outward([p, q, r], mid));
        if (ra.Length > 1) for (int i = 0; i < n; i++) faces.Add(Outward([a, ra[i], ra[(i + 1) % n]], mid));
        if (rb.Length > 1) for (int i = 0; i < n; i++) faces.Add(Outward([b, rb[i], rb[(i + 1) % n]], mid));
        return new Solid(faces);
    }

    /// <summary>
    /// Capsule (sphere swept along the segment a→b): two inscribed hemispheres and a prism. The end hemisphere
    /// and ring are exact translations of the start ones, so the swept hull of the polyhedral ball is exact.
    /// </summary>
    public static Solid Capsule(Vec3 a, Vec3 b, double radiusMm, Tolerance? tol = null)
    {
        tol ??= Tolerance.Default;
        Vec3 t = b - a;
        if (t == default) return Sphere(a, radiusMm, tol);
        var rings = Tessellation.SphereRings(radiusMm * Units.NmPerMm, tol.ChordNm, Frame.Around(t.X, t.Y, t.Z));
        int eq = rings.Count / 2;
        var mid = Midpoint(a, b);
        var faces = new List<Face3>();
        for (int j = 0; j < eq; j++)
            foreach (var (p, q, r) in Tessellation.Zip(rings[j], rings[j + 1]))
                faces.Add(Outward([a + p, a + q, a + r], mid));
        for (int j = eq; j + 1 < rings.Count; j++)
            foreach (var (p, q, r) in Tessellation.Zip(rings[j], rings[j + 1]))
                faces.Add(Outward([b + p, b + q, b + r], mid));
        var e = rings[eq];
        for (int i = 0; i < e.Length; i++)
        {
            Vec3 p = a + e[i], q = a + e[(i + 1) % e.Length];
            faces.Add(Outward([p, q, q + t, p + t], mid));
        }
        return new Solid(faces);
    }

    /// <summary>
    /// Solid from a closed, outward-oriented triangle mesh on the grid (e.g. STL/OBJ import).
    /// Triangles that are degenerate on the grid (collinear vertices) are skipped.
    /// </summary>
    public static Solid FromTriangles(IReadOnlyList<Vec3> vertices, IReadOnlyList<int> indices)
    {
        if (indices.Count % 3 != 0) throw new ArgumentException("Index count must be a multiple of 3.", nameof(indices));
        var faces = new List<Face3>(indices.Count / 3);
        for (int i = 0; i < indices.Count; i += 3)
        {
            Vec3 a = vertices[indices[i]], b = vertices[indices[i + 1]], c = vertices[indices[i + 2]];
            if (Plane3.FromPoints(a, b, c).IsDegenerate) continue;
            faces.Add(Face3.FromGrid([a, b, c]));
        }
        return new Solid(faces);
    }

    /// <summary>
    /// Prism from a planar region: the region (in the xy-plane, mm) is extruded from z0 to z1 and then placed by
    /// <paramref name="placement"/> (applied in double precision before rounding to the grid). Non-convex regions
    /// and holes are supported; caps are split into convex pieces.
    /// </summary>
    public static Solid Extrude(Geometry2D.Region2 region, double z0Mm, double z1Mm, Pose3? placement = null)
    {
        var norm = region.Normalize();
        if (z1Mm < z0Mm) (z0Mm, z1Mm) = (z1Mm, z0Mm);
        if (z1Mm == z0Mm || norm.Contours.Count == 0) return Empty;
        double z0 = z0Mm * Units.NmPerMm, z1 = z1Mm * Units.NmPerMm;
        var pose = placement ?? Pose3.Identity;
        Vec3 P(Vec2 p, double z) => Place(pose, p.X, p.Y, z);
        var tris = new List<Vec3[]>();
        foreach (var c in norm.Contours)
            for (int i = 0; i < c.Count; i++)
            {
                Vec2 a = c[i], b = c[(i + 1) % c.Count];
                // Interior lies left of a→b, so (a0, b0, b1, a1) faces outwards.
                tris.Add([P(a, z0), P(b, z0), P(b, z1)]);
                tris.Add([P(a, z0), P(b, z1), P(a, z1)]);
            }
        foreach (var piece in Geometry2D.Triangulator2.ConvexParts(norm))
        {
            for (int i = 1; i + 1 < piece.Length; i++)
            {
                tris.Add([P(piece[0], z1), P(piece[i], z1), P(piece[i + 1], z1)]);
                tris.Add([P(piece[0], z0), P(piece[i + 1], z0), P(piece[i], z0)]);
            }
        }
        return FromTriangleList(tris);
    }

    /// <summary>
    /// Solid of revolution: the region is a profile in the half-plane x = r ≥ 0, y = z (mm), revolved once about
    /// the z-axis with sagitta ≤ <see cref="Tolerance.ChordNm"/>, then placed by <paramref name="placement"/>.
    /// </summary>
    public static Solid Revolve(Geometry2D.Region2 profile, Tolerance? tol = null, Pose3? placement = null)
    {
        tol ??= Tolerance.Default;
        var norm = profile.Normalize();
        if (norm.Contours.Count == 0) return Empty;
        long rMax = norm.Contours.SelectMany(c => c.Points.ToArray()).Max(p => p.X);
        if (norm.Contours.Any(c => c.Points.ToArray().Any(p => p.X < 0)))
            throw new ArgumentException("Profile must lie in r = x ≥ 0.", nameof(profile));
        int n = (Discretization.SegmentCount(rMax, tol.ChordNm) + 3) / 4 * 4;
        var pose = placement ?? Pose3.Identity;
        var cos = new double[n];
        var sin = new double[n];
        for (int k = 0; k < n; k++) { cos[k] = Math.Cos(2 * Math.PI * k / n); sin[k] = Math.Sin(2 * Math.PI * k / n); }
        Vec3 P(Vec2 p, int k) => Place(pose, p.X * cos[k % n], p.X * sin[k % n], p.Y);

        var tris = new List<Vec3[]>();
        foreach (var c in norm.Contours)
            for (int i = 0; i < c.Count; i++)
            {
                Vec2 a = c[i], b = c[(i + 1) % c.Count];
                if (a.X == 0 && b.X == 0) continue;
                for (int k = 0; k < n; k++)
                {
                    if (a.X == 0) tris.Add([P(a, k), P(b, k + 1), P(b, k)]);
                    else if (b.X == 0) tris.Add([P(a, k), P(a, k + 1), P(b, k)]);
                    else
                    {
                        tris.Add([P(a, k), P(a, k + 1), P(b, k + 1)]);
                        tris.Add([P(a, k), P(b, k + 1), P(b, k)]);
                    }
                }
            }
        return FromTriangleList(tris);
    }

    /// <summary>
    /// The solid moved by a rigid motion. Vertices are rounded to the grid (≤ 0.87 nm); faces are re-triangulated
    /// so that every face stays exactly planar.
    /// </summary>
    public Solid Transform(Pose3 pose)
    {
        var tris = new List<Vec3[]>();
        foreach (var f in Faces)
        {
            var v = f.Vertices;
            for (int i = 1; i + 1 < v.Length; i++)
                tris.Add([Place(pose, v[0].X, v[0].Y, v[0].Z), Place(pose, v[i].X, v[i].Y, v[i].Z), Place(pose, v[i + 1].X, v[i + 1].Y, v[i + 1].Z)]);
        }
        return FromTriangleList(tris);
    }

    private static Vec3 Place(Pose3 pose, double x, double y, double z)
    {
        var (px, py, pz) = pose.Apply(x, y, z);
        return Vec3.Nm((long)Math.Round(px, MidpointRounding.AwayFromZero), (long)Math.Round(py, MidpointRounding.AwayFromZero), (long)Math.Round(pz, MidpointRounding.AwayFromZero));
    }

    private static Solid FromTriangleList(List<Vec3[]> tris)
    {
        var faces = new List<Face3>(tris.Count);
        foreach (var t in tris)
            if (!Plane3.FromPoints(t[0], t[1], t[2]).IsDegenerate) faces.Add(Face3.FromGrid(t));
        return new Solid(faces);
    }

    private static Vec3 Midpoint(Vec3 a, Vec3 b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);

    private static Face3 Outward(Vec3[] pts, Vec3 interior)
    {
        int o = Predicates.Orient3D(pts[0], pts[1], pts[2], interior);
        if (o == 0) throw new InvalidOperationException("Degenerate primitive face.");
        if (o > 0) Array.Reverse(pts);
        return Face3.FromGrid(pts);
    }

    /// <summary>Boolean operation.</summary>
    public Solid Boolean(Solid other, SolidOp op)
    {
        ArgumentNullException.ThrowIfNull(other);
        var c = SolidBoolean.Classify(Faces, other.Faces);
        return new Solid(SolidBoolean.Assemble(c, op));
    }

    /// <summary>Remaining part (this − tool) and removed part (this ∩ tool) in one pass.</summary>
    public (Solid Remaining, Solid Removed) Split(Solid tool)
    {
        var c = SolidBoolean.Classify(Faces, tool.Faces);
        return (new Solid(SolidBoolean.Assemble(c, SolidOp.Difference)), new Solid(SolidBoolean.Assemble(c, SolidOp.Intersection)));
    }

    /// <summary>Union.</summary>
    public static Solid operator |(Solid a, Solid b) => a.Boolean(b, SolidOp.Union);

    /// <summary>Intersection.</summary>
    public static Solid operator &(Solid a, Solid b) => a.Boolean(b, SolidOp.Intersection);

    /// <summary>Difference.</summary>
    public static Solid operator -(Solid a, Solid b) => a.Boolean(b, SolidOp.Difference);

    /// <summary>
    /// Maximum number of threads the 3D kernel uses (face classification, hull faces, unions, cut chains). Defaults to
    /// the processor count; 1 runs everything sequentially. Results do not depend on this setting.
    /// </summary>
    public static int MaxParallelism
    {
        get => SolidBoolean.MaxParallelism;
        set => SolidBoolean.MaxParallelism = Math.Max(1, value);
    }

    /// <summary>
    /// Subtracts the tools from the workpiece in order (a cut chain). Each tool is built by its factory; while one tool is
    /// subtracted, the next is built on another core (tool construction such as a convex hull often costs as much as the
    /// cut). The result is the same as subtracting one after another. Sequential if <c>MaxParallelism</c> is 1.
    /// </summary>
    public static Solid SubtractInOrder(Solid workpiece, IEnumerable<Func<Solid>> tools)
    {
        ArgumentNullException.ThrowIfNull(workpiece);
        ArgumentNullException.ThrowIfNull(tools);
        var work = workpiece;
        if (SolidBoolean.MaxParallelism <= 1)
        {
            foreach (var make in tools) work -= make();
            return work;
        }
        using var e = tools.GetEnumerator();
        if (!e.MoveNext()) return work;
        var next = Task.Run(e.Current);
        while (true)
        {
            var tool = next.GetAwaiter().GetResult(); // rethrows the factory's own exception
            bool more = e.MoveNext();
            if (more) next = Task.Run(e.Current);
            try
            {
                work -= tool;
            }
            catch
            {
                if (more) next.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // observe, don't leak
                throw;
            }
            if (!more) return work;
        }
    }

    /// <summary>
    /// Union of many solids as a balanced tree (pairs of neighbours first): far fewer faces pass through each Boolean
    /// than in a left-to-right chain. The pairs of one level are independent and run in parallel.
    /// </summary>
    public static Solid UnionAll(IReadOnlyList<Solid> solids)
    {
        ArgumentNullException.ThrowIfNull(solids);
        if (solids.Count == 0) return Empty;
        var level = solids.ToArray();
        while (level.Length > 1)
        {
            var next = new Solid[(level.Length + 1) / 2];
            var current = level;
            int pairs = current.Length / 2;
            if (pairs > 1 && SolidBoolean.MaxParallelism > 1)
                Parallel.For(0, pairs, new ParallelOptions { MaxDegreeOfParallelism = SolidBoolean.MaxParallelism },
                    i => next[i] = current[2 * i] | current[2 * i + 1]);
            else
                for (int i = 0; i < pairs; i++) next[i] = current[2 * i] | current[2 * i + 1];
            if (current.Length % 2 == 1) next[^1] = current[^1];
            level = next;
        }
        return level[0];
    }


    /// <summary>All face vertices (with repetitions).</summary>
    public IEnumerable<Point3> Vertices => Faces.SelectMany(f => f.Vertices);

    /// <summary>Volume in mm³ (divergence theorem over the exact vertices, evaluated in double).</summary>
    public double VolumeMm3
    {
        get
        {
            if (Faces.Count == 0) return 0;
            var o = Faces[0].Vertices[0];
            double ox = o.X, oy = o.Y, oz = o.Z, sum = 0;
            foreach (var f in Faces)
            {
                var v = f.Vertices;
                double ax = v[0].X - ox, ay = v[0].Y - oy, az = v[0].Z - oz;
                for (int i = 1; i + 1 < v.Length; i++)
                {
                    double bx = v[i].X - ox, by = v[i].Y - oy, bz = v[i].Z - oz;
                    double cx = v[i + 1].X - ox, cy = v[i + 1].Y - oy, cz = v[i + 1].Z - oz;
                    sum += ax * (by * cz - bz * cy) + ay * (bz * cx - bx * cz) + az * (bx * cy - by * cx);
                }
            }
            return sum / 6 / 1e18;
        }
    }

    /// <summary>Surface area in mm².</summary>
    public double SurfaceAreaMm2
    {
        get
        {
            double sum = 0;
            foreach (var f in Faces)
            {
                var v = f.Vertices;
                for (int i = 1; i + 1 < v.Length; i++)
                {
                    double bx = v[i].X - v[0].X, by = v[i].Y - v[0].Y, bz = v[i].Z - v[0].Z;
                    double cx = v[i + 1].X - v[0].X, cy = v[i + 1].Y - v[0].Y, cz = v[i + 1].Z - v[0].Z;
                    double nx = by * cz - bz * cy, ny = bz * cx - bx * cz, nz = bx * cy - by * cx;
                    sum += Math.Sqrt(nx * nx + ny * ny + nz * nz) / 2;
                }
            }
            return sum / 1e12;
        }
    }

    /// <summary>Axis-aligned bounds in mm, or null if empty.</summary>
    public (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)? BoundsMm
    {
        get
        {
            if (Faces.Count == 0) return null;
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            foreach (var v in Vertices)
            {
                x0 = Math.Min(x0, v.X); y0 = Math.Min(y0, v.Y); z0 = Math.Min(z0, v.Z);
                x1 = Math.Max(x1, v.X); y1 = Math.Max(y1, v.Y); z1 = Math.Max(z1, v.Z);
            }
            const double k = 1e-6;
            return (x0 * k, y0 * k, z0 * k, x1 * k, y1 * k, z1 * k);
        }
    }

    /// <summary>Saves the solid losslessly in the internal .ncs format.</summary>
    public void Save(string path) => NcsFormat.Save(this, path);

    /// <summary>Loads a solid saved with <see cref="Save"/>.</summary>
    public static Solid Load(string path) => NcsFormat.Load(path);

    /// <summary>Triangle buffers for three.js / Babylon.js (flat shading).</summary>
    public MeshBuffers ToMeshBuffers(OriginMode origin = OriginMode.Centroid) => MeshBuffers.From(this, origin);
}
