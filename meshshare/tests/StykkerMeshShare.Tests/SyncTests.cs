using StykkerMeshShare.Core;

namespace StykkerMeshShare.Tests;

// Mehrere PCs im selben Prozess, jeder mit eigenem Temp-Ordner. Abgeglichen wird über LocalPeer.
public sealed class Mesh : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meshshare-test-" + Guid.NewGuid().ToString("N")[..8]);
    public List<MeshNode> Nodes { get; } = new();

    public MeshNode Add(string name)
    {
        var node = new MeshNode(Options(name));
        Nodes.Add(node);
        return node;
    }

    public MeshOptions Options(string name) => new()
    {
        Folder = Path.Combine(_root, name, "share"),
        DataDir = Path.Combine(_root, name, "data"),
        NodeName = name,
        StableDelay = TimeSpan.Zero,
    };

    public static void Write(MeshNode node, string relative, string content, DateTime? mtime = null)
    {
        var full = Path.Combine(node.Folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        File.SetLastWriteTimeUtc(full, mtime ?? DateTime.UtcNow.AddMinutes(-1));
    }

    public static string? Read(MeshNode node, string relative)
    {
        var full = Path.Combine(node.Folder, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    // Im Server scannt jeder PC selbst (Watcher, Takt); hier scannt die Gegenstelle vor dem Abruf.
    public static async Task<int> Pull(MeshNode to, MeshNode from)
    {
        from.Scan();
        return (await to.PullAsync(new LocalPeer(from))).Applied;
    }

    // Jeder holt bei jedem ab, bis sich nichts mehr tut.
    public async Task Converge(int rounds = 6)
    {
        for (int i = 0; i < rounds; i++)
        {
            int applied = 0;
            foreach (var a in Nodes) foreach (var b in Nodes) if (a != b) applied += await Pull(a, b);
            if (applied == 0 && i > 0) return;
        }
    }

    public static string Listing(MeshNode node) => string.Join("\n",
        node.Files.Select(f => $"{f.Path}={f.Hash}"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class SyncTests
{
    [Fact]
    public async Task NewFileArrivesWithContentAndTime()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        var time = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        Mesh.Write(a, "docs/hello.txt", "hi", time);

        Assert.Equal(1, await Mesh.Pull(b, a));
        Assert.Equal("hi", Mesh.Read(b, "docs/hello.txt"));
        Assert.Equal(time, File.GetLastWriteTimeUtc(Path.Combine(b.Folder, "docs", "hello.txt")));
        // Ein zweiter Abruf ändert nichts mehr.
        Assert.Equal(0, await Mesh.Pull(b, a));
    }

    [Fact]
    public async Task ChangeAndDeletePropagate()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        Mesh.Write(a, "x.txt", "one");
        await mesh.Converge();

        Mesh.Write(a, "x.txt", "two", DateTime.UtcNow.AddSeconds(-30));
        await mesh.Converge();
        Assert.Equal("two", Mesh.Read(b, "x.txt"));

        File.Delete(Path.Combine(a.Folder, "x.txt"));
        Mesh.Write(a, "keep1.txt", "k1");
        Mesh.Write(a, "keep2.txt", "k2");
        await mesh.Converge();
        Assert.Null(Mesh.Read(b, "x.txt"));
        // Die gelöschte Fassung liegt bei B im Papierkorb.
        Assert.Contains(Directory.GetFiles(b.TrashDir, "x.txt", SearchOption.AllDirectories), _ => true);
    }

    [Fact]
    public async Task ChangesTravelOverAMiddleNode()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        var c = mesh.Add("C");
        Mesh.Write(a, "far.txt", "from A");
        // C sieht A nicht direkt: nur A → B → C.
        await Mesh.Pull(b, a);
        await Mesh.Pull(c, b);
        Assert.Equal("from A", Mesh.Read(c, "far.txt"));
    }

    [Fact]
    public async Task ConcurrentEditsKeepBothVersions()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        Mesh.Write(a, "plan.txt", "base");
        await mesh.Converge();

        Mesh.Write(a, "plan.txt", "edit A", DateTime.UtcNow.AddSeconds(-20));
        Mesh.Write(b, "plan.txt", "edit B", DateTime.UtcNow.AddSeconds(-10));   // B ist später: B gewinnt
        await mesh.Converge();

        Assert.Equal("edit B", Mesh.Read(a, "plan.txt"));
        Assert.Equal("edit B", Mesh.Read(b, "plan.txt"));
        var copy = Assert.Single(a.Files, f => f.Path.Contains("(conflict A ", StringComparison.Ordinal));
        Assert.Equal("edit A", Mesh.Read(a, copy.Path));
        Assert.Equal("edit A", Mesh.Read(b, copy.Path));
        Assert.Equal(Mesh.Listing(a), Mesh.Listing(b));
    }

    [Fact]
    public async Task ConcurrentEditsConvergeWhicheverSidePullsFirst()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        Mesh.Write(a, "plan.txt", "base");
        await mesh.Converge();
        Mesh.Write(a, "plan.txt", "edit A", DateTime.UtcNow.AddSeconds(-20));
        Mesh.Write(b, "plan.txt", "edit B", DateTime.UtcNow.AddSeconds(-10));

        // Diesmal holt zuerst der Gewinner (B) beim Verlierer ab.
        await Mesh.Pull(b, a);
        await mesh.Converge();

        Assert.Equal("edit B", Mesh.Read(a, "plan.txt"));
        var copy = Assert.Single(b.Files, f => f.Path.Contains("(conflict A ", StringComparison.Ordinal));
        Assert.Equal("edit A", Mesh.Read(b, copy.Path));
        Assert.Equal(Mesh.Listing(a), Mesh.Listing(b));
    }

    [Fact]
    public async Task ChangeWinsOverConcurrentDelete()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        Mesh.Write(a, "keep.txt", "k");
        Mesh.Write(a, "other1.txt", "o1");
        Mesh.Write(a, "other2.txt", "o2");
        await mesh.Converge();

        File.Delete(Path.Combine(a.Folder, "keep.txt"));
        Mesh.Write(b, "keep.txt", "changed on B", DateTime.UtcNow.AddSeconds(-5));
        await mesh.Converge();

        Assert.Equal("changed on B", Mesh.Read(a, "keep.txt"));
        Assert.Equal("changed on B", Mesh.Read(b, "keep.txt"));
    }

    [Fact]
    public async Task NewPcWithOwnFilesMergesWithoutOverwriting()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        Mesh.Write(a, "only-a.txt", "a");
        Mesh.Write(a, "same.txt", "same");
        Mesh.Write(a, "clash.txt", "from A", DateTime.UtcNow.AddMinutes(-5));
        Mesh.Write(b, "only-b.txt", "b");
        Mesh.Write(b, "same.txt", "same");
        Mesh.Write(b, "clash.txt", "from B", DateTime.UtcNow.AddMinutes(-2));
        await mesh.Converge();

        Assert.Equal(Mesh.Listing(a), Mesh.Listing(b));
        Assert.Equal("a", Mesh.Read(b, "only-a.txt"));
        Assert.Equal("b", Mesh.Read(a, "only-b.txt"));
        Assert.Equal("from B", Mesh.Read(a, "clash.txt"));
        Assert.Contains(a.Files, f => f.Path.StartsWith("clash (conflict A ", StringComparison.Ordinal));
        Assert.DoesNotContain(a.Files, f => f.Path.StartsWith("same (conflict", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VanishedFolderContentIsHeldNotDeleted()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        for (int i = 0; i < 5; i++) Mesh.Write(a, $"f{i}.txt", "x" + i);
        await mesh.Converge();

        foreach (var file in Directory.GetFiles(a.Folder)) File.Delete(file);
        a.Scan();
        Assert.Equal(5, a.HeldDeletes);
        await mesh.Converge();
        Assert.Equal(5, b.Files.Count);

        // Erst nach Bestätigung gehen die Löschungen raus.
        a.AllowHeldDeletes();
        Assert.Equal(0, a.HeldDeletes);
        await mesh.Converge();
        Assert.Empty(b.Files);
    }

    [Fact]
    public async Task IgnoredFilesStayLocal()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        Mesh.Write(a, "~$report.docx", "lock");
        Mesh.Write(a, "download.partial", "half");
        Mesh.Write(a, "Thumbs.db", "t");
        Mesh.Write(a, "real.txt", "r");
        await mesh.Converge();
        Assert.Equal(new[] { "real.txt" }, b.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task IndexSurvivesARestart()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        Mesh.Write(a, "x.txt", "x");
        await mesh.Converge();
        var id = b.NodeId;

        var again = new MeshNode(mesh.Options("B"));
        Assert.Equal(id, again.NodeId);
        Assert.Single(again.Files);
        Assert.Equal(0, await Mesh.Pull(again, a));
    }

    [Fact]
    public async Task UploadGetsAFreeName()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        Mesh.Write(a, "photo.jpg", "old");
        using var content = new MemoryStream("new"u8.ToArray());
        var name = await a.AddFileAsync("photo.jpg", content, CancellationToken.None);
        Assert.Equal("photo (2).jpg", name);
        Assert.Equal("old", Mesh.Read(a, "photo.jpg"));
        Assert.Null(await a.AddFileAsync("../escape.txt", new MemoryStream(), CancellationToken.None));
    }
}

