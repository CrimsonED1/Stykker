using Stykker.NanoCut.Geometry2D;

namespace Stykker.NanoCut.Geometry3D;

/// <summary>
/// Planar sections of solids and the profiles taken from them: arbitrary planes, axial (r–z) and radial sections of
/// turned parts with a range, envelopes over several angles, and line profiles.
/// </summary>
/// <remarks>
/// Every face crossing the plane contributes one segment whose end points are exact intersections of three planes
/// (face plane, edge plane, section plane), rounded once to the nm grid of the plane's (u, v) frame. The segments are
/// chained into closed contours and normalised with the exact 2D kernel. A face lying in the section plane is treated
/// as if the plane were moved by an infinitesimal amount against its normal, so a section exactly at the top face of a
/// block returns the full cross-section and one exactly at its bottom face returns nothing.
/// </remarks>
public static class Section3
{
    // Segment ends of neighbouring faces are the same exact point computed from different planes; rounding to the
    // grid can differ by 1 nm, so chaining accepts ends this close.
    private const long JoinNm = 2;

    /// <summary>The section of <paramref name="solid"/> with <paramref name="plane"/> in (u, v) coordinates.</summary>
    public static Region2 Cut(Solid solid, SectionPlane plane)
    {
        var segments = new List<(Vec2 A, Vec2 B)>();
        var p = plane.Plane;
        double nlen = Math.Sqrt((double)p.Nx * (double)p.Nx + (double)p.Ny * (double)p.Ny + (double)p.Nz * (double)p.Nz);
        foreach (var f in solid.Faces)
        {
            if (Misses(f.Box, p, nlen)) continue;
            if (Segment(f, plane) is { } s) segments.Add(s);
        }
        return Region2.FromContours(Chain(segments), FillRule.NonZero).Normalize();
    }

    /// <summary>The section limited to the window u ∈ [u0, u1], v ∈ [v0, v1] (mm).</summary>
    public static Region2 Cut(Solid solid, SectionPlane plane, double u0Mm, double u1Mm, double v0Mm, double v1Mm) =>
        Window(Cut(solid, plane), u0Mm, u1Mm, v0Mm, v1Mm);

    /// <summary>
    /// Axial profile of a turned part: the half-section at <paramref name="angleRad"/> around the axis, limited to
    /// z ∈ [z0, z1] along the axis (measured from <paramref name="axisPoint"/>) and r ∈ [r0, r1] (mm; r1 = null: no
    /// limit). Coordinates: u = radius, v = axial position.
    /// </summary>
    public static Region2 Axial(Solid solid, Vec3 axisPoint, (double X, double Y, double Z) axis, double angleRad,
        double z0Mm, double z1Mm, double r0Mm = 0, double? r1Mm = null, (double X, double Y, double Z)? reference = null)
    {
        var full = Cut(solid, SectionPlane.Axial(axisPoint, axis, angleRad, reference));
        return Window(full, r0Mm, r1Mm ?? double.PositiveInfinity, z0Mm, z1Mm);
    }

    /// <summary>
    /// Axial profiles at <paramref name="angles"/> equally spaced angles over a full turn and their envelopes:
    /// <c>Outer</c> is the union (material at some angle – the largest radius per z), <c>Inner</c> the intersection
    /// (material at every angle). Their difference shows run-out and anything not rotationally symmetric.
    /// </summary>
    public static (Region2 Outer, Region2 Inner, IReadOnlyList<Region2> Profiles) AxialEnvelope(Solid solid, Vec3 axisPoint,
        (double X, double Y, double Z) axis, int angles, double z0Mm, double z1Mm, double r0Mm = 0, double? r1Mm = null,
        (double X, double Y, double Z)? reference = null)
    {
        if (angles < 1) throw new ArgumentOutOfRangeException(nameof(angles));
        var profiles = Enumerable.Range(0, angles)
            .Select(i => Axial(solid, axisPoint, axis, 2 * Math.PI * i / angles, z0Mm, z1Mm, r0Mm, r1Mm, reference))
            .ToList();
        var outer = Region2.UnionAll(profiles);
        var inner = profiles[0];
        for (int i = 1; i < profiles.Count; i++) inner &= profiles[i];
        return (outer, inner, profiles);
    }

    /// <summary>
    /// Radial section (normal to the axis) at <paramref name="atMm"/> along it; u, v are centred on the axis, so polar
    /// angles in the plane are angles around the axis.
    /// </summary>
    public static Region2 Radial(Solid solid, Vec3 axisPoint, (double X, double Y, double Z) axis, double atMm,
        (double X, double Y, double Z)? reference = null) =>
        Cut(solid, SectionPlane.Radial(axisPoint, axis, atMm, reference));

    /// <summary>
    /// Surface heights (mm, along <paramref name="up"/>) at <paramref name="samples"/> points equally spaced from
    /// <paramref name="a"/> to <paramref name="b"/>: the largest v of the section in the plane through a, b and up,
    /// relative to the line a–b. NaN where the line misses the solid.
    /// </summary>
    public static double[] LineProfile(Solid solid, Vec3 a, Vec3 b, (double X, double Y, double Z) up, int samples)
    {
        var plane = SectionPlane.Through(a, b, up);
        double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        double length = Math.Sqrt(dx * dx + dy * dy + dz * dz) / Units.NmPerMm;
        return Profile2.Heights(Cut(solid, plane), 0, length, samples);
    }

