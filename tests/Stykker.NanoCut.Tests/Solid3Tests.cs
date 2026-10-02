using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Tests;

public class Solid3Tests
{
    private static Solid Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
        Solid.Box(Vec3.Mm(x0, y0, z0), Vec3.Mm(x1, y1, z1));

    [Fact]
    public void OverlappingBoxes()
    {
        var a = Box(0, 0, 0, 10, 10, 10);
        var b = Box(5, 5, 5, 15, 15, 15);
        Assert.Equal(1000, a.VolumeMm3, 9);
        Assert.Equal(125, (a & b).VolumeMm3, 9);
        Assert.Equal(875, (a - b).VolumeMm3, 9);
        Assert.Equal(1875, (a | b).VolumeMm3, 9);
        Assert.Equal(875, (b - a).VolumeMm3, 9);
    }

    [Fact]
    public void SharedFacesAndCoplanarFaces()
    {
        var a = Box(0, 0, 0, 10, 10, 10);
        var touching = Box(10, 0, 0, 20, 10, 10);
        Assert.Equal(2000, (a | touching).VolumeMm3, 9);
        Assert.True((a & touching).IsEmpty);
        Assert.Equal(1000, (a - touching).VolumeMm3, 9);

        var lower = Box(0, 0, 0, 10, 10, 5); // shares five face planes with a
        Assert.Equal(500, (a - lower).VolumeMm3, 9);
        Assert.Equal(500, (a & lower).VolumeMm3, 9);
        Assert.Equal(1000, (a | lower).VolumeMm3, 9);
        Assert.True((a - a).IsEmpty);
        Assert.Equal(1000, (a | a).VolumeMm3, 9);
        Assert.Equal(1000, (a & a).VolumeMm3, 9);
    }

    [Fact]
    public void CavityAndChainedBooleansStayExact()
    {
        var outer = Box(0, 0, 0, 10, 10, 10);
        var hollow = outer - Box(2, 2, 2, 8, 8, 8);
        Assert.Equal(1000 - 216, hollow.VolumeMm3, 9);

        // Chain with tilted cuts: vertices become three-plane intersections, never rounded.
        var t1 = Solid.FromTriangles(
            [Vec3.Mm(-1, -1, 4), Vec3.Mm(11, -1, 7), Vec3.Mm(-1, 11, 9), Vec3.Mm(5, 5, 20)],
            [0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3]);
        var cut = outer - t1;
        var again = cut - Box(3, 3, -1, 7, 7, 11);
        Assert.Equal(cut.VolumeMm3 - (16 * 10 - (t1 & Box(3, 3, 0, 7, 7, 10)).VolumeMm3), again.VolumeMm3, 6);
        Assert.Contains(again.Vertices, v => !v.IsGrid);
        // Exact vertices lie exactly on the planes they were created from (here: x = 3 mm face).
        var plane = Plane3.FromPoints(Vec3.Mm(3, 0, 0), Vec3.Mm(3, 1, 0), Vec3.Mm(3, 0, 1));
        Assert.Contains(again.Vertices, v => !v.IsGrid && v.SideOf(plane) == 0);
    }

    [Fact]
    public void RandomTetrahedraSatisfyVolumeIdentities()
    {
        var rng = new Random(3);
        for (int iter = 0; iter < 60; iter++)
        {
            var a = RandomTetrahedron(rng);
            var b = rng.Next(2) == 0 ? RandomTetrahedron(rng) : RandomBox(rng);
            double va = a.VolumeMm3, vb = b.VolumeMm3;
            double i = (a & b).VolumeMm3, u = (a | b).VolumeMm3, d = (a - b).VolumeMm3, e = (b - a).VolumeMm3;
            double tol = 1e-9 * (va + vb);
            Assert.InRange(u + i - va - vb, -tol, tol);
            Assert.InRange(d + i - va, -tol, tol);
            Assert.InRange(e + i - vb, -tol, tol);
            Assert.True(i >= -tol && d >= -tol);
        }
    }

    [Fact]
    public void DegenerateLatticeConfigurationsStayClosed()
    {
        // Coordinates on a coarse 1 mm lattice: touching vertices, shared edges and coplanar faces are common.
        var rng = new Random(17);
        for (int iter = 0; iter < 3000; iter++)
        {
            var a = LatticeSolid(rng);
            var b = LatticeSolid(rng);
            double va = a.VolumeMm3, vb = b.VolumeMm3;
            var i = a & b;
            var u = a | b;
            var d = a - b;
            foreach (var r in new[] { i, u, d })
                Assert.True(ClosureError(r) < 1e-9, $"iteration {iter}: result is not closed ({ClosureError(r)})");
            Assert.InRange(u.VolumeMm3 + i.VolumeMm3 - va - vb, -1e-9, 1e-9);
            Assert.InRange(d.VolumeMm3 + i.VolumeMm3 - va, -1e-9, 1e-9);
        }
    }

