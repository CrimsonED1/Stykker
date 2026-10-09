namespace Stykker.NanoCut.Gpu;

/// <summary>
/// One half-space of a convex tool: the points <c>n·p ≤ d</c>, with <c>n</c> a unit normal and <c>d</c> the distance
/// of the plane from the tool's origin in mm. A tool is the intersection of all of its half-spaces, so every normal
/// points out of the tool.
/// </summary>
/// <param name="Nx">First component of the unit normal.</param>
/// <param name="Ny">Second component of the unit normal.</param>
/// <param name="Nz">Third component of the unit normal.</param>
/// <param name="D">Distance of the plane from the tool's origin along the normal, mm.</param>
public readonly record struct HalfSpace(double Nx, double Ny, double Nz, double D);

/// <summary>
/// A convex tool as the intersection of at most <see cref="MaxPlanes"/> half-spaces, which is the shape the dexel
/// kernel can sweep: a rack tooth, a polyhedral grinding grain, any box. <see cref="BallStep"/> stays the sphere,
/// and a sphere is cheaper than the inscribed polyhedron, so a program of round tools keeps using it.
/// </summary>
/// <remarks>
/// The kernel finds where a vertical line meets the swept tool as a tiny linear program in (z, t) over the
/// half-spaces, so what it needs is the planes and nothing else. <see cref="CornersMm"/> is what the host side
/// needs: the corners bound the tool, and the corner set is what the binning rotates to decide which columns a step
/// can reach. They are worked out once, here, by solving every triple of planes and keeping the points that lie
/// inside all half-spaces.
/// <para>
/// A tool the kernel cannot sweep is refused rather than allowed to produce a map that removes the whole stock, and
/// there are two ways to ask for one. An unbounded body has a direction in which nothing closes, and the kernel would
/// read infinity for the end of the sweep there; a body can be unbounded and still have corners, which is why the
/// normals are checked for it on their own. And a half-space that carries no corner is either redundant or does
/// nothing for the bounding that is there — a redundant one raises no end, but it makes the corner set a description
/// of the body that the caller did not ask for, so it goes too.
/// </para>
/// </remarks>
public sealed class ConvexTool
{
    /// <summary>
    /// Half-spaces the dexel kernel can sweep. The limit is where the interval search, which is cubic in the number
    /// of half-spaces, stops being worth it (<see cref="ConvexProfile.MaxPlanes"/>); a larger tool is split, which is
    /// what <c>ToolShape</c> does for it too.
    /// </summary>
    public const int MaxPlanes = ConvexProfile.MaxPlanes;

    /// <summary>How far a corner may sit outside a half-space and still count as inside, mm (0.1 nm).</summary>
    private const double SlackMm = 1e-7;

    /// <summary>Below this determinant a triple of planes is parallel or degenerate and yields no corner.</summary>
    private const double MinDet = 1e-9;

    /// <summary>Below this length the cross product of two unit normals is too short to name a direction (1 µrad).</summary>
    private const double MinCross = 1e-6;

    /// <summary>How far a unit direction may sit behind every normal before the tool counts as unbounded there.</summary>
    private const double SlackDir = 1e-9;

