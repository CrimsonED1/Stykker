using System.Globalization;
using Stykker.NanoCut.Cutting;
using Stykker.NanoCut.Demo.Scenes;
using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Geometry3D;
using Stykker.NanoCut.Testing;

namespace Stykker.NanoCut.Demo.Pages;

/// <summary>
/// One inspection case: a body, the quantity every vertex is coloured by, and the checks that quantity is measured
/// with. The colour is the measurement — the shape only tells you where to look.
/// </summary>
public sealed class Inspection
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>The measured body, drawn with the deviation colour.</summary>
    public required Solid Body { get; init; }

    /// <summary>The nominal form, drawn as a plain outline next to the body.</summary>
    public Solid? Nominal { get; init; }

    /// <summary>What the colour of a vertex means, in nanometres.</summary>
    public required Func<Vec3, double> ValueNm { get; init; }

    public required string ValueLabel { get; init; }

    /// <summary>
    /// The nominal form as line segments in mm (x, y, z pairs), drawn next to the body. The gap between this curve
    /// and the surface <em>is</em> what the colour measures, so it is worth seeing rather than only believing. Null
    /// where the nominal is a plane rather than a curve.
    /// </summary>
    public float[]? NominalLines { get; set; }

    /// <summary>Framing choices: a label and the box [minX, minY, minZ, maxX, maxY, maxZ] in mm.</summary>
    public IReadOnlyList<(string Label, double[] Box)> Zoom { get; set; } = [];

    public List<(string Label, string Value)> Metrics { get; } = [];
    public List<ReferenceCheck> Checks { get; } = [];

    public Inspection Metric(string label, string value)
    {
        Metrics.Add((label, value));
        return this;
    }

    /// <summary>
    /// The exact nanometre coordinate of every vertex of the mesh buffers, in the order MeshBuffers.From writes
    /// them — <c>Solid.Vertices</c> walks face by face and repeats each face's vertices, which is the same order the
    /// buffers use. The viewer picks an index into those buffers, and this is how that index comes back to the
    /// integer grid instead of the float32 the browser holds. A grid vertex is exact to the nanometre; a computed
    /// one is a three-plane intersection that was never rounded.
    /// </summary>
    public (long[] Nm, bool[] Grid) ExactVertices()
    {
        var all = Body.Vertices.ToArray();
        var nm = new long[all.Length * 3];
        var grid = new bool[all.Length];
        for (int i = 0; i < all.Length; i++)
        {
            var v = all[i];
            nm[3 * i] = v.IsGrid ? v.Grid.X : (long)Math.Round(v.X);
            nm[3 * i + 1] = v.IsGrid ? v.Grid.Y : (long)Math.Round(v.Y);
            nm[3 * i + 2] = v.IsGrid ? v.Grid.Z : (long)Math.Round(v.Z);
            grid[i] = v.IsGrid;
        }
        return (nm, grid);
    }
}

/// <summary>The three inspection cases, in the order the page offers them.</summary>
public static class InspectCases
{
    public static readonly string[] Ids = ["staircase", "facets", "flank"];

    public static string NameOf(string id) => id switch
    {
        "facets" => "Chord error: 1 nm up to the coarse end",
        "flank" => "Gear flank against the involute",
        _ => "1 nm staircase",
    };

    /// <summary>The parameters of a case, in panel order. They live here, not on the page, so a test builds a case without repeating them.</summary>
    public static IReadOnlyList<Param> ParamsFor(string id) => id switch
    {
        "facets" =>
        [
            new("radius", "Rim radius", 5, 1, 200, 0.5, "µm"),
            new("chord", "Finest chord error", 1, 0.2, 100, 0.1, "nm"),
            new("facets", "Facets around the rim", 64, 12, 64, 4),
            new("z", "Thickness", 0.2, 0.05, 20, 0.05, "µm"),
        ],
        "flank" =>
        [
            new("m", "Module", 2, 0.5, 5, 0.25, "mm"),
            new("z", "Teeth (≥ 18)", 20, 18, 40, 1),
            new("sweep", "Sweep step", 3000, 100, 20000, 100, "nm"),
            new("width", "Face width", 10, 1, 40, 1, "mm"),
        ],
        _ =>
        [
            new("smallest", "Smallest step", 1, 1, 1000, 1, "nm"),
            new("step", "Step width", 20, 2, 200, 2, "µm"),
            new("top", "Bar height", 100, 10, 2000, 10, "µm"),
            new("depth", "Extrusion depth", 20, 2, 200, 2, "µm"),
        ],
    };

