using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Tests;

public class NcsFormatTests
{
    [Fact]
    public void RoundTripIsBitIdenticalAndKeepsWorking()
    {
        var tol = Tolerance.Budget(totalUm: 2.1, chordNm: 1000);
        var stock = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(20, 20, 10));
        var cut = stock - Solid.Capsule(Vec3.Mm(-5, 10, 12), Vec3.Mm(25, 13, 11), 3, tol);
        Assert.Contains(cut.Vertices, v => !v.IsGrid);

        using var ms = new MemoryStream();
        NcsFormat.Save(cut, ms);
        ms.Position = 0;
        var back = NcsFormat.Load(ms);

        Assert.Equal(cut.FaceCount, back.FaceCount);
        for (int i = 0; i < cut.FaceCount; i++)
        {
            var a = cut.Faces[i];
            var b = back.Faces[i];
            Assert.Equal(a.Support, b.Support);
            Assert.Equal(a.Edges, b.Edges);
            Assert.Equal(a.Vertices.Length, b.Vertices.Length);
            for (int k = 0; k < a.Vertices.Length; k++)
            {
                Assert.Equal(a.Vertices[k].IsGrid, b.Vertices[k].IsGrid);
                Assert.Equal(a.Vertices[k].Homogeneous, b.Vertices[k].Homogeneous);
            }
        }
        Assert.Equal(cut.VolumeMm3, back.VolumeMm3);

        // A loaded result can be cut further exactly like the original.
        var hole = Solid.Box(Vec3.Mm(2, 2, -1), Vec3.Mm(4, 4, 11));
        Assert.Equal((cut - hole).VolumeMm3, (back - hole).VolumeMm3, 9);
    }

    [Fact]
    public void RejectsForeignData()
    {
        using var ms = new MemoryStream("solid x"u8.ToArray());
        Assert.Throws<FormatException>(() => NcsFormat.Load(ms));
    }
}
