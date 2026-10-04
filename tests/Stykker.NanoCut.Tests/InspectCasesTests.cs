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
            var buffers = inspection.Body.ToMeshBuffers(OriginMode.Centroid);
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
    /// Verified for 32 and 64 facets, which is where the page's default sits. See
    /// <see cref="ThePageOnlyOffersRimWidthsThatHoldTheirChordError"/> for why the page stops there.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1, 32)]
    [InlineData(0.5, 32)]
    [InlineData(1, 64)]
    [InlineData(0.2, 64)]
    public void EveryRimFacetLandsOnItsChordError(double chord, double facets)
    {
        var inspection = Build("facets", ("radius", 5), ("chord", chord), ("facets", facets), ("z", 0.2));
        Assert.All(inspection.Checks, c => Assert.True(c.Passed,
            $"{c.Name}: |Δ| {c.Deviation} > {c.Allowed} {c.Unit} (chord {chord} nm, {facets:0} facets)"));
    }

    /// <summary>
    /// Known limitation, written down so it cannot be forgotten: above roughly a hundred facets the widest facet has
    /// been observed to miss its own sagitta by an amount of the order of that sagitta, and the effect has not been
    /// pinned down — 128 facets failed on one run and passed on the next, which is itself the reason to distrust the
    /// range. The page's default (64) is inside the verified range, and the parameter is capped there so the page
    /// cannot offer a number that is not known to hold.
    /// </summary>
    [Fact]
    public void ThePageOnlyOffersRimWidthsThatHoldTheirChordError()
    {
        var param = InspectCases.ParamsFor("facets").Single(p => p.Key == "facets");
        Assert.Equal(64, param.Max);
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
        double[] rim = inspection.Body.Vertices
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
        double[] seen = inspection.Body.Vertices
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