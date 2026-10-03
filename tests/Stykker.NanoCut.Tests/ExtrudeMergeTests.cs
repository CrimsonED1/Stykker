using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Tests;

/// <summary>
/// Solid.Extrude merges the coplanar triangles of its caps into polygon faces. A cap with a hole must not become an outer
/// face lying over the hole plus a reversed face for the hole: that pair has the right volume, so a volume check does not
/// see it, but every mesh shows the hole closed and later Booleans cut through the phantom sheet.
/// </summary>
public class ExtrudeMergeTests
{
    private static Solid Washer() =>
        Solid.Extrude(Region2.Rectangle(Vec2.Mm(0, 0), Vec2.Mm(10, 10)) - Region2.Rectangle(Vec2.Mm(4, 4), Vec2.Mm(6, 6)), 0, 5);

    [Fact]
    public void ACapWithAHoleDoesNotCoverTheHole()
    {
        var s = Washer();
        Assert.Equal(480, s.VolumeMm3, 9);

        // Area of the mesh at the top, by the way its triangles face: the cap is 100 − 4 mm², and nothing faces down.
        var m = s.ToMeshBuffers(OriginMode.Absolute);
        double up = 0, down = 0;
        for (int t = 0; t < m.Indices.Length; t += 3)
        {
            var p = new double[3][];
            for (int k = 0; k < 3; k++)
            {
                uint i = m.Indices[t + k];
                p[k] = [m.Positions[3 * i] + m.OriginMm.X, m.Positions[3 * i + 1] + m.OriginMm.Y, m.Positions[3 * i + 2] + m.OriginMm.Z];
            }
            if (p.Any(v => Math.Abs(v[2] - 5) > 1e-6)) continue;
            double area = 0.5 * ((p[1][0] - p[0][0]) * (p[2][1] - p[0][1]) - (p[2][0] - p[0][0]) * (p[1][1] - p[0][1]));
            if (area > 0) up += area; else down -= area;
        }
        Assert.Equal(96, up, 6);
        Assert.Equal(0, down, 9);

        // No face of the top cap reaches into the hole.
        foreach (var f in s.Faces.Where(f => f.Vertices.All(v => v.IsGrid && v.Grid.Z == 5_000_000)))
        {
            double cx = f.Vertices.Average(v => (double)v.Grid.X) / 1e6, cy = f.Vertices.Average(v => (double)v.Grid.Y) / 1e6;
            Assert.False(cx > 4 && cx < 6 && cy > 4 && cy < 6, $"a top face is centred in the hole at ({cx}, {cy})");
        }
    }

    [Fact]
    public void ABoxInsideTheHoleLeavesTheWasherAsItWas()
    {
        var s = Washer();
        var r = s - Solid.Box(Vec3.Mm(4.5, 4.5, -1), Vec3.Mm(5.5, 5.5, 6));
        Assert.Equal(480, r.VolumeMm3, 9);
        Assert.True(r.FaceCount <= s.FaceCount, $"{s.FaceCount} faces became {r.FaceCount}: the box cut something in the hole");
    }

    [Fact]
    public void CapsWithoutHolesStillMerge()
    {
        var s = Solid.Extrude(Region2.Rectangle(Vec2.Mm(0, 0), Vec2.Mm(10, 10)), 0, 5);
        Assert.Equal(6, s.FaceCount);
        Assert.Equal(500, s.VolumeMm3, 9);
    }

    /// <summary>
    /// A concave polygon whose first twelve vertices lie in its kernel (on a bulge of the bottom edge, below a notch at
    /// the far right): they are on one side of every edge, so Face3.FromGrid's bounded check lets it through. The merge
    /// must not rely on that check; IsConvexLoop looks at every corner.
    /// </summary>
    [Fact]
    public void TheMergeCatchesAConcaveLoopThatFromGridLetsThrough()
    {
        var loop = new List<Vec3>();
        for (int k = 0; k < 13; k++)
        {
            double t = k / 12.0;
            loop.Add(Vec3.Mm(10 * t, -0.5 * Math.Sin(Math.PI * t), 0));
        }
        loop.AddRange([Vec3.Mm(40, 0, 0), Vec3.Mm(40, 10, 0), Vec3.Mm(39, 10, 0), Vec3.Mm(39, 5, 0), Vec3.Mm(38, 5, 0),
            Vec3.Mm(38, 10, 0), Vec3.Mm(0, 10, 0)]);
        var up = new Plane3(0, 0, 1, 0);

        Assert.False(ConvexHull3.IsConvexLoop(loop, up));
        _ = Face3.FromGrid([.. loop]);   // the documented gap of the bounded check: no exception

        // A bulge along the whole bottom edge and no notch is convex, and reversed it faces the other way.
        var convex = Enumerable.Range(0, 13).Select(k => Vec3.Mm(40 * k / 12.0, -0.5 * Math.Sin(Math.PI * k / 12.0), 0))
            .Append(Vec3.Mm(40, 10, 0)).Append(Vec3.Mm(0, 10, 0)).ToList();
        Assert.True(ConvexHull3.IsConvexLoop(convex, up));
        Assert.False(ConvexHull3.IsConvexLoop([.. Enumerable.Reverse(convex)], up));

        // A loop that touches itself at a vertex is not one convex polygon.
        var pinched = new List<Vec3> { Vec3.Mm(0, 0, 0), Vec3.Mm(2, 0, 0), Vec3.Mm(1, 1, 0), Vec3.Mm(2, 2, 0), Vec3.Mm(0, 2, 0), Vec3.Mm(1, 1, 0) };
        Assert.False(ConvexHull3.IsConvexLoop(pinched, up));
    }

    /// <summary>The side of an edge plane is still searched past the bounded convexity check.</summary>
    [Fact]
    public void AFaceWhoseFirstVerticesAreCollinearIsNotDegenerate()
    {
        // Fourteen vertices on the bottom edge, then the two top corners.
        var pts = Enumerable.Range(0, 14).Select(i => Vec3.Mm(i, 0, 0)).Append(Vec3.Mm(13, 5, 0)).Append(Vec3.Mm(0, 5, 0)).ToArray();
        var f = Face3.FromGrid(pts);
        Assert.Equal(16, f.Vertices.Length);
    }
}

