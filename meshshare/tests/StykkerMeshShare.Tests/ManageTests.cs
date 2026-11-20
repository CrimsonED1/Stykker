using System.Text;
using StykkerMeshShare.Core;

namespace StykkerMeshShare.Tests;

// Gezielt senden („von A nach B“), auf bestimmten PCs löschen, verschieben – von jedem PC aus. Siehe MeshNode.Manage.cs.
public class ManageTests
{
    private static async Task Upload(MeshNode node, string path, string content, IReadOnlyCollection<string> to, bool keepHere)
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(content));
        Assert.Equal(path, await node.AddFileAsync(path, body, CancellationToken.None, to, keepHere));
        // Die Datei muss alt genug sein, damit der Scan sie sofort nimmt (StableDelay ist im Test 0).
        File.SetLastWriteTimeUtc(Path.Combine(node.Folder, path.Replace('/', Path.DirectorySeparatorChar)), DateTime.UtcNow.AddMinutes(-1));
    }

    private static async Task<(Mesh Mesh, MeshNode A, MeshNode B, MeshNode C)> ThreePcs()
    {
        var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        var c = mesh.Add("C");
        await mesh.Converge();   // einmal abgleichen: jeder kennt jeden
        return (mesh, a, b, c);
    }

    [Fact]
    public async Task ASentFileOnlyReachesItsTarget()
    {
        var (mesh, a, b, c) = await ThreePcs();
        using var _ = mesh;
        await Upload(a, "film.mp4", "frames", [b.NodeId], keepHere: true);
        await mesh.Converge();

        Assert.Equal("frames", Mesh.Read(a, "film.mp4"));
        Assert.Equal("frames", Mesh.Read(b, "film.mp4"));
        Assert.Null(Mesh.Read(c, "film.mp4"));
        // C kennt die Datei (für die Liste), hat sie aber nicht.
        var known = Assert.Single(c.AllFiles);
        Assert.True(known.Away);
        Assert.Empty(c.Files);
        var view = Assert.Single(c.Overview().Files);
        Assert.Equal(new[] { a.NodeId, b.NodeId }.Order(), view.Holders.Order());
    }

    [Fact]
    public async Task RemoveHereWaitsUntilTheFileArrived()
    {
        var (mesh, a, b, c) = await ThreePcs();
        using var _ = mesh;
        await Upload(a, "photos/trip.jpg", "jpeg", [b.NodeId], keepHere: false);
        a.Scan();
        await Mesh.Pull(a, b);   // B hat sie noch nicht: A behält sie
        Assert.Equal("jpeg", Mesh.Read(a, "photos/trip.jpg"));

        await mesh.Converge();
        Assert.Equal("jpeg", Mesh.Read(b, "photos/trip.jpg"));
        Assert.Null(Mesh.Read(a, "photos/trip.jpg"));
        Assert.False(Directory.Exists(Path.Combine(a.Folder, "photos")));   // leerer Ordner weggeräumt
        Assert.Null(Mesh.Read(c, "photos/trip.jpg"));
        Assert.Contains(a.RecentActivity, x => x.Kind == ActivityKind.Sent && x.From == "B");
    }

    [Fact]
    public async Task DeletingOnOnePcKeepsItOnTheOthers()
    {
        var (mesh, a, b, c) = await ThreePcs();
        using var _ = mesh;
        Mesh.Write(a, "x.txt", "data");
        await mesh.Converge();
        Assert.Equal("data", Mesh.Read(c, "x.txt"));

        Assert.Null(a.DeleteOn(["x.txt"], [b.NodeId]));   // von A aus: nur auf B löschen
        await mesh.Converge();
        Assert.Null(Mesh.Read(b, "x.txt"));
        Assert.Equal("data", Mesh.Read(a, "x.txt"));
        Assert.Equal("data", Mesh.Read(c, "x.txt"));
        // B lädt sie nicht wieder.
        await mesh.Converge();
        Assert.Null(Mesh.Read(b, "x.txt"));
    }

    [Fact]
    public async Task DeletingEverywhereRemovesItForGood()
    {
        var (mesh, a, b, c) = await ThreePcs();
        using var _ = mesh;
        Mesh.Write(a, "x.txt", "data");
        await mesh.Converge();

        Assert.Null(c.DeleteOn(["x.txt"], [a.NodeId, b.NodeId, c.NodeId]));
        await mesh.Converge();
        foreach (var node in new[] { a, b, c })
        {
            Assert.Null(Mesh.Read(node, "x.txt"));
            Assert.Empty(node.AllFiles);
        }
    }

    [Fact]
    public async Task APcWithoutTheFileCanDeleteItElsewhere()
    {
        var (mesh, a, b, c) = await ThreePcs();
        using var _ = mesh;
        await Upload(a, "film.mp4", "frames", [b.NodeId], keepHere: false);
        await mesh.Converge();
        Assert.Equal("frames", Mesh.Read(b, "film.mp4"));

        // C (oder das Handy an C) löscht sie auf B: sie war nur dort, also ist sie überall weg.
        Assert.Null(c.DeleteOn(["film.mp4"], [b.NodeId]));
        await mesh.Converge();
        Assert.Null(Mesh.Read(b, "film.mp4"));
        Assert.Empty(b.AllFiles);
        Assert.Empty(a.AllFiles);
    }

    [Fact]
    public async Task SendingAnExistingFileToAnotherPc()
    {
        var (mesh, a, b, c) = await ThreePcs();
        using var _ = mesh;
        await Upload(a, "doc.pdf", "pdf", [b.NodeId], keepHere: false);
        await mesh.Converge();
        Assert.Null(Mesh.Read(c, "doc.pdf"));

        Assert.Null(b.Send(["doc.pdf"], [c.NodeId], removeHere: true));   // von B weiter an C, B gibt sie danach frei
        await mesh.Converge();
        Assert.Equal("pdf", Mesh.Read(c, "doc.pdf"));
        Assert.Null(Mesh.Read(b, "doc.pdf"));
        Assert.Null(Mesh.Read(a, "doc.pdf"));
    }

    [Fact]
    public async Task AMoveIsAMoveOnTheOtherPcs()
    {
        var (mesh, a, b, _) = await ThreePcs();
        using var __ = mesh;
        Mesh.Write(a, "x.txt", "data");
        await mesh.Converge();

        Assert.Null(a.Move([("x.txt", "archive/y.txt")]));
        await mesh.Converge();
        Assert.Null(Mesh.Read(b, "x.txt"));
        Assert.Equal("data", Mesh.Read(b, "archive/y.txt"));
        // B hat verschoben, nicht neu geladen.
        Assert.Contains(b.RecentActivity, x => x.Kind == ActivityKind.Moved && x.Path == "archive/y.txt");
        Assert.DoesNotContain(b.RecentActivity, x => x.Kind == ActivityKind.Received && x.Path == "archive/y.txt");
    }

    [Fact]
    public async Task MovingOntoAnExistingFileIsRefused()
    {
        var (mesh, a, _, _) = await ThreePcs();
        using var __ = mesh;
        Mesh.Write(a, "x.txt", "1");
        Mesh.Write(a, "y.txt", "2");
        a.Scan();
        Assert.NotNull(a.Move([("x.txt", "y.txt")]));
        Assert.NotNull(a.Move([("x.txt", "../outside.txt")]));
        Assert.Equal("1", Mesh.Read(a, "x.txt"));
    }

    [Fact]
    public async Task AnUploadDoesNotTakeTheNameOfAFileOnAnotherPc()
    {
        var (mesh, a, b, c) = await ThreePcs();
        using var _ = mesh;
        await Upload(a, "film.mp4", "frames", [b.NodeId], keepHere: true);
        await mesh.Converge();
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("other"));
        Assert.Equal("film (2).mp4", await c.AddFileAsync("film.mp4", body, CancellationToken.None));
    }
}