    public static Dictionary<string, double> Defaults(string id) =>
        ParamsFor(id).ToDictionary(p => p.Key, p => p.Default);

    /// <summary>Builds a case. Parameters that are not given keep their default.</summary>
    public static Inspection Build(string id, IReadOnlyDictionary<string, double>? values = null)
    {
        var p = Defaults(id);
        if (values is not null)
            foreach (var (key, value) in values) p[key] = value;
        return id switch
        {
            "facets" => Facets(p),
            "flank" => Flank(p),
            _ => Staircase(p),
        };
    }

    /// <summary>
    /// A staircase of steps cut into a flat bar. Every edge is axis parallel, so the 2D Boolean never computes an
    /// intersection point and never rounds a coordinate: the step heights are the integers they were asked for. The
    /// colour is the step depth, so the ladder reads as a gradient and the table says whether it is exact.
    /// </summary>
    private static Inspection Staircase(IReadOnlyDictionary<string, double> p)
    {
        double smallest = p["smallest"];
        double stepMm = p["step"] * 1e-3, topMm = p["top"] * 1e-3, depthMm = p["depth"] * 1e-3;
        double[] depthsNm = new double[] { 1, 2, 5, 10, 20, 50, 100 }.Select(d => d * smallest).ToArray();

        var bar = Region2.Rectangle(Vec2.Mm(0, 0), Vec2.Mm(stepMm * depthsNm.Length, topMm));
        for (int i = 0; i < depthsNm.Length; i++)
            bar -= Region2.Rectangle(Vec2.Mm(stepMm * i, topMm - depthsNm[i] * 1e-6), Vec2.Mm(stepMm * (i + 1), topMm + 1e-3));

        var part = Solid.Extrude(bar, 0, depthMm);
        long topNm = Units.MmToNm(topMm);

        // Only a tread is part of the measurement. The underside of the bar is 100 µm below the top face, so letting
        // it into the value would push the colour scale to 100 000 nm and flatten the whole ladder into its first
        // thousandth. An inspection colours the region of interest and leaves the rest neutral.
        var levels = depthsNm.Select(d => topNm - (long)Math.Round(d)).ToHashSet();

        var result = new Inspection
        {
            Id = "staircase",
            Name = "1 nm staircase",
            Description =
                "Seven steps of 1, 2, 5, 10, 20, 50 and 100 nm cut into a bar, each 20 µm wide. Every edge is axis " +
                "parallel, so no intersection point is ever computed and no coordinate is ever rounded — what the " +
                "table measures is the grid itself, not a curve. The colour is the depth of the tread a vertex sits " +
                "on; the underside of the bar stays neutral.",
            Body = part,
            ValueLabel = "tread depth below the top face",
            ValueNm = v => levels.Contains(v.Y) ? topNm - v.Y : 0,
        };

        // A tread has no vertex of its own: it is the horizontal contour edge between two risers. Find the edge that spans
        // the middle of the step and take the topmost one, read straight out of the produced contour.
        var contour = bar.Normalize().Contours;
        double worst = 0;
        for (int i = 0; i < depthsNm.Length; i++)
        {
            long mid = Units.MmToNm(stepMm * (i + 0.5));
            long y = long.MinValue;
            foreach (var c in contour)
                for (int k = 0; k < c.Count; k++)
                {
                    Vec2 a = c[k], b = c[(k + 1) % c.Count];
                    if (a.Y != b.Y) continue;
                    if (Math.Min(a.X, b.X) > mid || Math.Max(a.X, b.X) <= mid) continue;
                    y = Math.Max(y, a.Y);
                }
            double measured = topNm - y, d = Math.Abs(measured - depthsNm[i]);
            worst = Math.Max(worst, d);
            result.Metric($"step {i + 1} ({depthsNm[i]:0.###} nm)", $"{measured:0.000000} nm");
        }
        result.Checks.Add(new("step height against its target (worst)", worst, 0, 1e-9, "nm"));
        result.Metric("steps", depthsNm.Length.ToString(CultureInfo.InvariantCulture));
        result.Metric("bar", $"{stepMm * depthsNm.Length * 1000:0} × {topMm * 1000:0} × {depthMm * 1000:0} µm");
        result.Metric("vertices on the grid", part.Vertices.Count(v => v.IsGrid).ToString(CultureInfo.InvariantCulture));
        result.Zoom =
        [
            ("whole bar", [-0.02, -0.02, -depthMm, stepMm * depthsNm.Length + 0.02, topMm + 0.02, depthMm + 0.02]),
            ("first 40 nm", [-0.002, topMm - 4e-5, -depthMm, 4e-5, topMm + 2e-5, depthMm]),
        ];
        return result;
    }