    private ConvexTool(HalfSpace[] planes, (double X, double Y, double Z)[] corners)
    {
        Planes = planes;
        CornersMm = corners;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue, radius = 0;
        foreach (var (x, y, z) in corners)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
            radius = Math.Max(radius, Math.Sqrt(x * x + y * y + z * z));
        }
        BoxMm = (minX, minY, minZ, maxX, maxY, maxZ);
        RadiusMm = radius;
    }

    /// <summary>The half-spaces, in the given order; the kernel indexes them in exactly this order.</summary>
    public IReadOnlyList<HalfSpace> Planes { get; }

    /// <summary>Number of half-spaces.</summary>
    public int PlaneCount => Planes.Count;

    /// <summary>
    /// The corners of the tool in its own frame, mm. The sweep of a pose pair has the corners
    /// R·c and R·c + w for these c and the move w, so bounding the sweep is bounding those.
    /// </summary>
    public IReadOnlyList<(double X, double Y, double Z)> CornersMm { get; }

    /// <summary>The axis-aligned box around the tool in its own frame, mm.</summary>
    public (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) BoxMm { get; }

    /// <summary>The radius of the smallest sphere about the tool's origin that holds it, mm.</summary>
    public double RadiusMm { get; }

    /// <summary>
    /// Builds a tool from half-spaces. The normals are normalised and <see cref="HalfSpace.D"/> is left alone, because
    /// it is the distance in mm and not a coefficient: <c>n·p ≤ d</c> and <c>(n/|n|)·p ≤ d</c> describe the same
    /// plane at the same place, so scaling the normal must not move it.
    /// </summary>
    /// <param name="planes">Between 1 and <see cref="MaxPlanes"/> half-spaces with non-zero normals.</param>
    public static ConvexTool FromPlanes(params HalfSpace[] planes)
    {
        ArgumentNullException.ThrowIfNull(planes);
        if (planes.Length is 0 or > MaxPlanes)
            throw new ArgumentOutOfRangeException(nameof(planes), planes.Length,
                $"a convex tool has 1 to {MaxPlanes} half-spaces");
        var unit = new HalfSpace[planes.Length];
        for (int i = 0; i < planes.Length; i++)
        {
            var (nx, ny, nz) = (planes[i].Nx, planes[i].Ny, planes[i].Nz);
            double n = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (!(n > 0) || !double.IsFinite(n))
                throw new ArgumentException($"half-space {i} has the zero normal ({nx}, {ny}, {nz}).", nameof(planes));
            if (double.IsNaN(planes[i].D)) throw new ArgumentException($"half-space {i} has no distance.", nameof(planes));
            unit[i] = new HalfSpace(nx / n, ny / n, nz / n, planes[i].D);
        }
        if (!Bounded(unit))
            throw new ArgumentException(
                "the normals all look the same way, so the tool is unbounded and the kernel would read infinity for " +
                "an end of the sweep and take the whole column. Add a half-space that closes it.", nameof(planes));
        return new(unit, Corners(unit));
    }

    /// <summary>An axis-aligned box between two corners, six half-spaces.</summary>
    public static ConvexTool Box(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        if (!(maxX > minX) || !(maxY > minY) || !(maxZ > minZ))
            throw new ArgumentException($"a box needs min < max on every axis, got ({minX}, {minY}, {minZ}) to ({maxX}, {maxY}, {maxZ}).");
        return FromPlanes(
            new HalfSpace(-1, 0, 0, -minX), new HalfSpace(1, 0, 0, maxX),
            new HalfSpace(0, -1, 0, -minY), new HalfSpace(0, 1, 0, maxY),
            new HalfSpace(0, 0, -1, -minZ), new HalfSpace(0, 0, 1, maxZ));
    }

    /// <summary>
    /// The octahedron |x − c| + |y − c| + |z − c| ≤ r, eight half-spaces with normals (±1, ±1, ±1). It is the
    /// grain shape of <c>GrindingWheel</c>, and the case the octahedron test is written against.
    /// </summary>
    /// <remarks>
    /// The distance is r/√3, not r: the face x + y + z = r stands that far from the origin. Its vertices sit at
    /// (±r, 0, 0) and the tool reaches r across a vertex and r/√3 across a face, which is the ratio the exact
    /// kernel's octahedron has too.
    /// </remarks>
    public static ConvexTool Octahedron(double radiusMm, double cx = 0, double cy = 0, double cz = 0)
    {
        if (!(radiusMm > 0)) throw new ArgumentOutOfRangeException(nameof(radiusMm), radiusMm, "the radius must be positive.");
        const double k = 0.5773502691896258;   // 1 / sqrt(3), the distance of the octahedron's faces from its centre
        Span<HalfSpace> planes = stackalloc HalfSpace[8];
        int n = 0;
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    planes[n++] = new HalfSpace(sx, sy, sz, (radiusMm + sx * cx + sy * cy + sz * cz) * k);
        return FromPlanes(planes.ToArray());
    }

    /// <summary>
    /// An inscribed polyhedron of a ball, <paramref name="planeCount"/> half-spaces spread over the sphere with a
    /// Fibonacci spiral. It lies inside the ball, so it removes slightly less material than <see cref="BallStep"/>
    /// would, which is the direction a preview may err in without claiming a surface the exact kernel does not cut.
    /// </summary>
    /// <remarks>
    /// Inscribed is not free. Planes at distance <c>radiusMm</c> with unit normals would <em>circumscribe</em> the
    /// ball: every facet touches it, but the corners between the facets stand outside, and a grain that cuts further
    /// than the wheel is a wrong preview in the direction that matters. So the planes are built first at unit distance
    /// and shrunk by the radius of their own corners, which is the largest body with those normals that still fits
    /// inside the ball.
    /// </remarks>
    public static ConvexTool Ball(double radiusMm, int planeCount = MaxPlanes, double cx = 0, double cy = 0, double cz = 0)
    {
        if (!(radiusMm > 0)) throw new ArgumentOutOfRangeException(nameof(radiusMm), radiusMm, "the radius must be positive.");
        ArgumentOutOfRangeException.ThrowIfLessThan(planeCount, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(planeCount, MaxPlanes);
        var normals = new (double X, double Y, double Z)[planeCount];
        double golden = (1 + Math.Sqrt(5)) / 2;
        for (int i = 0; i < planeCount; i++)
        {
            double z = 1 - 2 * (i + 0.5) / planeCount;
            double r = Math.Sqrt(Math.Max(0, 1 - z * z));
            double angle = 2 * Math.PI * i / golden;
            normals[i] = (r * Math.Cos(angle), r * Math.Sin(angle), z);
        }

        Span<HalfSpace> unit = stackalloc HalfSpace[planeCount];
        for (int i = 0; i < planeCount; i++) unit[i] = new(normals[i].X, normals[i].Y, normals[i].Z, 1);
        double shrink = radiusMm / FromPlanes(unit.ToArray()).RadiusMm;

        var planes = new HalfSpace[planeCount];
        for (int i = 0; i < planeCount; i++)
        {
            var (nx, ny, nz) = normals[i];
            planes[i] = new HalfSpace(nx, ny, nz, shrink + cx * nx + cy * ny + cz * nz);
        }
        return FromPlanes(planes);
    }

    /// <summary>
    /// Whether the half-spaces bound a body, which they have to: the kernel reads the lowest and the highest z of a
    /// sweep, and an unbounded end comes back as an infinity that takes the whole column. A tool is unbounded exactly
    /// when some direction has every normal behind it, and such a direction is found by the pairs -- the cross product
    /// of two normals is the direction both of them are blind to, and the cone of those directions is generated by
    /// them, so if one of them leaves no normal behind it, nothing closes that way.
    /// </summary>
    /// <remarks>
    /// This is what a plane set like x and y boxed in but nothing below z = 0 needs: the corners it does find are a
    /// finite set, and a finite corner set on its own says nothing about the body around them. A set whose normals all
    /// point the same way is refused here, before the corners are worked out, because its corners are meaningless.
    /// </remarks>
    private static bool Bounded(HalfSpace[] p)
    {
        int m = p.Length;
        for (int i = 0; i < m; i++)
            for (int j = i + 1; j < m; j++)
            {
                var (ax, ay, az) = (p[i].Nx, p[i].Ny, p[i].Nz);
                var (bx, by, bz) = (p[j].Nx, p[j].Ny, p[j].Nz);
                double rx = ay * bz - az * by, ry = az * bx - ax * bz, rz = ax * by - ay * bx;
                double len = Math.Sqrt(rx * rx + ry * ry + rz * rz);
                if (len < MinCross) continue;   // near-parallel normals: the pair says nothing about a direction

                // Normalised, so that the tolerance below is the same for every pair whatever the angle between the
                // normals was. Reading the raw cross product would call a pair of 1e-8 radians apart unbounded.
                rx /= len; ry /= len; rz /= len;
                bool open = true;
                for (int q = 0; q < m && open; q++)
                    open = p[q].Nx * rx + p[q].Ny * ry + p[q].Nz * rz <= SlackDir;
                if (open) return false;
            }
        return true;
    }

    /// <summary>
    /// The corners: every point where three planes meet and every half-space holds. A triple with a vanishing
    /// determinant (parallel planes) contributes nothing, and one outside the tool is not a corner.
    /// </summary>
    /// <remarks>
    /// The check at the end is what makes the set trustworthy. A corner of a bounded polyhedron lies on at least
    /// three half-spaces, so if every half-space carries one, the triples found all the corners there are; a
    /// half-space that carries none is redundant, and a redundant one can also be the only thing keeping the body
    /// bounded in an unhelpful direction, so neither is accepted silently. <c>Sphere(4)</c> is the smallest input
    /// that passes.
    /// </remarks>
    private static (double X, double Y, double Z)[] Corners(HalfSpace[] p)
    {
        int m = p.Length;
        var corners = new List<(double X, double Y, double Z)>();
        var seen = new HashSet<(long X, long Y, long Z)>();
        var carries = new bool[m];

        for (int i = 0; i < m; i++)
            for (int j = i + 1; j < m; j++)
                for (int k = j + 1; k < m; k++)
                {
                    if (!Triple(p, i, j, k, out var c)) continue;
                    bool inside = true;
                    for (int q = 0; q < m; q++)
                    {
                        double v = p[q].Nx * c.X + p[q].Ny * c.Y + p[q].Nz * c.Z;
                        if (v > p[q].D + SlackMm) { inside = false; break; }
                    }
                    if (!inside) continue;

                    // Marked before the dedup, not after: a corner of a polytope usually lies on more than three
                    // planes, so the first triple to find it names only three of them and every other triple that
                    // lands on the same point would be dropped as a duplicate without ever crediting the rest.
                    carries[i] = carries[j] = carries[k] = true;
                    if (seen.Add(((long)Math.Round(c.X * 1e6), (long)Math.Round(c.Y * 1e6), (long)Math.Round(c.Z * 1e6))))
                        corners.Add(c);
                }

        if (corners.Count == 0)
            throw new ArgumentException("the half-spaces have no common corner, so they describe no body.");
        for (int q = 0; q < m; q++)
            if (!carries[q])
                throw new ArgumentException(
                    $"half-space {q} carries no corner, so the tool is redundant or unbounded there; the kernel " +
                    "cannot sweep it. Remove the half-space or split the tool.");
        return [.. corners];
    }

    /// <summary>Solves the three planes n·v = d, or reports a vanishing determinant.</summary>
    private static bool Triple(HalfSpace[] p, int i, int j, int k, out (double X, double Y, double Z) v)
    {
        double a = p[i].Nx, b = p[i].Ny, c = p[i].Nz, d = p[i].D;
        double e = p[j].Nx, f = p[j].Ny, g = p[j].Nz, h = p[j].D;
        double l = p[k].Nx, m = p[k].Ny, n = p[k].Nz, o = p[k].D;

        double det = a * (f * n - g * m) - b * (e * n - g * l) + c * (e * m - f * l);
        if (Math.Abs(det) < MinDet) { v = default; return false; }
        double inv = 1 / det;
        v = ((d * (f * n - g * m) - b * (h * n - g * o) + c * (h * m - f * o)) * inv,
             (a * (h * n - g * o) - d * (e * n - g * l) + c * (e * o - h * l)) * inv,
             (a * (f * o - h * m) - b * (e * o - h * l) + d * (e * m - f * l)) * inv);
        return true;
    }
}