    private static Solid LatticeSolid(Random rng)
    {
        Vec3 P() => Vec3.Mm(rng.Next(0, 5), rng.Next(0, 5), rng.Next(0, 5));
        if (rng.Next(3) == 0)
        {
            Vec3 p = P(), q = P();
            if (p.X == q.X || p.Y == q.Y || p.Z == q.Z) return LatticeSolid(rng);
            return Solid.Box(p, q);
        }
        Vec3 a = P(), b = P(), c = P(), d = P();
        int o = Predicates.Orient3D(a, b, c, d);
        if (o == 0) return LatticeSolid(rng);
        if (o > 0) (b, c) = (c, b);
        return Solid.FromTriangles([a, b, c, d], [0, 1, 2, 0, 3, 1, 1, 3, 2, 2, 3, 0]);
    }

    /// <summary>A closed surface encloses the same volume from any reference point.</summary>
    private static double ClosureError(Solid s)
    {
        double V(double ox, double oy, double oz)
        {
            double sum = 0;
            foreach (var f in s.Faces)
            {
                var v = f.Vertices;
                for (int k = 1; k + 1 < v.Length; k++)
                {
                    double ax = v[0].X - ox, ay = v[0].Y - oy, az = v[0].Z - oz;
                    double bx = v[k].X - ox, by = v[k].Y - oy, bz = v[k].Z - oz;
                    double cx = v[k + 1].X - ox, cy = v[k + 1].Y - oy, cz = v[k + 1].Z - oz;
                    sum += ax * (by * cz - bz * cy) + ay * (bz * cx - bx * cz) + az * (bx * cy - by * cx);
                }
            }
            return sum / 6 / 1e18;
        }
        return Math.Abs(V(0, 0, 0) - V(7e6, -3e6, 11e6));
    }

    [Fact]
    public void SteinmetzSolidWithinBudget()
    {
        // Two cylinders r = 3 mm with perpendicular axes: V = 16 r³ / 3; surface 16 r².
        var tol = Tolerance.Default;
        var c1 = Solid.Cylinder(Vec3.Mm(-5, 0, 0), Vec3.Mm(5, 0, 0), 3, tol);
        var c2 = Solid.Cylinder(Vec3.Mm(0, -5, 0), Vec3.Mm(0, 5, 0), 3, tol);
        double v = (c1 & c2).VolumeMm3;
        double exact = 16.0 * 27 / 3;
        Assert.InRange(v, exact - 16 * 9 * tol.TotalMm, exact);
    }

    [Fact]
    public void SphereLensWithinBudget()
    {
        // Spheres r = 3 mm, centres 3 mm apart: V = π (4r + d)(2r − d)² / 12; surface 2 · 2πr·h with h = r − d/2.
        var tol = Tolerance.Default;
        var lens = Solid.Sphere(Vec3.Mm(0, 0, 0), 3, tol) & Solid.Sphere(Vec3.Mm(3, 0, 0), 3, tol);
        double exact = Math.PI * (4 * 3 + 3) * 9 / 12;
        double surface = 2 * 2 * Math.PI * 3 * 1.5;
        Assert.InRange(lens.VolumeMm3, exact - surface * tol.TotalMm, exact);
    }

    [Fact]
    public void PrimitivesAreInscribed()
    {
        var tol = Tolerance.Default;
        double r = 3;
        var sphere = Solid.Sphere(Vec3.Mm(1, 2, 3), r, tol);
        double vs = 4.0 / 3 * Math.PI * r * r * r;
        Assert.InRange(sphere.VolumeMm3, vs - 4 * Math.PI * r * r * tol.ChordNm * 1e-6, vs);
        var cyl = Solid.Cylinder(Vec3.Mm(0, 0, 0), Vec3.Mm(0, 0, 10), r, tol);
        double vc = Math.PI * r * r * 10;
        Assert.InRange(cyl.VolumeMm3, vc - 2 * Math.PI * r * 10 * tol.ChordNm * 1e-6, vc);
        foreach (var v in sphere.Vertices)
        {
            double d = Math.Sqrt(Math.Pow(v.X - 1e6, 2) + Math.Pow(v.Y - 2e6, 2) + Math.Pow(v.Z - 3e6, 2));
            Assert.InRange(d, r * 1e6 - 1, r * 1e6 + 1);
        }
    }

