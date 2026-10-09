using ManifoldSharp;
using Stykker.NanoCut.Geometry3D;
using Xunit.Abstractions;

namespace Stykker.NanoCut.OracleTests;

/// <summary>
/// Phase 3 acceptance: volumes of NanoCut 3D Booleans agree with ManifoldSharp (exact rational "robust" engine)
/// on identical grid inputs. ManifoldSharp is a test-only dependency.
/// </summary>
public class ManifoldOracleTests(ITestOutputHelper output)
{
    [Fact]
    public void VolumesMatchManifoldOnRandomSolids()
    {
        var rng = new Random(77);
        double worst = 0;
        for (int i = 0; i < 300; i++)
        {
            var (va, ta) = RandomTetrahedron(rng);
            var (vb, tb) = rng.Next(2) == 0 ? RandomTetrahedron(rng) : RandomBox(rng);
            var na = Solid.FromTriangles(va, ta);
            var nb = Solid.FromTriangles(vb, tb);
            var ma = ToManifold(va, ta);
            var mb = ToManifold(vb, tb);
            Compare(i, "∩", (na & nb).VolumeMm3, ma.Intersection(mb).Volume() * 1e-18);
            Compare(i, "∪", (na | nb).VolumeMm3, ma.Union(mb).Volume() * 1e-18);
            Compare(i, "−", (na - nb).VolumeMm3, ma.Difference(mb).Volume() * 1e-18);
        }
        output.WriteLine($"worst |ΔV| = {worst:E3} mm³");

        void Compare(int i, string op, double nanoCut, double manifold)
        {
            double d = Math.Abs(nanoCut - manifold);
            worst = Math.Max(worst, d);
            Assert.True(d <= 1e-9 * Math.Max(1, Math.Abs(manifold)), $"case {i} {op}: NanoCut {nanoCut} mm³, Manifold {manifold} mm³");
        }
    }

    [Fact]
    public void Example1VolumeMatchesManifold()
    {
        // Same polyhedral inputs (grid vertices) in both kernels.
        var tol = Tolerance.Default;
        var stock = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(20, 20, 10), tol);
        var tool = Solid.Capsule(Vec3.Mm(-5, 10, 12), Vec3.Mm(25, 10, 12), 3, tol);
        double nanoCut = (stock - tool).VolumeMm3;
        var (sv, st) = ToIndexed(stock);
        var (tv, tt) = ToIndexed(tool);
        double manifold = ToManifold(sv, st).Difference(ToManifold(tv, tt)).Volume() * 1e-18;
        output.WriteLine($"NanoCut {nanoCut:F9} mm³, Manifold {manifold:F9} mm³");
        Assert.True(Math.Abs(nanoCut - manifold) < 1e-6, $"NanoCut {nanoCut}, Manifold {manifold}");
    }

    private static Manifold ToManifold(IReadOnlyList<Vec3> verts, IReadOnlyList<int> tris)
    {
        var mesh = new MeshGL64 { NumProp = 3 };
        foreach (var v in verts)
        {
            mesh.VertProperties.Add(v.X);
            mesh.VertProperties.Add(v.Y);
            mesh.VertProperties.Add(v.Z);
        }
        foreach (var t in tris) mesh.TriVerts.Add((ulong)t);
        var m = Manifold.FromMeshGL64(mesh);
        Assert.Equal(Error.NoError, m.Status());
        return m;
    }

    /// <summary>Indexed triangle mesh of a solid whose vertices are all grid points (primitives).</summary>
    private static (List<Vec3> Verts, List<int> Tris) ToIndexed(Solid s)
    {
        var index = new Dictionary<Vec3, int>();
        var verts = new List<Vec3>();
        var tris = new List<int>();
        int Id(Vec3 p)
        {
            if (!index.TryGetValue(p, out int k)) { k = verts.Count; verts.Add(p); index[p] = k; }
            return k;
        }
        foreach (var f in s.Faces)
        {
            var v = f.Vertices;
            for (int i = 1; i + 1 < v.Length; i++)
            {
                tris.Add(Id(v[0].Grid));
                tris.Add(Id(v[i].Grid));
                tris.Add(Id(v[i + 1].Grid));
            }
        }
        return (verts, tris);
    }

    private static (Vec3[], int[]) RandomTetrahedron(Random rng)
    {
        Vec3 P() => new(rng.NextInt64(-5_000_000, 5_000_000), rng.NextInt64(-5_000_000, 5_000_000), rng.NextInt64(-5_000_000, 5_000_000));
        Vec3 a = P(), b = P(), c = P(), d = P();
        int o = Predicates.Orient3D(a, b, c, d);
        if (o == 0) return RandomTetrahedron(rng);
        if (o > 0) (b, c) = (c, b);
        return ([a, b, c, d], [0, 1, 2, 0, 3, 1, 1, 3, 2, 2, 3, 0]);
    }

    private static (Vec3[], int[]) RandomBox(Random rng)
    {
        long x = rng.NextInt64(-5_000_000, 3_000_000), y = rng.NextInt64(-5_000_000, 3_000_000), z = rng.NextInt64(-5_000_000, 3_000_000);
        long x1 = x + rng.NextInt64(1, 4_000_000), y1 = y + rng.NextInt64(1, 4_000_000), z1 = z + rng.NextInt64(1, 4_000_000);
        Vec3[] v = [new(x, y, z), new(x1, y, z), new(x1, y1, z), new(x, y1, z), new(x, y, z1), new(x1, y, z1), new(x1, y1, z1), new(x, y1, z1)];
        int[] t = [0, 3, 2, 0, 2, 1, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7];
        return (v, t);
    }
}
