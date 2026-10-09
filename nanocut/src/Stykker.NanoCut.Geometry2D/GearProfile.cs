namespace Stykker.NanoCut.Geometry2D;

/// <summary>
/// Generators for gear geometry: involute spur gear profiles and the matching rack tool. All curves are
/// inscribed with sagitta ≤ the given chord error.
/// </summary>
public static class GearProfile
{
    /// <summary>Involute function inv(α) = tan α − α.</summary>
    public static double Inv(double alpha) => Math.Tan(alpha) - alpha;

    /// <summary>
    /// External involute spur gear centred at the origin. Tip radius r_p + m, root radius r_p − 1.25·m,
    /// tooth thickness π·m/2 on the pitch circle; below the base circle the flank continues radially.
    /// </summary>
    public static Region2 Involute(double moduleMm, int teeth, double pressureAngleDeg = 20, Tolerance? tol = null)
    {
        tol ??= Tolerance.Default;
        if (teeth < 6) throw new ArgumentOutOfRangeException(nameof(teeth));
        double m = moduleMm * Units.NmPerMm;
        double alpha = pressureAngleDeg * Math.PI / 180;
        double rp = m * teeth / 2, rb = rp * Math.Cos(alpha), ra = rp + m, rf = rp - 1.25 * m;
        double psiB = Math.PI / (2 * teeth) + Inv(alpha); // half tooth angle at the base circle
        double Theta(double r) => psiB - Inv(Math.Acos(Math.Min(1, rb / r)));
        if (Theta(ra) <= 0) throw new ArgumentException("Pointed teeth: tip circle too large.");
        double s = tol.ChordNm;

        // Flank as radius samples from max(rb, rf) to ra with chord error ≤ s.
        double rStart = Math.Max(rb, rf);
        var radii = new List<double> { rStart };
        Refine(rStart, ra, radii, r => (r, Theta(r)), s);

        var pts = new List<Vec2>();
        for (int k = 0; k < teeth; k++)
        {
            double phi = 2 * Math.PI * k / teeth;
            // Root radial segment (if the root circle is below the base circle), then the right flank upwards.
            if (rf < rb) pts.Add(Polar(rf, phi - psiB));
            foreach (var r in radii) pts.Add(Polar(r, phi - Theta(r)));
            // Tip arc.
            pts.AddRange(Arc(ra, phi - Theta(ra), phi + Theta(ra), s).Skip(1).SkipLast(1));
            // Left flank downwards.
            for (int i = radii.Count - 1; i >= 0; i--) pts.Add(Polar(radii[i], phi + Theta(radii[i])));
            if (rf < rb) pts.Add(Polar(rf, phi + psiB));
            // Root arc to the next tooth.
            double rootStart = phi + (rf < rb ? psiB : Theta(rf));
            double rootEnd = phi + 2 * Math.PI / teeth - (rf < rb ? psiB : Theta(rf));
            pts.AddRange(Arc(rf, rootStart, rootEnd, s).Skip(1).SkipLast(1));
        }
        var unique = new List<Vec2>(pts.Count);
        foreach (var p in pts)
            if (unique.Count == 0 || unique[^1] != p) unique.Add(p);
        if (unique.Count > 1 && unique[0] == unique[^1]) unique.RemoveAt(unique.Count - 1);
        return Region2.FromContours([new Contour2(unique)]).Normalize();
    }

    /// <summary>
    /// Rack cutter for generating gears of module m: pitch line y = 0, teeth pointing to −y with tip at −1.25·m
    /// and root at +m, tooth thickness π·m/2 on the pitch line, flanks inclined by the pressure angle.
    /// Teeth are centred at x = k·π·m for k = −n/2 … n/2; a body of height <paramref name="bodyMm"/> lies above.
    /// </summary>
    public static Region2 Rack(double moduleMm, int teeth, double pressureAngleDeg = 20, double bodyMm = 2)
    {
        double m = moduleMm * Units.NmPerMm;
        double t = Math.Tan(pressureAngleDeg * Math.PI / 180);
        double p = Math.PI * m;
        double yTip = -1.25 * m, yRoot = m, yTop = m + bodyMm * Units.NmPerMm;
        double halfPitch = p / 4; // half tooth thickness on the pitch line
        var contours = new List<Contour2>();
        int first = -teeth / 2;
        for (int k = first; k < first + teeth; k++)
        {
            double c = k * p;
            // Trapezoid from tip to root (wider at the root).
            contours.Add(new Contour2([
                P(c - halfPitch - yTip * t, yTip), P(c + halfPitch + yTip * t, yTip),
                P(c + halfPitch + yRoot * t, yRoot), P(c - halfPitch - yRoot * t, yRoot)]));
        }
        double x0 = (first - 0.5) * p, x1 = (first + teeth - 0.5) * p;
        contours.Add(new Contour2([P(x0, yRoot), P(x1, yRoot), P(x1, yTop), P(x0, yTop)]));
        return Region2.FromContours(contours).Normalize();

        static Vec2 P(double x, double y) => Vec2.Nm((long)Math.Round(x), (long)Math.Round(y));
    }

    private static Vec2 Polar(double r, double angle) =>
        Vec2.Nm((long)Math.Round(r * Math.Cos(angle)), (long)Math.Round(r * Math.Sin(angle)));

    private static IEnumerable<Vec2> Arc(double r, double from, double to, double s)
    {
        int n = Discretization.SegmentCount(r, s, to - from);
        for (int i = 0; i <= n; i++) yield return Polar(r, from + (to - from) * i / n);
    }

    // Adds radii in (r0, r1] so that the polyline through curve(r) has chord error ≤ s.
    private static void Refine(double r0, double r1, List<double> radii, Func<double, (double R, double Theta)> curve, double s)
    {
        var (ar, at) = curve(r0);
        var (br, bt) = curve(r1);
        double rm = (r0 + r1) / 2;
        var (mr, mt) = curve(rm);
        double ax = ar * Math.Cos(at), ay = ar * Math.Sin(at);
        double bx = br * Math.Cos(bt), by = br * Math.Sin(bt);
        double mx = mr * Math.Cos(mt), my = mr * Math.Sin(mt);
        double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        double dev = len == 0 ? 0 : Math.Abs((bx - ax) * (my - ay) - (by - ay) * (mx - ax)) / len;
        if (dev > s / 4 && r1 - r0 > 1)
        {
            Refine(r0, rm, radii, curve, s);
            Refine(rm, r1, radii, curve, s);
        }
        else radii.Add(r1);
    }
}
