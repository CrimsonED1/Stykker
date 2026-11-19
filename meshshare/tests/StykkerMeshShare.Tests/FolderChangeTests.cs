using StykkerMeshShare.Core;

namespace StykkerMeshShare.Tests;

public class FolderChangeTests
{
    [Fact]
    public async Task MovingKeepsFilesAndTellsNobody()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        var time = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
        Mesh.Write(a, "docs/one.txt", "1", time);
        Mesh.Write(a, "two.txt", "2");
        await mesh.Converge();
        var seqB = b.Seq;

        var target = Path.Combine(mesh.Root, "A", "moved");
        Assert.Null(a.ChangeFolder(target, move: true));

        Assert.Equal(target, a.Folder);
        Assert.Equal("1", File.ReadAllText(Path.Combine(target, "docs", "one.txt")));
        Assert.Equal(time, File.GetLastWriteTimeUtc(Path.Combine(target, "docs", "one.txt")));
        Assert.False(Directory.Exists(Path.Combine(mesh.Root, "A", "share")));
        Assert.Equal(0, a.HeldDeletes);
        await mesh.Converge();
        // B hat nichts mitbekommen: keine Löschung, keine neue Fassung.
        Assert.Equal(2, b.Files.Count);
        Assert.Equal(seqB, b.Seq);
    }

    [Fact]
    public async Task StartingEmptyFetchesEverythingAndDeletesNothing()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        Mesh.Write(a, "one.txt", "1");
        Mesh.Write(a, "sub/two.txt", "2");
        Mesh.Write(a, "three.txt", "3");
        await mesh.Converge();

        var old = a.Folder;
        var target = Path.Combine(mesh.Root, "A", "fresh");
        Assert.Null(a.ChangeFolder(target, move: false));
        await mesh.Converge();

        Assert.Equal("2", File.ReadAllText(Path.Combine(target, "sub", "two.txt")));
        Assert.Equal(3, a.Files.Count);
        Assert.Equal(3, b.Files.Count);
        Assert.True(File.Exists(Path.Combine(old, "one.txt")));   // der alte Ordner bleibt
        Assert.Equal(Mesh.Listing(a), Mesh.Listing(b));
    }

    [Fact]
    public void RefusesANonEmptyTarget()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var target = Path.Combine(mesh.Root, "A", "full");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "x.txt"), "x");
        Assert.NotNull(a.ChangeFolder(target, move: true));
        Assert.NotEqual(target, a.Folder);
    }

    [Fact]
    public void RefusesDangerousFolders()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var bad in new[]
        {
            "relative/path",
            Path.GetPathRoot(a.Folder)!,
            profile,
            Path.Combine(windows, "Temp", "x"),
            Path.Combine(a.DataDir, "inside"),
            Path.Combine(a.Folder, "inside"),
            a.Folder,
        })
            Assert.NotNull(FolderRules.Check(bad, a.Folder, a.DataDir));
        Assert.Null(FolderRules.Check(Path.Combine(mesh.Root, "A", "ok"), a.Folder, a.DataDir));
    }
}