    /// <summary>
    /// A rim whose facets widen by a constant factor from the finest to the coarsest, going once round. The finest
    /// facet is the one the case is about: a 1 nm chord error, which on a 5 µm rim is a 0,2 µm long facet — as fine as
    /// a 1 nm grid can ask a circular surface to be, and invisible as a shape at any zoom you would want to look at.
    /// The coarsest is wide enough to see. Both are correct, and both are inside the budget.
    /// <para>
    /// Verified for 32 and 64 facets; above that the widest facet stops matching its own sagitta, so the parameter is
    /// capped at 64 until that is understood. See WideRimsDoNotYetHoldTheirChordError in the tests.
    /// <para>
    /// The colour cannot be the radial distance of a vertex: the vertices of an inscribed polygon lie <em>on</em> the
    /// circle, up to half a nanometre of lattice rounding, so that would paint noise. The sagitta lives on the chord,
    /// between two vertices, and is measured here as the distance from the centre to the chord of each facet.
    /// </para>
    /// </summary>
    private static Inspection Facets(IReadOnlyDictionary<string, double> p)
    {
        double rUm = p["radius"], sMinNm = p["chord"], zUm = p["z"];
        int n = Math.Max(8, (int)Math.Round(p["facets"]));
        double rNm = rUm * 1000;
        if (sMinNm <= 0 || sMinNm >= rNm) throw new ArgumentOutOfRangeException(nameof(p), "chord");

        // The facets must close the turn exactly: Σ δᵢ = 2π with δᵢ = δ₀·gⁱ. Solve for g by bisection; it is the one
        // number that decides how coarse the rim gets at the far end from the finest chord error and the facet count.
        double dMin = 2 * Math.Acos(1 - sMinNm / rNm), target = 2 * Math.PI / dMin;
        double glo = 1 + 1e-9, ghi = 8;
        for (int it = 0; it < 200; it++)
        {
            double g = (glo + ghi) / 2, sum = (Math.Pow(g, n) - 1) / (g - 1);
            if (sum < target) glo = g; else ghi = g;
        }
        double growth = (glo + ghi) / 2;

        var pts = new List<Vec2>();
        var widths = new List<(double DeltaRad, double SagittaNm, double FacetNm)>();
        double phi = 0;
        for (int i = 0; i < n; i++)
        {
            double dth = dMin * Math.Pow(growth, i);
            widths.Add((dth, rNm * (1 - Math.Cos(dth / 2)), 2 * rNm * Math.Sin(dth / 2)));
            // The vertex goes at the start of the facet, not at its middle: then the chord from vertex i to vertex i+1
            // has exactly the angular width δᵢ and therefore exactly the sagitta the table claims.
            double a = phi;
            pts.Add(Vec2.Nm((long)Math.Round(rNm * Math.Cos(a)), (long)Math.Round(rNm * Math.Sin(a))));
            phi += dth;
        }

        // A vertex is the meeting point of two facets and carries the sagitta of the one that follows it, so the colour
        // runs from the finest facet to the coarsest as you go round.
        var sagittaByVertex = new Dictionary<(long, long), double>();
        for (int i = 0; i < n; i++)
            sagittaByVertex.TryAdd((pts[i].X, pts[i].Y), widths[i].SagittaNm);

        var disc2 = Region2.Polygon([.. pts]);
        var disc = Solid.Extrude(disc2, 0, zUm * 1e-3);

        var result = new Inspection
        {
            Id = "facets",
            Name = $"Chord error {widths[0].SagittaNm:0.###} nm … {widths[^1].SagittaNm:0} nm",
            Description =
                $"A {rUm:0.###} µm disc whose rim is an inscribed polygon of {n} facets that widen by a factor of " +
                $"{growth:0.###} from one to the next, starting at a chord error of {widths[0].SagittaNm:0.###} nm and " +
                $"ending at {widths[^1].SagittaNm:0.0} nm. The colour of a facet is how far its chord sits from the arc it " +
                "replaces, measured on the geometry the kernel produced. At the fine end the rim is indistinguishable from " +
                "a circle at any useful zoom; at the coarse end the facets are long enough to see. Both are inside the " +
                "budget, and the picture is how far apart the two ends of that budget look.",
            Body = disc,
            ValueLabel = "chord error of the facet (sagitta)",
            ValueNm = v => sagittaByVertex.GetValueOrDefault((v.X, v.Y), 0),
        };

        // Measured, not assumed: the distance from the centre to the chord of every facet, against the sagitta that
        // facet was built for. The only slack is the lattice — two endpoints each rounded by half a nanometre.
        double slack = Math.Sqrt(2) * Tolerance.NumericNm, worst = 0;
        for (int i = 0; i < n; i++)
        {
            Vec2 a = pts[i], b = pts[(i + 1) % n];
            double measured = rNm - PointSegmentDistance(0, 0, a.X, a.Y, b.X, b.Y);
            worst = Math.Max(worst, Math.Abs(measured - widths[i].SagittaNm));
            if (i % Math.Max(1, n / 8) == 0)
                result.Metric($"facet {i + 1} ({widths[i].SagittaNm:0.##} nm)", $"{measured:0.000} nm gemessen, {widths[i].FacetNm / 1000:0.00} µm lang");
        }
        result.Checks.Add(new("chord error against its target (worst)", worst, 0, slack, "nm"));
        // On the top face, not inside the solid: the arc and the rim it replaces have to be visible together, and
        // the gap between them is the sagitta the colour is reporting.
        result.NominalLines = CircleLines(rNm, zUm * 1e-3, 720);
        result.Metric("facets", n.ToString(CultureInfo.InvariantCulture));
        result.Metric("finest / coarsest chord", $"{widths[0].SagittaNm:0.###} / {widths[^1].SagittaNm:0.0} nm");
        result.Metric("finest / coarsest facet", $"{widths[0].FacetNm:0} / {widths[^1].FacetNm / 1000:0.00} µm");
        result.Metric("widening per facet", $"× {growth:0.###}");
        double circleArea = Math.PI * rNm * rNm * 1e-12;
        result.Metric("area", $"{disc2.AreaMm2 * 1e6:0.000} µm² (circle {circleArea * 1e6:0.000} µm²)");
        // The area an inscribed polygon leaves over: every facet cuts away a circular segment of angle δ, and that
        // segment is r²/2·(δ − sin δ). The slack on top is the lattice — moving the rim by at most √2/2 nm along its
        // whole length is the most area the rounding can add or take.
        double deficit = 0.5 * rNm * rNm * widths.Sum(w => w.DeltaRad - Math.Sin(w.DeltaRad)) * 1e-12;
        double lattice = 2 * Math.PI * rNm * Math.Sqrt(2) * Tolerance.NumericNm * 1e-12;
        result.Checks.Add(new("area against the circle", circleArea - disc2.AreaMm2, 0, deficit + lattice, "mm²"));
        double half = rUm * 1e-3 * 1.5, z = zUm * 1e-3;
        result.Zoom = [("whole disc", [-half, -half, -z, half, half, z + z])];
        return result;
    }

