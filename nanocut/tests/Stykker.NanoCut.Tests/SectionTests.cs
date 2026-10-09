using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Geometry3D;
using Xunit.Abstractions;

namespace Stykker.NanoCut.Tests;

public class SectionTests(ITestOutputHelper output)
{
    private static readonly Tolerance Tol = Tolerance.Budget(totalUm: 0.1, chordNm: 50, sweepNm: 30);
    private static readonly (double, double, double) ZAxis = (0, 0, 1);

    [Fact]
    public void BoxSectionIsExactAndWindowed()
    {
        var box = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(10, 20, 30));
        var s = Section3.Cut(box, SectionPlane.XY(5));
        Assert.Equal((Int128)2 * 200 * Units.NmPerMm * Units.NmPerMm, s.TwiceAreaNm2);
        var w = Section3.Cut(box, SectionPlane.XY(5), 2, 4, double.NegativeInfinity, double.PositiveInfinity);
        Assert.Equal(40, w.AreaMm2, 12);
        var side = Section3.Cut(box, SectionPlane.XZ(7));
        Assert.Equal(300, side.AreaMm2, 12);
        Assert.Equal((Vec2.Mm(0, 0), Vec2.Mm(10, 30)), side.Bounds);
    }

    [Fact]
    public void FaceInThePlaneCountsAsJustBelowIt()
    {
        var box = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(10, 20, 30));
        Assert.Equal(200, Section3.Cut(box, SectionPlane.XY(30)).AreaMm2, 12);
        Assert.True(Section3.Cut(box, SectionPlane.XY(0)).IsEmpty);
    }

    [Fact]
    public void TiltedPlaneThroughBox()
    {
        var box = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(10, 20, 30));
        var s = Section3.Cut(box, SectionPlane.Through(Vec3.Mm(0, 0, 0), Vec3.Mm(10, 20, 0), ZAxis));
        double expected = Math.Sqrt(500) * 30;
        output.WriteLine($"area {s.AreaMm2:F9} expected {expected:F9}");
        // End points are rounded to the nm grid of the plane frame: ≤ 0.5 nm per end over 30 mm height.
        Assert.InRange(s.AreaMm2, expected - 30e-6, expected + 30e-6);
    }

    [Fact]
    public void AxialProfileOfTurnedPartMatchesItsGenerator()
    {
        // Stepped shaft: r 10 for z 0…20, r 6 for z 20…40, with a 2 mm bore.
        var profile = Region2.Polygon(Vec2.Mm(2, 0), Vec2.Mm(10, 0), Vec2.Mm(10, 20), Vec2.Mm(6, 20), Vec2.Mm(6, 40), Vec2.Mm(2, 40));
        var shaft = Solid.Revolve(profile, Tol);

        // Angle 0 runs through the revolve's vertices: the section is the generator up to rounding.
        var at0 = Section3.Axial(shaft, Vec3.Mm(0, 0, 0), ZAxis, 0, 0, 40);
        var dev0 = Profile2.Deviation(at0, profile);
        output.WriteLine($"angle 0: max |dev| {dev0.MaxAbsMm * 1e6:F2} nm");
        Assert.True(dev0.MaxAbsMm < 2e-6);

        // Halfway between two vertices the facets are closest to the axis: at most the chord error inside.
        var mid = Section3.Axial(shaft, Vec3.Mm(0, 0, 0), ZAxis, 0.0123, 0, 40);
        var dev = Profile2.Deviation(mid, profile);
        output.WriteLine($"angle 0.0123: excess {dev.MaxExcessMm * 1e6:F2} nm, shortfall {dev.MaxShortfallMm * 1e6:F2} nm");
        // Rounding: revolve vertices (≤ 0.87 nm) plus section end points (≤ 0.71 nm) plus the 2 nm join tolerance.
        Assert.True(dev.MaxExcessMm < 3e-6);
        Assert.True(dev.MaxShortfallMm < Tol.ChordNm * 1e-6 + 3e-6);

        // Radius along the axis, with a range.
        var z = Profile2.Positions(5, 35, 7);
        var r = Profile2.Radii(at0, 5, 35, 7);
        var bore = Profile2.InnerRadii(at0, 5, 35, 7);
        for (int i = 0; i < z.Length; i++)
        {
            Assert.Equal(z[i] <= 20 ? 10 : 6, r[i], 5); // at z = 20 the shoulder face counts
            Assert.Equal(2, bore[i], 5);
        }

        // A range in z and r only keeps that window.
        var window = Section3.Axial(shaft, Vec3.Mm(0, 0, 0), ZAxis, 0, 10, 30, 5, 8);
        Assert.Equal(3 * 10 + 1 * 10, window.AreaMm2, 5);
    }

    [Fact]
    public void EnvelopeAndRadialSectionShowRunOut()
    {
        // Cylinder r = 5 whose axis is 50 µm off the measuring axis: run-out 2 · 50 µm.
        const double e = 0.05;
        var cyl = Solid.Cylinder(Vec3.Mm(e, 0, 0), Vec3.Mm(e, 0, 10), 5, Tol);
        var (outer, inner, profiles) = Section3.AxialEnvelope(cyl, Vec3.Mm(0, 0, 0), ZAxis, 36, 1, 9);
        Assert.Equal(36, profiles.Count);
        double rOut = Profile2.Radii(outer, 5, 5, 1)[0], rIn = Profile2.Radii(inner, 5, 5, 1)[0];
        output.WriteLine($"envelope r {rIn:F6} … {rOut:F6}");
        Assert.Equal(5 + e, rOut, 4);
        Assert.Equal(5 - e, rIn, 4);

        var radial = Section3.Radial(cyl, Vec3.Mm(0, 0, 0), ZAxis, 5);
        var rho = Profile2.Polar(radial, (0, 0), 0, 2 * Math.PI, 361);
        output.WriteLine($"radial r {rho.Min():F6} … {rho.Max():F6}");
        Assert.Equal(2 * e, rho.Max() - rho.Min(), 4);
        Assert.Equal(5 + e, rho[0], 4);
        Assert.Equal(5 - e, rho[180], 4);
    }

    [Fact]
    public void LineProfileFollowsAGroove()
    {
        var part = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(10, 10, 5)) - Solid.Box(Vec3.Mm(4, -1, 4), Vec3.Mm(6, 11, 6));
        var h = Section3.LineProfile(part, Vec3.Mm(0, 5, 0), Vec3.Mm(10, 5, 0), ZAxis, 101);
        var s = Profile2.Positions(0, 10, 101);
        for (int i = 0; i < h.Length; i++)
            Assert.Equal(s[i] > 4 && s[i] < 6 ? 4 : 5, h[i], 9);
        var (ra, rz) = Profile2.Roughness(h);
        Assert.Equal(1, rz, 9);
        Assert.True(ra > 0);
    }

    [Fact]
    public void SectionThroughExactBooleanVertices()
    {
        // Box minus sphere: the section at z = 1.234 crosses faces whose vertices are exact (non-grid) points.
        var part = Solid.Box(Vec3.Mm(-5, -5, -5), Vec3.Mm(5, 5, 5)) - Solid.Sphere(Vec3.Mm(0, 0, 0), 4, Tol);
        var s = Section3.Cut(part, SectionPlane.XY(1.234));
        double r2 = 16 - 1.234 * 1.234;
        double expected = 100 - Math.PI * r2;
        output.WriteLine($"area {s.AreaMm2:F6} expected {expected:F6}, contours {s.Contours.Count}");
        Assert.Equal(2, s.Contours.Count);
        Assert.InRange(s.AreaMm2, expected - 1e-3, expected + 1e-3);
    }

    [Fact]
    public void DeviationOfShiftedEdge()
    {
        var nominal = Region2.Rectangle(Vec2.Mm(0, 0), Vec2.Mm(10, 5));
        var actual = Region2.Rectangle(Vec2.Mm(0, 0), Vec2.Mm(10.01, 4.998));
        var d = Profile2.Deviation(actual, nominal);
        Assert.Equal(0.01, d.MaxExcessMm, 9);
        Assert.Equal(0.002, d.MaxShortfallMm, 9);
        Assert.Equal(0.01 * 4.998, d.ExcessAreaMm2, 9);
        Assert.Equal(10 * 0.002, d.MissingAreaMm2, 9);
        Assert.StartsWith("contour,index,u_mm,v_mm\n0,", Profile2.ToCsv(actual));
    }
}