    [Fact]
    public void MeshBuffersAreConsistent()
    {
        var box = Box(0, 0, 0, 2, 4, 6);
        var buf = box.ToMeshBuffers(OriginMode.Centroid);
        Assert.Equal(12, buf.TriangleCount);
        Assert.Equal((1.0, 2.0, 3.0), buf.OriginMm);
        Assert.Equal(buf.Positions.Length, buf.Normals.Length);
        Assert.All(buf.Positions, p => Assert.InRange(p, -3f, 3f));
        Assert.Equal(84 + 12 * 50, buf.ToStl().Length);
    }

    private static Solid RandomTetrahedron(Random rng)
    {
        Vec3 P() => new(rng.NextInt64(-5_000_000, 5_000_000), rng.NextInt64(-5_000_000, 5_000_000), rng.NextInt64(-5_000_000, 5_000_000));
        Vec3 a = P(), b = P(), c = P(), d = P();
        int o = Predicates.Orient3D(a, b, c, d);
        if (o == 0) return RandomTetrahedron(rng);
        // Outward: d must be below the counter-clockwise triangle (a, b, c).
        if (o > 0) (b, c) = (c, b);
        return Solid.FromTriangles([a, b, c, d], [0, 1, 2, 0, 3, 1, 1, 3, 2, 2, 3, 0]);
    }

    private static Solid RandomBox(Random rng)
    {
        long x = rng.NextInt64(-5_000_000, 3_000_000), y = rng.NextInt64(-5_000_000, 3_000_000), z = rng.NextInt64(-5_000_000, 3_000_000);
        return Solid.Box(new Vec3(x, y, z), new Vec3(x + rng.NextInt64(1, 4_000_000), y + rng.NextInt64(1, 4_000_000), z + rng.NextInt64(1, 4_000_000)));
    }
}

public class Solid3IoTests
{
    [Fact]
    public void ConeVolumeAndStlRoundTrip()
    {
        var tol = Tolerance.Default;
        var cone = Solid.Cone(Vec3.Mm(0, 0, 0), Vec3.Mm(0, 0, 9), 3, 0, tol);
        double exact = Math.PI * 9 * 9 / 3;
        Assert.InRange(cone.VolumeMm3, exact - 0.01, exact);
        var frustum = Solid.Cone(Vec3.Mm(0, 0, 0), Vec3.Mm(2, 1, 6), 3, 1, tol);
        double h = Math.Sqrt(4 + 1 + 36);
        double vf = Math.PI * h / 3 * (9 + 3 + 1);
        Assert.InRange(frustum.VolumeMm3, vf - 0.01, vf);

        // Binary STL round trip of a box (float32 is exact for these values).
        var box = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(2, 3, 4));
        var back = StlReader.Read(box.ToMeshBuffers(OriginMode.Absolute).ToStl());
        Assert.Equal(24, back.VolumeMm3, 9);
        Assert.Equal(12, back.FaceCount);

        var ascii = "solid t\n facet normal 0 0 0\n outer loop\n vertex 0 0 0\n vertex 1 0 0\n vertex 0 1 0\n endloop\n endfacet\n" +
                    " facet normal 0 0 0\n outer loop\n vertex 0 0 0\n vertex 0 0 1\n vertex 1 0 0\n endloop\n endfacet\n" +
                    " facet normal 0 0 0\n outer loop\n vertex 0 0 0\n vertex 0 1 0\n vertex 0 0 1\n endloop\n endfacet\n" +
                    " facet normal 0 0 0\n outer loop\n vertex 1 0 0\n vertex 0 0 1\n vertex 0 1 0\n endloop\n endfacet\nendsolid t\n";
        var tet = StlReader.Read(System.Text.Encoding.ASCII.GetBytes(ascii));
        Assert.Equal(1.0 / 6, Math.Abs(tet.VolumeMm3), 9);
    }

    [Fact]
    public void TranslationSweepAcrossSplitCoplanarFaces()
    {
        // A cylinder moved sideways: its caps (triangle fans, coplanar pieces) are parallel to the move and must be swept
        // as one face each. Minkowski sum with a segment: V = V(K) + |t| · area of K projected along t.
        var tol = Tolerance.Budget(totalUm: 2.1, chordNm: 1000, sweepNm: 500);
        var cyl = Solid.Cylinder(Vec3.Mm(0, -1, 0), Vec3.Mm(0, 1, 0), 10, tol);
        var b = cyl.BoundsMm!.Value;
        var swept = Sweep3.Translate(cyl, Vec3.Mm(42, 0, 0));
        Assert.Equal(cyl.VolumeMm3 + 42 * (b.MaxY - b.MinY) * (b.MaxZ - b.MinZ), swept.VolumeMm3, 6);
        Assert.Equal(1880, (Solid.Box(Vec3.Mm(0, -5, -10), Vec3.Mm(20, 5, 0)) - swept.Transform(Pose3.TranslationMm(-11, 0, 7))).VolumeMm3, 6);
    }
}