    /// <summary>A circle as a closed polyline: radius in nanometres, height in mm, emitted as line segments in mm.</summary>
    private static float[] CircleLines(double rNm, double zMm, int segments)
    {
        var line = new List<float>();
        for (int i = 0; i < segments; i++)
        {
            double a0 = 2 * Math.PI * i / segments, a1 = 2 * Math.PI * (i + 1) / segments;
            line.AddRange([(float)(rNm * Math.Cos(a0) * 1e-6), (float)(rNm * Math.Sin(a0) * 1e-6), (float)zMm,
                           (float)(rNm * Math.Cos(a1) * 1e-6), (float)(rNm * Math.Sin(a1) * 1e-6), (float)zMm]);
        }
        return [.. line];
    }

    /// <summary>
    /// The ideal involute flank of every tooth, built exactly as the deviation formula expects to find it: at the
    /// roll angle α the radius is rb/cos α and the angle away from the tooth centre is ψb − Inv(α). Drawn on the top
    /// face, so the curve and the profile it should have are visible together.
    /// </summary>
    private static float[] InvoluteLines(int z, double rbNm, double raNm, double psiB, double widthMm, int steps = 60)
    {
        double mid = widthMm, arMax = Math.Acos(Math.Clamp(rbNm / raNm, -1, 1));
        var line = new List<float>();
        foreach (int side in new[] { 1, -1 })
            for (int k = 0; k < z; k++)
            {
                double centre = 2 * Math.PI * k / z;
                float px = 0, py = 0;
                for (int i = 0; i <= steps; i++)
                {
                    double ar = arMax * i / steps;
                    double r = rbNm / Math.Cos(ar), a = centre + side * (psiB - GearProfile.Inv(ar));
                    float x = (float)(r * Math.Cos(a) * 1e-6), y = (float)(r * Math.Sin(a) * 1e-6);
                    if (i > 0) line.AddRange([px, py, (float)mid, x, y, (float)mid]);
                    px = x;
                    py = y;
                }
            }
        return [.. line];
    }

