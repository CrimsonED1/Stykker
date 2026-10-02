using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Cutting;

/// <summary>
/// Height sampling of a solid's top surface (largest z along a vertical line) and roughness of a sampled profile.
/// Heights are evaluated in double precision from the exact face planes (sub-nm), for measurement and display.
/// </summary>
public sealed class SurfaceProfile
{
    private readonly (double X0, double Y0, double X1, double Y1, double Nx, double Ny, double Nz, double D, double[] Xs, double[] Ys)[] _faces;

    /// <summary>Prepares height queries on <paramref name="solid"/> (only faces facing upwards can be the top).</summary>
    public SurfaceProfile(Solid solid) : this([solid])
    {
    }

    /// <summary>Prepares height queries on several solids together (e.g. the cells of a workpiece).</summary>
    public SurfaceProfile(IEnumerable<Solid> solids)
    {
        _faces = solids.SelectMany(s => s.Faces).Where(f => f.Support.Nz > 0).Select(f =>
        {
            var p = f.Support;
            var xs = f.Vertices.Select(v => v.X).ToArray();
            var ys = f.Vertices.Select(v => v.Y).ToArray();
            return (xs.Min() - 1, ys.Min() - 1, xs.Max() + 1, ys.Max() + 1, (double)p.Nx, (double)p.Ny, (double)p.Nz, (double)p.D, xs, ys);
        }).ToArray();
    }

    /// <summary>Height (mm) of the top surface above (x, y) in mm, or NaN if the vertical line misses the solid.</summary>
    public double TopZ(double xMm, double yMm)
    {
        double x = xMm * Units.NmPerMm, y = yMm * Units.NmPerMm, best = double.NaN;
        foreach (var f in _faces)
        {
            if (x < f.X0 || x > f.X1 || y < f.Y0 || y > f.Y1 || !Inside(f.Xs, f.Ys, x, y)) continue;
            double z = -(f.Nx * x + f.Ny * y + f.D) / f.Nz;
            if (double.IsNaN(best) || z > best) best = z;
        }
        return best / Units.NmPerMm;
    }

    /// <summary>Heights (mm) at <paramref name="samples"/> equally spaced points from (x0, y0) to (x1, y1) (mm).</summary>
    public double[] Line(double x0Mm, double y0Mm, double x1Mm, double y1Mm, int samples)
    {
        if (samples < 2) throw new ArgumentOutOfRangeException(nameof(samples));
        var z = new double[samples];
        for (int i = 0; i < samples; i++)
        {
            double f = (double)i / (samples - 1);
            z[i] = TopZ(x0Mm + (x1Mm - x0Mm) * f, y0Mm + (y1Mm - y0Mm) * f);
        }
        return z;
    }

    /// <summary>
    /// Arithmetic mean roughness Ra and peak-to-valley height Rz (one sampling length, mm) of a profile, measured from its
    /// mean line (NaN samples are ignored).
    /// </summary>
    public static (double Ra, double Rz) Roughness(IReadOnlyList<double> heights)
    {
        var h = heights.Where(v => !double.IsNaN(v)).ToArray();
        if (h.Length == 0) return (double.NaN, double.NaN);
        double mean = h.Average();
        return (h.Average(v => Math.Abs(v - mean)), h.Max() - h.Min());
    }

    // Convex polygon (counter-clockwise seen from above, since the face points upwards), boundary included.
    private static bool Inside(double[] xs, double[] ys, double x, double y)
    {
        int n = xs.Length;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            double cross = (xs[j] - xs[i]) * (y - ys[i]) - (ys[j] - ys[i]) * (x - xs[i]);
            double len = Math.Abs(xs[j] - xs[i]) + Math.Abs(ys[j] - ys[i]);
            if (cross < -1e-9 * len * len - 1e-6 * len) return false;
        }
        return true;
    }
}