    /// <summary>Limits a region to u ∈ [u0, u1], v ∈ [v0, v1] (mm; infinite bounds allowed).</summary>
    public static Region2 Window(Region2 region, double u0Mm, double u1Mm, double v0Mm, double v1Mm)
    {
        if (region.Bounds is not { } b) return Region2.Empty;
        long Clamp(double mm, long lo, long hi) =>
            double.IsNegativeInfinity(mm) ? lo : double.IsPositiveInfinity(mm) ? hi : Math.Clamp(Units.MmToNm(mm), lo, hi);
        long x0 = Clamp(u0Mm, b.Min.X - 1, b.Max.X + 1), x1 = Clamp(u1Mm, b.Min.X - 1, b.Max.X + 1);
        long y0 = Clamp(v0Mm, b.Min.Y - 1, b.Max.Y + 1), y1 = Clamp(v1Mm, b.Min.Y - 1, b.Max.Y + 1);
        if (x0 >= x1 || y0 >= y1) return Region2.Empty;
        return region & Region2.Rectangle(new Vec2(x0, y0), new Vec2(x1, y1));
    }

    // Face box clearly on one side of the plane (double precision with a generous margin).
    private static bool Misses(in Box3 box, in Plane3 p, double nlen)
    {
        double nx = (double)p.Nx / nlen, ny = (double)p.Ny / nlen, nz = (double)p.Nz / nlen, d = (double)p.D / nlen;
        double c = nx * (box.MinX + box.MaxX) / 2 + ny * (box.MinY + box.MaxY) / 2 + nz * (box.MinZ + box.MaxZ) / 2 + d;
        double r = Math.Abs(nx) * (box.MaxX - box.MinX) / 2 + Math.Abs(ny) * (box.MaxY - box.MinY) / 2 + Math.Abs(nz) * (box.MaxZ - box.MinZ) / 2;
        return Math.Abs(c) > r + 16;
    }

    // The face's crossing with the plane, oriented so that the solid's interior lies to its left in (u, v).
    private static (Vec2 A, Vec2 B)? Segment(Face3 f, SectionPlane plane)
    {
        int n = f.Vertices.Length;
        Span<bool> below = n <= 64 ? stackalloc bool[n] : new bool[n];
        bool any = false, all = true;
        for (int i = 0; i < n; i++)
        {
            below[i] = f.Vertices[i].SideOf(plane.Plane) < 0; // on the plane counts as above (see remarks)
            any |= below[i];
            all &= below[i];
        }
        if (!any || all) return null;

        Point3? first = null, second = null;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            if (below[i] == below[j]) continue;
            var h = Plane3.Intersect(f.Support, f.Edges[i], plane.Plane)
                    ?? throw new InvalidOperationException("Face edge parallel to a crossing section plane.");
            if (first is null) first = new Point3(h);
            else second = new Point3(h);
        }
        if (first is not { } a || second is not { } b) return null;

        // Direction of the boundary with the interior on the left: n_plane × n_face.
        var s = f.Support;
        var t = SectionPlane.Cross(plane.Normal, ((double)s.Nx, (double)s.Ny, (double)s.Nz));
        bool flip = (b.X - a.X) * t.X + (b.Y - a.Y) * t.Y + (b.Z - a.Z) * t.Z < 0;
        var pa = Round(plane.ToPlane(a.X, a.Y, a.Z));
        var pb = Round(plane.ToPlane(b.X, b.Y, b.Z));
        if (pa == pb) return null;
        return flip ? (pb, pa) : (pa, pb);
    }

    private static Vec2 Round((double U, double V) p) => new((long)Math.Round(p.U), (long)Math.Round(p.V));

    private static List<Contour2> Chain(List<(Vec2 A, Vec2 B)> segments)
    {
        var starts = new Dictionary<Vec2, List<int>>();
        for (int i = 0; i < segments.Count; i++)
        {
            if (!starts.TryGetValue(segments[i].A, out var list)) starts[segments[i].A] = list = [];
            list.Add(i);
        }
        var used = new bool[segments.Count];
        var contours = new List<Contour2>();
        for (int i = 0; i < segments.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var start = segments[i].A;
            var loop = new List<Vec2> { start };
            var end = segments[i].B;
            while (!Near(end, start))
            {
                int next = Next(end);
                if (next < 0) break; // open chain (should not happen for closed solids): closed by the straight edge
                loop.Add(end);
                used[next] = true;
                end = segments[next].B;
            }
            if (loop.Count >= 3) contours.Add(new Contour2(loop));
        }
        return contours;

        int Next(Vec2 p)
        {
            if (starts.TryGetValue(p, out var exact))
                foreach (int k in exact) if (!used[k]) return k;
            for (long dx = -JoinNm; dx <= JoinNm; dx++)
                for (long dy = -JoinNm; dy <= JoinNm; dy++)
                    if (starts.TryGetValue(new Vec2(p.X + dx, p.Y + dy), out var near))
                        foreach (int k in near) if (!used[k]) return k;
            return -1;
        }
    }

    private static bool Near(Vec2 a, Vec2 b) => Math.Abs(a.X - b.X) <= JoinNm && Math.Abs(a.Y - b.Y) <= JoinNm;
}