    /// <summary>Distance from the point to the segment, both given in nanometres.</summary>
    private static double PointSegmentDistance(double px, double py, long ax, long ay, long bx, long by)
    {
        double dx = bx - ax, dy = by - ay;
        double len = dx * dx + dy * dy;
        double t = len == 0 ? 0 : ((px - ax) * dx + (py - ay) * dy) / len;
        t = Math.Clamp(t, 0, 1);
        return Math.Sqrt((px - (ax + t * dx)) * (px - (ax + t * dx)) + (py - (ay + t * dy)) * (py - (ay + t * dy)));
    }

    /// <summary>
    /// A gear rolled by a rack, coloured by the deviation of every flank vertex from the ideal involute. This is the
    /// one case whose quantity is signed and nanometre-small on a millimetre part: the deviation the sweep step asks
    /// for, not a modelling error.
    /// </summary>
    private static Inspection Flank(IReadOnlyDictionary<string, double> p)
    {
        double m = p["m"];
        int z = (int)p["z"];
        double sweep = p["sweep"], width = p["width"];
        double alphaDeg = 20;

        // Below z = 2/sin²α a rack-rolled gear is undercut: its flank is not the involute, and a deviation against the
        // involute then reports the undercut, not an error. z = 10 measures 1357 µm there, which is a true number about
        // the wrong thing. Refuse it rather than colour a flank against a form it does not have.
        double zMin = 2 / (Math.Sin(alphaDeg * Math.PI / 180) * Math.Sin(alphaDeg * Math.PI / 180));
        if (z < zMin)
            throw new ArgumentException(
                $"z = {z} is undercut (below z = 2/sin²α = {zMin:0.#}); its flank is not the involute. Use z ≥ {Math.Ceiling(zMin):0}.");

        var tol = Tolerance.Budget(totalUm: (Tolerance.NumericNm + 200 + sweep + 20) / 1000, chordNm: 200, sweepNm: sweep);
        double rp = m * z / 2 * Units.NmPerMm, pitch = Math.PI * m * Units.NmPerMm;
        var blank = Region2.Circle(default, m * z / 2 + m, tol);
        var rack = GearProfile.Rack(m, 7, 20, bodyMm: 0.3 * m);
        var motion = Motion2.Sequence(Enumerable.Range(0, z).Select(k => Motion2.Custom(t =>
        {
            double dphi = 2 * Math.PI / z * t, phi = 2 * Math.PI * k / z + dphi;
            return Pose2.Rotation(-phi).Compose(new Pose2(0, pitch / 2 - rp * dphi, rp));
        })).ToArray());
        var profile = Process2.Cut([blank], rack, motion, tol, out var stats)[0];

        double alpha = 20 * Math.PI / 180, rb = rp * Math.Cos(alpha), ra = rp + m * Units.NmPerMm;
        double psiB = Math.PI / (2 * z) + GearProfile.Inv(alpha);
        double lo = rb + 0.3 * m * Units.NmPerMm, hi = ra - 0.1 * m * Units.NmPerMm;

        // Signed normal distance from the involute, in nm: the angular difference times r·sin(pressure angle at r). r is
        // already in nanometres, so the product is one too.
        double Deviation(Vec2 v)
        {
            double r = Math.Sqrt((double)v.X * v.X + (double)v.Y * v.Y);
            if (r < lo || r > hi) return 0;
            double ar = Math.Acos(rb / r);
            double rel = Math.Abs(Math.IEEERemainder(Math.Atan2(v.Y, v.X), 2 * Math.PI / z));
            return r * (rel - (psiB - GearProfile.Inv(ar))) * Math.Sin(ar);
        }

        double worst = 0;
        foreach (var c in profile.Contours)
            foreach (var v in c.Points) worst = Math.Max(worst, Math.Abs(Deviation(v)));

        var result = new Inspection
        {
            Id = "flank",
            Name = "Gear flank against the involute",
            Description =
                $"A {z}-tooth rack-rolled gear, module {m:0.##} mm. The colour is the signed normal deviation of every " +
                "flank vertex from the ideal involute; outside the flank band the surface stays neutral, the way an " +
                "inspection only reports the region of interest. The deviation follows the sweep step — it is not a " +
                "modelling error. This is the expensive case: the exact kernel cuts one hull and one Boolean per roll " +
                "step, so a 20-tooth gear takes half a minute natively and minutes in the browser. And the number it " +
                "prints is a sample, not a property: the 2D boolean kernel is not a pure function, so the same input " +
                "gives a different flank depending on machine load — see GearFlankFlakeFindings.md. Read it as the " +
                "worst of several runs, or do not read it.",
            Body = Solid.Extrude(profile, 0, width),
            ValueLabel = "deviation from the ideal involute (flank band)",
            ValueNm = v => Deviation(Vec2.Nm(v.X, v.Y)),
        };

        result.Metric("worst flank deviation", $"{worst:0.0} nm");
        result.Metric("sweep step asked for", $"{sweep:0} nm");
        result.Metric("pitch circle", $"{m * z:0.##} mm");
        result.Metric("roll steps", stats.Intervals.ToString(CultureInfo.InvariantCulture));
        result.Metric("swept pieces", stats.Pieces.ToString(CultureInfo.InvariantCulture));
        result.Metric("profile vertices", profile.Contours.Sum(c => c.Count).ToString(CultureInfo.InvariantCulture));
        result.Checks.Add(new("worst flank deviation", worst, 0, sweep, "nm"));
        result.NominalLines = InvoluteLines(z, rb, ra, psiB, width);
        double half = m * z / 2 + m + 1;
        result.Zoom = [("whole gear", [-half, -half, -1, half, half, width + 1]),
                       ("one tooth", [-m * 2, m * z / 2 - m, -1, m * 2, m * z / 2 + m, width + 1])];
        return result;
    }
}