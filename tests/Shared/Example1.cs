using Stykker.NanoCut.Cutting;
using Stykker.NanoCut.Geometry2D;

namespace Stykker.NanoCut.Testing;

/// <summary>
/// Example 1 of the plan, 2D part: ball tool r = 3 mm, 1 mm deep, across a 20 × 10 mm cross-section
/// (cut at x = 10 of the 20 × 20 × 10 mm block). The cross-section of the groove is a circular segment.
/// </summary>
public static class Example1
{
    /// <summary>Tool radius in mm.</summary>
    public const double R = 3;

    /// <summary>Plunge depth below the top face in mm.</summary>
    public const double H = 1;

    /// <summary>Exact circular-segment area r²·acos((r-h)/r) - (r-h)·√(2rh - h²).</summary>
    public static double ExactSegmentArea => R * R * Math.Acos((R - H) / R) - (R - H) * Math.Sqrt(2 * R * H - H * H);

    /// <summary>Exact groove width at the top face, 2·√(2rh - h²).</summary>
    public static double ExactWidth => 2 * Math.Sqrt(2 * R * H - H * H);

    /// <summary>Runs all 2D checks of example 1.</summary>
    public static IReadOnlyList<ReferenceCheck> Run2D(Tolerance? tol = null)
    {
        tol ??= Tolerance.Default;
        var checks = new List<ReferenceCheck>();
        double budgetMm = tol.TotalMm;

        // Step 1: rectangle 20 × 10 mm and circle around (10, 12), r = 3 mm.
        var rect = Region2.Rectangle(Vec2.Mm(0, 0), Vec2.Mm(20, 10), tol);
        var circ = Region2.Circle(Vec2.Mm(10, 12), R, tol);
        var cut = rect & circ;
        var rest = rect - circ;

        // Allowed area error: arc length × 0.1 µm.
        double arcLength = 2 * R * Math.Acos((R - H) / R);
        checks.Add(new("2D cut area (rect ∩ circle)", cut.AreaMm2, ExactSegmentArea, arcLength * budgetMm, "mm²"));
        checks.Add(new("2D remaining area (rect − circle)", rest.AreaMm2, 200 - ExactSegmentArea, arcLength * budgetMm, "mm²"));

        // Groove width at the top face (y = 10): extreme cut vertices on the top edge.
        long top = Units.MmToNm(10);
        var onTop = cut.Contours.SelectMany(c => c.Points.ToArray()).Where(p => p.Y == top).Select(p => p.XMm).ToList();
        double width = onTop.Count >= 2 ? onTop.Max() - onTop.Min() : 0;
        checks.Add(new("2D groove width at top", width, ExactWidth, 2 * budgetMm, "mm"));

        // Maximum depth below the top face.
        var depth = Penetration2.Analyze(rect, circ);
        checks.Add(new("2D max depth", depth.DepthMm, H, budgetMm, "mm"));

        // Every arc vertex of the groove lies at distance r from the tool axis (10, 12).
        var center = Vec2.Mm(10, 12);
        double worst = R;
        foreach (var p in cut.Contours.SelectMany(c => c.Points.ToArray()).Where(p => p.Y < top))
        {
            double dx = p.XMm - center.XMm, dy = p.YMm - center.YMm;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (Math.Abs(d - R) > Math.Abs(worst - R)) worst = d;
        }
        checks.Add(new("2D groove vertex distance to axis (worst)", worst, R, budgetMm, "mm"));

        // Same groove as material removal of a moving tool (2D sweep: tool moves along the cut plane in x).
        var stock = Region2.Rectangle(Vec2.Mm(0, 0), Vec2.Mm(20, 10), tol);
        var result = Cutter2.Cut(stock, Tool2.Circle(R), ToolPath2.Linear(Vec2.Mm(-5, 12), Vec2.Mm(25, 12)), tol);
        // Along the path the removed band is 20 mm long with depth 1 mm: area = 20 · 1.
        checks.Add(new("2D sweep removed area (20 mm × 1 mm)", result.RemovedAreaMm2, 20 * H, 2 * 20 * budgetMm, "mm²"));
        checks.Add(new("2D sweep max depth", result.MaxDepthMm, H, budgetMm, "mm"));
        return checks;
    }
}
