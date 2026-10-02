using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Cutting;

/// <summary>
/// An acting 3D shape for processes: a union of convex solids with grid vertices, in the tool's own frame.
/// Any shape can be used once it is split into convex parts; the factories below do this automatically.
/// </summary>
public sealed class ToolShape
{
    private ToolShape(IReadOnlyList<Solid> parts) => Parts = parts;

    /// <summary>The convex parts.</summary>
    public IReadOnlyList<Solid> Parts { get; }

    /// <summary>The union of all parts (for display and as a solid).</summary>
    public Solid ToSolid()
    {
        var s = Parts[0];
        for (int i = 1; i < Parts.Count; i++) s = s | Parts[i];
        return s;
    }

    /// <summary>Shape from convex solids (convexity is the caller's responsibility).</summary>
    public static ToolShape FromConvexParts(params Solid[] parts)
    {
        if (parts.Length == 0) throw new ArgumentException("At least one part required.", nameof(parts));
        return new(parts);
    }

    /// <summary>Ball of the given radius around the origin.</summary>
    public static ToolShape Ball(double radiusMm, Tolerance? tol = null) =>
        new([Solid.Sphere(default, radiusMm, tol)]);

    /// <summary>
    /// Rotationally symmetric tool about the z-axis (milling cutters, drills): outline points (r, z) in mm with
    /// increasing z, r ≥ 0. Each segment becomes a convex frustum, so any z-monotone outline is supported
    /// (flat, ball and bull-nose end mills, chamfer tools, drills).
    /// </summary>
    public static ToolShape Revolved(Tolerance? tol, params (double R, double Z)[] outline)
    {
        if (outline.Length < 2) throw new ArgumentException("At least two outline points required.", nameof(outline));
        for (int i = 0; i + 1 < outline.Length; i++)
            if (outline[i + 1].Z < outline[i].Z || outline[i].R < 0) throw new ArgumentException("Outline z must increase and r must be ≥ 0.", nameof(outline));

        // Profile polygon closed along the axis (r = 0), counter-clockwise in (r, z).
        var poly = new List<Vec2> { Vec2.Mm(0, outline[0].Z) };
        poly.AddRange(outline.Select(p => Vec2.Mm(p.R, p.Z)));
        poly.Add(Vec2.Mm(0, outline[^1].Z));
        var profile = Region2.FromContours([new Contour2(poly)]).Normalize();
        if (profile.Contours.Count == 1 && ConvexHull.IsConvex(profile.Contours[0]))
            return new([Solid.Revolve(profile, tol)]);

        // Non-convex outline: one convex frustum per segment.
        var parts = new List<Solid>();
        for (int i = 0; i + 1 < outline.Length; i++)
        {
            var (r0, z0) = outline[i];
            var (r1, z1) = outline[i + 1];
            if (z1 == z0 || (r0 <= 0 && r1 <= 0)) continue;
            parts.Add(Solid.Cone(Vec3.Mm(0, 0, z0), Vec3.Mm(0, 0, z1), r0, r1, tol));
        }
        return new(parts);
    }

    /// <summary>
    /// Ball-nose end mill: a ball of radius r at the tip (centre at z = r) and a cylindrical shank up to <paramref name="lengthMm"/>.
    /// The tool is convex, so it is a single part.
    /// </summary>
    public static ToolShape BallNoseMill(double radiusMm, double lengthMm, Tolerance? tol = null)
    {
        tol ??= Tolerance.Default;
        int n = Math.Max(2, (Discretization.SegmentCount(radiusMm * Units.NmPerMm, tol.ChordNm) + 3) / 4);
        var pts = new List<(double, double)>();
        for (int i = 0; i <= n; i++)
        {
            double a = -Math.PI / 2 + Math.PI / 2 * i / n;
            pts.Add((radiusMm * Math.Cos(a), radiusMm + radiusMm * Math.Sin(a)));
        }
        pts.Add((radiusMm, lengthMm));
        return Revolved(tol, [.. pts]);
    }

    /// <summary>Prism of any planar region (xy, mm) from z0 to z1, split into convex prisms (e.g. shaper cutters).</summary>
    public static ToolShape Extruded(Region2 region, double z0Mm, double z1Mm) =>
        new(region.ConvexParts().Select(p => Solid.Extrude(Region2.Polygon(p), z0Mm, z1Mm)).ToArray());
}
