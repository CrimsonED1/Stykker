using Stykker.NanoCut.Cutting;
using Stykker.NanoCut.Geometry2D;

namespace Stykker.NanoCut.Tests;

public class Cutting2Tests
{
    [Fact]
    public void SquareToolSweptAlongLineIsExact()
    {
        var tool = new[] { Vec2.Mm(-1, -1), Vec2.Mm(1, -1), Vec2.Mm(1, 1), Vec2.Mm(-1, 1) };
        var swept = Minkowski2.SweepConvex(tool, [Vec2.Mm(0, 0), Vec2.Mm(10, 0)]);
        Assert.Equal(24.0, swept.AreaMm2, 12);
        // Diagonal move: hexagon area 4 + 2·(dx + dy) with dx = dy = 3.
        var diag = Minkowski2.SweepConvex(tool, [Vec2.Mm(0, 0), Vec2.Mm(3, 3)]);
        Assert.Equal(4 + 2 * (3 + 3), diag.AreaMm2, 12);
    }

    [Fact]
    public void CircleToolAlongPolylineHasRoundedCorner()
    {
        var tol = Tolerance.Default;
        var tool = Shapes2.CirclePoints(default, 1e6, tol.ChordNm);
        var swept = Minkowski2.SweepConvex(tool, [Vec2.Mm(0, 0), Vec2.Mm(10, 0), Vec2.Mm(10, 10)]);
        // Two 10 mm stadium segments sharing a disc at the corner: 2·(2·10) + π·r² - overlap quarter discs.
        // Union = two 10 × 2 rectangles (their 1 × 1 corner overlap counted once) + outer quarter disc at the
        // corner + two half discs at the ends.
        double exact = 2 * 20 - 1 + Math.PI / 4 + Math.PI / 2 + Math.PI / 2;
        // Inscribed tool polygon: the boundary moves inwards by at most the chord error.
        double allowed = swept.PerimeterMm * tol.ChordNm / Units.NmPerMm;
        Assert.InRange(swept.AreaMm2, exact - allowed, exact);
        Assert.Single(swept.Contours);
        Boolean2Tests.AssertValid(swept);
    }

    [Fact]
    public void ArcPathKeepsSweepError()
    {
        var tol = Tolerance.Default;
        var stock = Region2.Rectangle(Vec2.Mm(-20, -20), Vec2.Mm(20, 20));
        // Circle tool r = 1 mm on a full circle of radius 10 mm: removes an annulus 9..11 mm.
        var path = ToolPath2.StartAt(Vec2.Mm(10, 0)).ArcAround(Vec2.Mm(0, 0), 360);
        var result = Cutter2.Cut(stock, Tool2.Circle(1), path, tol);
        double exact = Math.PI * (11 * 11 - 9 * 9);
        // Chord path (≤ 30 nm sagitta) and tool polygon (≤ 50 nm) shrink the annulus by ≤ 80 nm per side.
        double allowed = 2 * Math.PI * (11 + 9) * (tol.SweepNm + tol.ChordNm) / Units.NmPerMm;
        Assert.InRange(result.RemovedAreaMm2, exact - allowed, exact + 1e-9);
        Assert.Equal(2, result.Removed.Contours.Count);
        Assert.Equal(Math.Round(1600 - result.RemovedAreaMm2, 9), Math.Round(result.Remaining.AreaMm2, 9));
    }

    [Fact]
    public void OffsetOfSquare()
    {
        var tol = Tolerance.Default;
        var sq = Region2.Rectangle(Vec2.Mm(0, 0), Vec2.Mm(10, 10));
        var grown = sq.Offset(1, tol);
        double exact = 100 + 4 * 10 + Math.PI;
        double allowed = grown.PerimeterMm * tol.ChordNm / Units.NmPerMm;
        Assert.InRange(grown.AreaMm2, exact - allowed, exact + 1e-9);
        var shrunk = sq.Offset(-1, tol);
        Assert.Equal(64.0, shrunk.AreaMm2, 6);
        Assert.Single(shrunk.Contours);
    }
}