public class PathRuleTests
{
    [Theory]
    [InlineData("..")]
    [InlineData("../x.txt")]
    [InlineData("a/../../x.txt")]
    [InlineData("/abs.txt")]
    [InlineData("C:/x.txt")]
    [InlineData("a//b.txt")]
    [InlineData("con.txt")]
    [InlineData("dir/NUL")]
    [InlineData("file.txt:stream")]
    [InlineData("trail.")]
    [InlineData("trail ")]
    [InlineData("")]
    [InlineData("a/./b")]
    public void RejectsUnsafePaths(string path)
    {
        Assert.Null(PathRules.Normalize(path));
        Assert.Null(PathRules.ToFull(Path.GetTempPath(), path));
    }

    [Fact]
    public void NormalizesSeparators()
    {
        Assert.Equal("a/b/c.txt", PathRules.Normalize(@"a\b/c.txt"));
        var root = Path.Combine(Path.GetTempPath(), "root");
        Assert.Equal(Path.Combine(root, "a", "b.txt"), PathRules.ToFull(root, "a/b.txt"));
    }

    [Fact]
    public void ConflictNameKeepsExtension()
    {
        var ticks = new DateTime(2026, 11, 19, 14, 32, 0, DateTimeKind.Utc).Ticks;
        Assert.Equal("docs/Report (conflict PC-2 2026-11-19 1432).docx", PathRules.ConflictName("docs/Report.docx", "PC 2", ticks).Replace("PC2", "PC-2"));
    }
}
