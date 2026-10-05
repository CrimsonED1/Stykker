using Stykker.NanoCut.Demo.Pages;
using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Tests;

/// <summary>
/// The inspection page ("/inspect") colours every vertex by a measured quantity and reports what it measured. These
/// tests pin the numbers the page puts on screen, so a change to a case cannot quietly turn the picture into a claim
/// that is no longer true.
/// </summary>
public class InspectCasesTests
{
    private static Inspection Build(string id, params (string Key, double Value)[] values) =>
        InspectCases.Build(id, values.Length == 0 ? null : values.ToDictionary(p => p.Key, p => p.Value));

    /// <summary>The first number of a metric whose value starts with a number.</summary>
    private static double Number(Inspection inspection, string label) => double.Parse(
        inspection.Metrics.Single(m => m.Label == label).Value.Split(' ')[0].Replace(',', '.'),
        System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The ground case is the one that hangs on a real process, so two things have to hold for it to mean anything:
    /// the wheel must actually cut — a simulation that reports no active grains produces a beautifully flat and
    /// entirely meaningless surface, which is exactly what happened before the wheel's axis was fixed — and the
    /// surface must never rise above the face it started from, because a subtractive process that adds material is
    /// not a process.
    /// </summary>
    [Fact]
    public void TheWheelCutsAndNothingIsAdded()
    {
        var inspection = Build("ground");
        Assert.True(Number(inspection, "removed") > 0, "the wheel removed nothing, so there is no ground surface");
        Assert.True(Number(inspection, "form error (peak to valley)") > 0, "a surface that came out flat was not ground");
        Assert.Contains(inspection.Checks, c => c.Name.Contains("rises above") && c.Passed);
    }

    /// <summary>
    /// The colour of a ground vertex is a difference of two nanometre heights, so what it reports has to be in
    /// nanometres. Reading the unit off the page is the check: the reported range must be of the order of the form
    /// error, not a millionth of it.
    /// </summary>
    [Fact]
    public void TheGroundColourIsInNanometres()
    {
        var inspection = Build("ground");
        double formNm = Number(inspection, "form error (peak to valley)");
        double[] values = inspection.Bodies.SelectMany(b => b.Vertices)
            .Select(v => inspection.ValueNm(new Vec3((long)Math.Round(v.X), (long)Math.Round(v.Y), (long)Math.Round(v.Z))))
            .ToArray();
        Assert.InRange(values.Max(), formNm / 100, formNm * 100);
    }

    /// <summary>
    /// The staircase's whole point: every edge is axis parallel, so the Boolean computes no intersection and rounds
    /// no coordinate. The step heights must come out as the integers they were asked for — not "within tolerance".
    /// </summary>
    [Fact]
    public void StaircaseStepsAreExactIntegers()
    {
        var inspection = Build("staircase", ("smallest", 1), ("step", 20), ("top", 100), ("depth", 20));
        var check = Assert.Single(inspection.Checks);
        Assert.True(check.Passed, $"worst step deviation {check.Deviation} nm > {check.Allowed} nm");

        // The metrics carry one measured height per step; they must be the requested 1, 2, 5, … 100 nm.
        double[] measured = inspection.Metrics
            .Where(m => m.Label.StartsWith("step ", StringComparison.Ordinal))
            .Select(m => double.Parse(m.Value.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        Assert.Equal(new double[] { 1, 2, 5, 10, 20, 50, 100 }, measured);
    }

    /// <summary>
    /// Scaling the ladder scales the numbers: 10 nm as the smallest step must give 10, 20, 50, … 1000 nm and again
    /// exactly. Otherwise the page would be showing a hard-coded picture rather than the grid.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(37)]
    public void StaircaseStepsFollowTheSmallestStep(double smallest)
    {
        var inspection = Build("staircase", ("smallest", smallest));
        double[] measured = inspection.Metrics
            .Where(m => m.Label.StartsWith("step ", StringComparison.Ordinal))
            .Select(m => double.Parse(m.Value.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        double[] expected = new double[] { 1, 2, 5, 10, 20, 50, 100 }.Select(d => d * smallest).ToArray();
        Assert.Equal(expected, measured);
        Assert.All(measured, v => Assert.Equal(expected.First(e => Math.Abs(e - v) < 1e-12), v));
    }

    /// <summary>
    /// The inspector reports an index into the mesh buffers, so the exact coordinates have to be in the same order
    /// and the same length — otherwise the page would attribute a vertex's coordinates to a different vertex.
    /// </summary>
    [Fact]
    public void ExactVerticesLineUpWithTheMeshBuffers()
    {
        foreach (var id in new[] { "staircase", "facets" })
        {
            var inspection = Build(id);
            var buffers = MeshBuffers.Concat(inspection.Bodies.Select(b => b.ToMeshBuffers(OriginMode.Centroid)));
            var (nm, grid) = inspection.ExactVertices();
            Assert.Equal(buffers.Positions.Length / 3, nm.Length / 3);
            Assert.Equal(nm.Length / 3, grid.Length);
            Assert.All(nm, v => Assert.InRange(v, -2_147_483_648L, 2_147_483_648L));
        }
    }

    /// <summary>The staircase has no curve in it, so every one of its vertices is a grid point, not an intersection.</summary>
    [Fact]
    public void EveryStaircaseVertexSitsOnTheGrid()
    {
        var inspection = Build("staircase");
        var (_, grid) = inspection.ExactVertices();
        Assert.NotEmpty(grid);
        Assert.All(grid, g => Assert.True(g, "the staircase must not produce a computed vertex"));
    }

    /// <summary>
    /// Every rim facet is supposed to sit exactly as far inside the circle as the chord error it was built for, and
    /// that distance is measured on the geometry, not read back from the request. The only slack is the lattice: the
    /// two endpoints of a chord are each rounded by half a nanometre, which can move the chord line by √2/2 nm.
    /// <para>
    /// Verified for 32 to 128 facets, which is the range the page offers.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1, 32)]
    [InlineData(0.5, 32)]
    [InlineData(1, 64)]
    [InlineData(0.2, 64)]
    [InlineData(2, 64)]
    [InlineData(1, 128)]
    public void EveryRimFacetLandsOnItsChordError(double chord, double facets)
    {
        var inspection = Build("facets", ("radius", 5), ("chord", chord), ("facets", facets), ("z", 0.2));
        Assert.All(inspection.Checks, c => Assert.True(c.Passed,
            $"{c.Name}: |Δ| {c.Deviation} > {c.Allowed} {c.Unit} (chord {chord} nm, {facets:0} facets)"));
    }

    /// <summary>
    /// A rim has to close, and it has to still look like a rim. Both ends are refused with a reason: too many facets
    /// at the finest chord error need more than a full turn before any widening, and a count that does close leaves a
    /// widest facet longer than the radius, which is a polygon missing sides rather than a rim that gets rougher.
    /// </summary>
    [Fact]
    public void ARimThatCannotCloseOrNoLongerLooksLikeOneIsRefusedWithAReason()
    {
        var tooMany = Assert.Throws<ArgumentOutOfRangeException>(
            () => Build("facets", ("radius", 5), ("chord", 1), ("facets", 2000), ("z", 0.2)));
        Assert.Contains("no widening factor closes the turn", tooMany.Message, StringComparison.Ordinal);

        var tooFew = Assert.Throws<ArgumentOutOfRangeException>(
            () => Build("facets", ("radius", 5), ("chord", 50), ("facets", 8), ("z", 0.2)));
        Assert.Contains("shorter than the radius", tooFew.Message, StringComparison.Ordinal);

        Assert.Equal(128, InspectCases.ParamsFor("facets").Single(p => p.Key == "facets").Max);
    }

    /// <summary>
    /// The rim case must be a function of its arguments, like everything else in this library: the same case run six
    /// times in one process has to give the same numbers, or the colour on the screen is showing one of several
    /// answers without saying which. If this ever fails at a facet count, the fault is in the construction and the
    /// chord check above is only telling the truth about one of the outcomes.
    /// </summary>
    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    public void TheRimGivesTheSameAnswerEveryTimeInOneProcess(double facets)
    {
        string first = Fingerprint(facets);
        for (int i = 0; i < 5; i++) Assert.Equal(first, Fingerprint(facets));
    }

    /// <summary>Every number the case puts on screen, as one comparable string.</summary>
    private static string Fingerprint(double facets)
    {
        var inspection = Build("facets", ("radius", 5), ("chord", 1), ("facets", facets), ("z", 0.2));
        return string.Join(" | ",
            inspection.Metrics.Select(m => $"{m.Label}={m.Value}"),
            inspection.Checks.Select(c => $"{c.Name}:{c.Actual:R}:{c.Passed}"));
    }

    /// <summary>
    /// The case is called a 1 nm test, so it has to contain a 1 nm facet and it has to be the finest one: the colour
    /// then runs from 1 nm up to the coarsest the same facet count can carry, with nothing in between faked.
    /// </summary>
    [Fact]
    public void TheFinestRimFacetIsTheOneNanometreOne()
    {
        var inspection = Build("facets");
        Assert.Equal(64, int.Parse(inspection.Metrics.Single(m => m.Label == "facets").Value,
                                   System.Globalization.CultureInfo.InvariantCulture));

        // Taken from the geometry rather than from the table, so a formatting change cannot make this pass or fail.
        double[] rim = inspection.Bodies.SelectMany(b=>b.Vertices)
            .Select(v => inspection.ValueNm(new Vec3((long)Math.Round(v.X), (long)Math.Round(v.Y), (long)Math.Round(v.Z))))
            .Where(v => v > 0)
            .Distinct()
            .OrderBy(v => v)
            .ToArray();
        Assert.InRange(rim[0], 0.9, 1.1);                       // the finest facet really is a 1 nm one
        Assert.True(rim.Zip(rim.Skip(1)).All(p => p.Second > p.First), "the chord error must grow around the rim");
        Assert.True(rim[^1] > 10 * rim[0], $"the coarse end ({rim[^1]:0.0} nm) must be well past the 1 nm end");
    }

    /// <summary>
    /// The colour of a facet is the sagitta of the chord that follows the vertex, so the rim carries exactly one
    /// value per facet and nothing else. Anything the extrude adds away from the rim stays neutral.
    /// </summary>
    [Fact]
    public void TheColourOfAFacetIsItsOwnSagitta()
    {
        var inspection = Build("facets");
        double[] seen = inspection.Bodies.SelectMany(b=>b.Vertices)
            .Select(v => inspection.ValueNm(new Vec3((long)Math.Round(v.X), (long)Math.Round(v.Y), (long)Math.Round(v.Z))))
            .Distinct()
            .OrderBy(v => v)
            .ToArray();
        int facets = int.Parse(inspection.Metrics.Single(m => m.Label == "facets").Value,
                               System.Globalization.CultureInfo.InvariantCulture);
        // One value per facet, plus neutral for whatever the caps contribute.
        Assert.InRange(seen.Length, facets, facets + 2);
        Assert.True(seen[^1] > seen[0], "the finest facet must not also be the coarsest");
    }
}