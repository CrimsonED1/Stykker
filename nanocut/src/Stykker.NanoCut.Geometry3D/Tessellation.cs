namespace Stykker.NanoCut.Geometry3D;

/// <summary>Orthonormal frame (U = axis, V, W with V × W = U) in double precision.</summary>
internal readonly record struct Frame((double X, double Y, double Z) U, (double X, double Y, double Z) V, (double X, double Y, double Z) W)
{
    /// <summary>Frame around the axis (ax, ay, az); W is the projection of +z (or +x for vertical axes), so θ = 270° points "down".</summary>
    public static Frame Around(double ax, double ay, double az)
    {
        double len = Math.Sqrt(ax * ax + ay * ay + az * az);
        if (len == 0) throw new ArgumentException("Axis must not be zero.");
        var u = (ax / len, ay / len, az / len);
        var refv = Math.Abs(u.Item3) < 0.9 ? (0.0, 0.0, 1.0) : (1.0, 0.0, 0.0);
        double dot = refv.Item1 * u.Item1 + refv.Item2 * u.Item2 + refv.Item3 * u.Item3;
        var w = Normalize((refv.Item1 - dot * u.Item1, refv.Item2 - dot * u.Item2, refv.Item3 - dot * u.Item3));
        var v = Cross(w, u);
        return new Frame(u, v, w);
    }

    public Vec3 Offset(double radius, double theta, double phi)
    {
        double c = Math.Cos(phi), s = Math.Sin(phi), ct = Math.Cos(theta), st = Math.Sin(theta);
        double x = radius * (c * (ct * V.X + st * W.X) + s * U.X);
        double y = radius * (c * (ct * V.Y + st * W.Y) + s * U.Y);
        double z = radius * (c * (ct * V.Z + st * W.Z) + s * U.Z);
        return new Vec3(R(x), R(y), R(z));
    }

    private static long R(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    private static (double X, double Y, double Z) Normalize((double X, double Y, double Z) a)
    {
        double l = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
        return (a.X / l, a.Y / l, a.Z / l);
    }

    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}

/// <summary>Sphere and ring tessellations with controlled chord error.</summary>
internal static class Tessellation
{
    /// <summary>
    /// Latitude rings of a sphere (offsets from the centre, rounded to the grid), from the south pole
    /// (single point) to the north pole. The number of latitude steps is even, so ring m/2 is the equator;
    /// every ring has a multiple of 4 vertices starting at θ = 0. Triangles built by <see cref="Zip"/>
    /// between consecutive rings have sagitta ≤ <paramref name="chordNm"/> (verified, refined if needed).
    /// </summary>
    public static List<Vec3[]> SphereRings(double radiusNm, double chordNm, Frame frame)
    {
        double s = Math.Min(chordNm, radiusNm / 2);
        double edge = Math.Sqrt(2 * radiusNm * s - s * s); // circumradius limit for sagitta s
        for (int attempt = 0; attempt < 20; attempt++, edge *= 0.9)
        {
            int m = Math.Max(2, (int)Math.Ceiling(Math.PI * radiusNm / edge));
            if (m % 2 == 1) m++;
            var rings = new List<Vec3[]>(m + 1);
            for (int j = 0; j <= m; j++)
            {
                double phi = -Math.PI / 2 + Math.PI * j / m;
                if (j == 0 || j == m)
                {
                    rings.Add([frame.Offset(radiusNm, 0, phi)]);
                    continue;
                }
                double rho = radiusNm * Math.Cos(phi);
                int n = Math.Max(4, (int)Math.Ceiling(2 * Math.PI * rho / edge / 4) * 4);
                var ring = new Vec3[n];
                for (int i = 0; i < n; i++) ring[i] = frame.Offset(radiusNm, 2 * Math.PI * i / n, phi);
                rings.Add(ring);
            }
            if (MaxSagitta(rings, radiusNm) <= s) return rings;
        }
        throw new InvalidOperationException("Sphere tessellation did not reach the chord error.");
    }

    /// <summary>Ring of a circle (offsets) with sagitta ≤ chord, a multiple of 4 vertices, θ from 0.</summary>
    public static Vec3[] Ring(double radiusNm, double chordNm, Frame frame)
    {
        int n = (Discretization.SegmentCount(radiusNm, chordNm) + 3) / 4 * 4;
        var ring = new Vec3[n];
        for (int i = 0; i < n; i++) ring[i] = frame.Offset(radiusNm, 2 * Math.PI * i / n, 0);
        return ring;
    }

    /// <summary>Triangles between two closed rings (a ring of one vertex is a pole). Orientation is not fixed.</summary>
    public static IEnumerable<(Vec3, Vec3, Vec3)> Zip(Vec3[] a, Vec3[] b)
    {
        int n1 = a.Length, n2 = b.Length;
        int ta = n1 == 1 ? 0 : n1, tb = n2 == 1 ? 0 : n2; // triangles with an edge on ring a / ring b
        int i = 0, k = 0;
        while (i < ta || k < tb)
        {
            bool advanceA = k >= tb || (i < ta && (double)(i + 1) / n1 < (double)(k + 1) / n2);
            if (advanceA)
            {
                yield return (a[i], a[(i + 1) % n1], b[k % n2]);
                i++;
            }
            else
            {
                yield return (a[i % n1], b[(k + 1) % n2], b[k]);
                k++;
            }
        }
    }

    private static double MaxSagitta(List<Vec3[]> rings, double r)
    {
        double worst = 0;
        for (int j = 0; j + 1 < rings.Count; j++)
            foreach (var (a, b, c) in Zip(rings[j], rings[j + 1]))
            {
                // Distance of the triangle plane from the centre.
                double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
                double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (nl == 0) return double.PositiveInfinity;
                double d = Math.Abs(nx * a.X + ny * a.Y + nz * a.Z) / nl;
                worst = Math.Max(worst, r - d);
            }
        return worst;
    }
}
