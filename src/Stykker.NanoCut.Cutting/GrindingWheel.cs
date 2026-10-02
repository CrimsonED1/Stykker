using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Cutting;

/// <summary>
/// A grinding wheel with discrete abrasive grains on its periphery, spinning about the tool z-axis. The bond (radius
/// <see cref="RadiusMm"/>, width <see cref="WidthMm"/>, centred on z = 0) does not cut; every grain is a small convex
/// solid whose outermost point protrudes <see cref="Grain.ProtrusionMm"/> beyond the bond. Each grain moves on its own
/// trochoid (feed + spin) and is simulated separately by <see cref="GrindingSimulation"/>.
/// </summary>
public sealed class GrindingWheel
{
    /// <summary>One abrasive grain.</summary>
    /// <param name="Index">Index in <see cref="Grains"/>.</param>
    /// <param name="AngleRad">Angular position on the wheel (from the tool x-axis about +z) at spindle angle 0.</param>
    /// <param name="AxialMm">Position along the wheel axis (tool z).</param>
    /// <param name="ProtrusionMm">Protrusion of the outermost point beyond the bond radius.</param>
    /// <param name="SizeMm">Grain size (tip-to-tip of the octahedron).</param>
    /// <param name="Shape">The grain as a convex solid in the tool frame (grid vertices).</param>
    /// <param name="Tip">The outermost (cutting) vertex in the tool frame.</param>
    public sealed record Grain(int Index, double AngleRad, double AxialMm, double ProtrusionMm, double SizeMm, Solid Shape, Vec3 Tip);

    /// <summary>Placement of one grain for <see cref="FromGrains"/>.</summary>
    /// <param name="AngleRad">Angular position on the wheel.</param>
    /// <param name="AxialMm">Position along the wheel axis.</param>
    /// <param name="ProtrusionMm">Protrusion beyond the bond.</param>
    /// <param name="SizeMm">Grain size (tip-to-tip of the octahedron).</param>
    /// <param name="Orientation">Rotation of the octahedron (null: a vertex points radially outwards).</param>
    public sealed record GrainSpec(double AngleRad, double AxialMm, double ProtrusionMm, double SizeMm, Pose3? Orientation = null);

    private GrindingWheel(double radiusMm, double widthMm, double rpm, int seed, IReadOnlyList<Grain> grains)
    {
        RadiusMm = radiusMm;
        WidthMm = widthMm;
        Rpm = rpm;
        Seed = seed;
        Grains = grains;
        Spindle = SpinningTool.Asymmetric(ToolShape.FromConvexParts([.. grains.Select(g => g.Shape)]), rpm, grains.Count);
    }

    /// <summary>Bond radius in mm.</summary>
    public double RadiusMm { get; }

    /// <summary>Wheel width along the axis in mm.</summary>
    public double WidthMm { get; }

    /// <summary>Spindle speed in rpm.</summary>
    public double Rpm { get; }

    /// <summary>Seed of the random grain distribution (0 for <see cref="FromGrains"/>).</summary>
    public int Seed { get; }

    /// <summary>The grains.</summary>
    public IReadOnlyList<Grain> Grains { get; }

    /// <summary>The grains as a spinning tool (time/angle conversion; the bond is not part of it).</summary>
    public SpinningTool Spindle { get; }

    /// <summary>The (non-cutting) bond as a cylinder, e.g. for display.</summary>
    public Solid Bond(Tolerance? tol = null) => Solid.Cylinder(Vec3.Mm(0, 0, -WidthMm / 2), Vec3.Mm(0, 0, WidthMm / 2), RadiusMm, tol);

