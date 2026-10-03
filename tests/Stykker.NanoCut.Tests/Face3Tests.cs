using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Tests;

public class Face3Tests
{
    [Fact]
    public void ConcavePolygonIsRejectedInsteadOfSilentlyLosingCorners()
    {
        // FromGrid takes convexity on trust, and for years did so silently: the side search stopped at the first vertex
        // off the edge plane, so a polygon concave past that vertex produced a face whose plane held only the corners
        // before it. The bounded check turns that into an exception.
        var concave = new[]
        {
            new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(10, 10, 0),
            new Vec3(5, 2, 0), new Vec3(0, 10, 0),      // reflex vertex
        };
        Assert.Throws<ArgumentException>(() => Face3.FromGrid(concave));

        // Collinear vertices stay legal, and a convex polygon containing some is accepted with all its corners.
        var withCollinear = new[]
        {
            new Vec3(0, 0, 0), new Vec3(5, 0, 0), new Vec3(10, 0, 0), new Vec3(10, 10, 0), new Vec3(0, 10, 0),
        };
        Assert.Equal(5, Face3.FromGrid(withCollinear).Vertices.Length);

        // The everyday cases must of course pass.
        Assert.Equal(3, Face3.FromGrid([new Vec3(0, 0, 0), new Vec3(4, 0, 0), new Vec3(0, 4, 0)]).Vertices.Length);
        Assert.Equal(4, Face3.FromGrid([new Vec3(0, 0, 0), new Vec3(4, 0, 0), new Vec3(4, 4, 0), new Vec3(0, 4, 0)])
            .Vertices.Length);
    }
}