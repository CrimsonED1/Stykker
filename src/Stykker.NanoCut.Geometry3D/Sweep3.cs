namespace Stykker.NanoCut.Geometry3D;

/// <summary>Exact sweeps of convex solids.</summary>
public static class Sweep3
{
    /// <summary>
    /// Volume swept by a convex solid with grid vertices translated along the integer vector <paramref name="t"/>:
    /// faces facing backwards stay, faces facing forwards move by t, faces parallel to t are swept in their plane,
    /// and every silhouette edge becomes a parallelogram. Exact (the Minkowski sum with the segment [0, t]) and
    /// linear in the number of faces.
    /// </summary>
    public static Solid Translate(Solid convex, Vec3 t)
    {
        if (t == default) return convex;
        var faces = convex.Faces;
        var sides = new int[faces.Count];
        var owner = new Dictionary<(Vec3, Vec3), int>();
        for (int i = 0; i < faces.Count; i++)
        {
            var f = faces[i];
            var s = f.Support;
            Int128 dot = s.Nx * t.X + s.Ny * t.Y + s.Nz * t.Z;
            sides[i] = Int128.Sign(dot);
            var v = f.Vertices;
            for (int k = 0; k < v.Length; k++)
            {
                if (!v[k].IsGrid) throw new ArgumentException("Translation sweep needs grid vertices.", nameof(convex));
                owner[(v[k].Grid, v[(k + 1) % v.Length].Grid)] = i;
            }
        }

        var result = new List<Face3>(faces.Count * 2);
        for (int i = 0; i < faces.Count; i++)
        {
            var pts = faces[i].Vertices.Select(v => v.Grid).ToArray();
            switch (sides[i])
            {
                case < 0:
                    result.Add(faces[i]);
                    break;
                case > 0:
                    result.Add(Face3.FromGrid(pts.Select(p => p + t).ToArray()));
                    break;
                default:
                    result.Add(Face3.FromGrid(PlanarHull(pts.Concat(pts.Select(p => p + t)).ToArray(), faces[i].Support)));
                    break;
            }
            // Silhouette edges: this face faces backwards, the neighbour forwards.
            if (sides[i] >= 0) continue;
            var v = pts;
            for (int k = 0; k < v.Length; k++)
            {
                Vec3 a = v[k], b = v[(k + 1) % v.Length];
                if (!owner.TryGetValue((b, a), out int j)) throw new ArgumentException("Solid is not closed.", nameof(convex));
                if (sides[j] > 0) result.Add(Face3.FromGrid([a + t, b + t, b, a]));
            }
        }
        return new Solid(result);
    }

    /// <summary>Convex hull of coplanar grid points, counter-clockwise around the plane normal.</summary>
    private static Vec3[] PlanarHull(Vec3[] pts, Plane3 plane)
    {
        // Project onto the coordinate plane where the normal has its largest component.
        Int128 ax = Int128.Abs(plane.Nx), ay = Int128.Abs(plane.Ny), az = Int128.Abs(plane.Nz);
        int drop = ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
        bool flip = (drop == 0 ? plane.Nx : drop == 1 ? plane.Ny : plane.Nz) < 0;
        Vec2 P(Vec3 p) => drop switch { 0 => new(p.Y, p.Z), 1 => new(p.Z, p.X), _ => new(p.X, p.Y) };
        var map = new Dictionary<Vec2, Vec3>();
        foreach (var p in pts) map[P(p)] = p;
        var hull = Geometry2D.ConvexHull.Compute(map.Keys);
        var result = hull.Select(h => map[h]).ToArray();
        if (flip) Array.Reverse(result);
        return result;
    }
}