    /// <summary>
    /// Wheel with <paramref name="grainCount"/> grains at random positions on the periphery (uniform angle and axial
    /// position), random orientation and a protrusion drawn from a normal distribution (mean, σ), clamped to
    /// [0, grain size]. The same seed gives the same wheel.
    /// </summary>
    public static GrindingWheel Random(double radiusMm, double widthMm, int grainCount, double grainSizeMm,
        double protrusionMeanMm, double protrusionSigmaMm, double rpm, int seed)
    {
        if (grainCount < 1) throw new ArgumentOutOfRangeException(nameof(grainCount));
        if (!(grainSizeMm > 0 && grainSizeMm < widthMm)) throw new ArgumentOutOfRangeException(nameof(grainSizeMm), "Grain size must be in (0, width).");
        if (!(protrusionSigmaMm >= 0)) throw new ArgumentOutOfRangeException(nameof(protrusionSigmaMm));
        var rng = new Random(seed);
        var specs = new GrainSpec[grainCount];
        for (int i = 0; i < grainCount; i++)
        {
            double angle = rng.NextDouble() * 2 * Math.PI;
            double axial = (rng.NextDouble() - 0.5) * (widthMm - grainSizeMm);
            // Box–Muller.
            double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble();
            double normal = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            double protrusion = Math.Clamp(protrusionMeanMm + protrusionSigmaMm * normal, 0, grainSizeMm);
            // Uniform random rotation (Shoemake).
            double a = rng.NextDouble(), b = rng.NextDouble() * 2 * Math.PI, c = rng.NextDouble() * 2 * Math.PI;
            double s1 = Math.Sqrt(1 - a), s2 = Math.Sqrt(a);
            var orientation = Pose3.FromQuaternion(s1 * Math.Sin(b), s1 * Math.Cos(b), s2 * Math.Sin(c), s2 * Math.Cos(c), 0, 0, 0);
            specs[i] = new GrainSpec(angle, axial, protrusion, grainSizeMm, orientation);
        }
        return Build(radiusMm, widthMm, rpm, seed, specs);
    }

    /// <summary>Wheel with explicitly placed grains (e.g. a single grain for a scratch test).</summary>
    public static GrindingWheel FromGrains(double radiusMm, double widthMm, double rpm, params GrainSpec[] grains)
    {
        if (grains.Length == 0) throw new ArgumentException("At least one grain required.", nameof(grains));
        return Build(radiusMm, widthMm, rpm, 0, grains);
    }

    private static GrindingWheel Build(double radiusMm, double widthMm, double rpm, int seed, IReadOnlyList<GrainSpec> specs)
    {
        if (!(radiusMm > 0) || !(widthMm > 0)) throw new ArgumentOutOfRangeException(nameof(radiusMm));
        var grains = new Grain[specs.Count];
        for (int i = 0; i < specs.Count; i++)
        {
            var g = specs[i];
            if (!(g.SizeMm > 0)) throw new ArgumentOutOfRangeException(nameof(specs), "Grain size must be positive.");
            var o = g.Orientation ?? Pose3.Identity;
            double h = g.SizeMm / 2 * Units.NmPerMm;
            // Octahedron in the local frame: x radial (outwards), z along the wheel axis.
            var local = new (double X, double Y, double Z)[] { (h, 0, 0), (-h, 0, 0), (0, h, 0), (0, -h, 0), (0, 0, h), (0, 0, -h) }
                .Select(v => o.Apply(v.X, v.Y, v.Z)).ToArray();
            // Radial position of the centre so that the outermost vertex lies exactly on bond radius + protrusion.
            double rTip = (radiusMm + g.ProtrusionMm) * Units.NmPerMm;
            int tipIndex = 0;
            double centre = double.MaxValue;
            for (int k = 0; k < local.Length; k++)
            {
                double c = Math.Sqrt(rTip * rTip - local[k].Y * local[k].Y) - local[k].X;
                if (c < centre) (centre, tipIndex) = (c, k);
            }
            double ca = Math.Cos(g.AngleRad), sa = Math.Sin(g.AngleRad), z = g.AxialMm * Units.NmPerMm;
            Vec3 Place((double X, double Y, double Z) v)
            {
                double r = centre + v.X;
                return Vec3.Nm((long)Math.Round(r * ca - v.Y * sa), (long)Math.Round(r * sa + v.Y * ca), (long)Math.Round(z + v.Z));
            }
            var pts = local.Select(Place).ToArray();
            var tip = pts[tipIndex];
            grains[i] = new Grain(i, g.AngleRad, g.AxialMm, g.ProtrusionMm, g.SizeMm, ConvexHull3.Compute(pts), tip);
        }
        return new GrindingWheel(radiusMm, widthMm, rpm, seed, grains);
    }
}
