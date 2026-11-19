using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using StykkerMeshShare.Core;

namespace StykkerMeshShare.Tests;

// Die Funde aus den Prüfungen: Angriffe über fremde Einträge und Verknüpfungen, Dateien, die den Abgleich aufhielten,
// und die neuen Steuerungen (pausieren, nicht laden, Papierkorb).
public class HardeningTests
{
    private static MeshNode Node(Mesh mesh, string name) => new(mesh.Options(name) with { TransferChunkSize = 64 * 1024, BadHashPause = TimeSpan.Zero });

    // Eine Gegenstelle, die beliebige Einträge liefern kann.
    private sealed class FakePeer(string nodeId, IReadOnlyList<FileEntry> entries, Func<string, Stream?>? content = null) : IPeerSource
    {
        public int Opened;
        public Task<ChangePage> GetChangesAsync(long since, CancellationToken ct) =>
            Task.FromResult(new ChangePage(nodeId, "evil", 100, since >= 100 ? [] : entries));
        public Task<Stream?> OpenFileAsync(string path, string hash, long offset, CancellationToken ct)
        {
            Interlocked.Increment(ref Opened);
            return Task.FromResult(content?.Invoke(path));
        }
    }

    private static FileEntry Entry(string path, string? hash, long size = 4, long? ticks = null) => new()
    {
        Path = path, Hash = hash, Size = size, MtimeTicks = ticks ?? DateTime.UtcNow.AddHours(-1).Ticks,
        Version = new() { ["evil"] = 1 }, Seq = 1, OriginId = "evil", OriginName = "evil",
    };

    [Theory]
    [InlineData(@"..\..\..\x")]
    [InlineData(@"C:\Users\x\settings")]
    [InlineData("ABCDEF0000000000000000000000000000000000000000000000000000000000")]
    [InlineData("abc")]
    public async Task ForeignHashThatIsNoHashIsIgnored(string hash)
    {
        using var mesh = new Mesh();
        var b = Node(mesh, "B");
        var peer = new FakePeer("evil", [Entry("x.txt", hash)], _ => new MemoryStream("abcd"u8.ToArray()));
        var result = await b.PullAsync(peer);
        Assert.True(result.Complete);
        Assert.Equal(0, peer.Opened);
        Assert.False(File.Exists(Path.Combine(b.Folder, "x.txt")));
        Assert.False(PathRules.IsHash(hash));
    }

    [Fact]
    public async Task ImpossibleTimesAndSizesAreIgnored()
    {
        using var mesh = new Mesh();
        var b = Node(mesh, "B");
        var hash = new string('a', 64);
        var peer = new FakePeer("evil", [Entry("t.txt", hash, ticks: long.MaxValue), Entry("s.txt", hash, size: -5)]);
        Assert.True((await b.PullAsync(peer)).Complete);
        Assert.Equal(0, peer.Opened);
    }

    private static bool MakeJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        process.WaitForExit(10_000);
        return Directory.Exists(link);
    }

    [Fact]
    public async Task JunctionsInTheFolderLeadNowhere()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var mesh = new Mesh();
        var outside = Path.Combine(mesh.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");

        var b = Node(mesh, "B");
        Assert.True(MakeJunction(Path.Combine(b.Folder, "docs"), outside));

        // Lesen über die Junction: weder herunterladen noch an andere PCs ausliefern.
        Assert.Null(b.OpenForDownload("docs/secret.txt"));
        Assert.Null(PathRules.ToFullNoLinks(b.Folder, "docs/secret.txt"));
        Assert.NotNull(PathRules.ToFull(b.Folder, "docs/secret.txt"));   // der reine Textvergleich sähe es nicht

        // Schreiben über die Junction: eine Gegenstelle liefert docs/new.txt – es darf nicht draußen landen.
        var data = "evil"u8.ToArray();
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
        var peer = new FakePeer("evil", [Entry("docs/new.txt", hash, data.Length)], _ => new MemoryStream(data));
        await b.PullAsync(peer);
        Assert.False(File.Exists(Path.Combine(outside, "new.txt")));

        // Hochladen über die Junction ebenso nicht.
        Assert.Null(await b.AddFileAsync("docs/up.txt", new MemoryStream(data), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(outside, "up.txt")));
    }

    [Fact]
    public void HelperFilesCannotBeDownloaded()
    {
        using var mesh = new Mesh();
        var b = Node(mesh, "B");
        var dir = Path.Combine(b.Folder, ".stykker", "partial");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "x.json"), "{}");
        Assert.Null(b.OpenForDownload(".stykker/partial/x.json"));
    }

    [Theory]
    [InlineData(@"\\attacker\share\x")]
    [InlineData(@"\\?\C:\x")]
    [InlineData("//attacker/share")]
    public void NetworkPathsAreRefused(string path) =>
        Assert.Contains("Network", FolderRules.Check(path, @"C:\Shared", @"C:\Data") ?? "");

    [Fact]
    public async Task FileWithTimeInTheFutureIsSynced()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        Mesh.Write(a, "future.txt", "from 2099", new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        a.Scan();
        Assert.Single(a.Files);
        Assert.False(a.PendingRescan);
        await Mesh.Pull(b, a);
        Assert.Equal("from 2099", Mesh.Read(b, "future.txt"));
    }

    [Fact]
    public async Task OpenFileDoesNotBlockTheRest()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        Mesh.Write(a, "open.docx", "doc");
        Mesh.Write(a, "other.txt", "1");
        await mesh.Converge();

        // A löscht open.docx und ändert other.txt; bei B ist open.docx offen (gesperrt).
        File.Delete(Path.Combine(a.Folder, "open.docx"));
        Mesh.Write(a, "other.txt", "2");
        MeshNode.PullResult result;
        using (new FileStream(Path.Combine(b.Folder, "open.docx"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            a.Scan();
            result = await b.PullAsync(new LocalPeer(a));
        }
        Assert.False(result.Complete);                     // open.docx kommt später
        Assert.Equal("2", Mesh.Read(b, "other.txt"));      // der Rest kam trotzdem an
        await Mesh.Pull(b, a);
        Assert.Null(Mesh.Read(b, "open.docx"));
    }

    [Fact]
    public void SenderNoticesContentChangedWithoutNewTime()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var time = DateTime.UtcNow.AddMinutes(-5);
        Mesh.Write(a, "c.bin", "AAAA", time);
        a.Scan();
        var hash = a.Files.Single().Hash!;
        Mesh.Write(a, "c.bin", "BBBB", time);   // gleiche Größe, gleiche Zeit, anderer Inhalt

        using (var stream = a.OpenFile("c.bin", hash)!) stream.CopyTo(Stream.Null);
        Assert.Null(a.OpenFile("c.bin", hash));   // nicht noch einmal ausliefern
        Assert.True(a.PendingRescan);
        a.Scan();
        Assert.NotEqual(hash, a.Files.Single().Hash);
    }

    [Fact]
    public async Task BrokenContentIsNotLoadedAgainAtOnce()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = new MeshNode(mesh.Options("B") with { BadHashPause = TimeSpan.FromMinutes(10) });
        Mesh.Write(a, "x.bin", "real content");
        a.Scan();
        var hash = a.Files.Single().Hash!;
        var entry = a.GetChanges(0).Entries.Single();
        var bad = new FakePeer(a.NodeId, [entry], _ => new MemoryStream("fake content"u8.ToArray()));
        Assert.False((await b.PullAsync(bad)).Complete);
        Assert.Equal(1, bad.Opened);
        Assert.False((await b.PullAsync(bad)).Complete);
        Assert.Equal(1, bad.Opened);   // in der Pause: nicht noch einmal geladen
        Assert.Contains(b.RecentActivity, x => x.Kind == ActivityKind.Error && x.Message!.Contains("SHA-256"));
        Assert.True(PathRules.IsHash(hash));
    }

    [Fact]
    public async Task PauseStopsDownloadsButNotDeletions()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        Mesh.Write(a, "keep.txt", "k");
        await mesh.Converge();
        b.SetPaused(true);
        Mesh.Write(a, "new.txt", "n");
        File.Delete(Path.Combine(a.Folder, "keep.txt"));
        a.Scan();
        var result = await b.PullAsync(new LocalPeer(a));
        Assert.False(result.Complete);
        Assert.Null(Mesh.Read(b, "new.txt"));
        Assert.Null(Mesh.Read(b, "keep.txt"));   // die Löschung kam trotzdem an
        b.SetPaused(false);
        await Mesh.Pull(b, a);
        Assert.Equal("n", Mesh.Read(b, "new.txt"));
    }

    [Fact]
    public async Task SkippedFileComesWhenAllowedAgain()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        Mesh.Write(a, "big.iso", "huge");
        Mesh.Write(a, "small.txt", "s");
        a.Scan();
        b.Skip("big.iso", true);
        Assert.True((await b.PullAsync(new LocalPeer(a))).Complete);
        Assert.Null(Mesh.Read(b, "big.iso"));
        Assert.Equal("s", Mesh.Read(b, "small.txt"));
        Assert.Equal(["big.iso"], b.Skipped);

        b.Skip("big.iso", false);
        await Mesh.Pull(b, a);
        Assert.Equal("huge", Mesh.Read(b, "big.iso"));
    }

    [Fact]
    public async Task TrashCanBeRestored()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var b = mesh.Add("B");
        Mesh.Write(a, "notes/plan.txt", "v1");
        await mesh.Converge();
        File.Delete(Path.Combine(a.Folder, "notes", "plan.txt"));
        await mesh.Converge();
        Assert.Null(Mesh.Read(b, "notes/plan.txt"));

        var item = Assert.Single(b.TrashItems());
        Assert.Equal("notes/plan.txt", item.Path);
        Assert.Null(b.Restore(item.Id, out var name));
        Assert.Equal("notes/plan.txt", name);
        Assert.Equal("v1", Mesh.Read(b, "notes/plan.txt"));
        await mesh.Converge();
        Assert.Equal("v1", Mesh.Read(a, "notes/plan.txt"));   // zurückgeholt geht wieder an alle
        Assert.NotNull(b.Restore("../../x", out _));
    }

    [Fact]
    public void BetterRouteWins()
    {
        Assert.True(NetworkRoute.MustWait(PeerRoute.Tailscale, [PeerRoute.Lan]));
        Assert.True(NetworkRoute.MustWait(PeerRoute.Lan, [PeerRoute.Lan]));    // gleich gut: die erste bleibt
        Assert.False(NetworkRoute.MustWait(PeerRoute.Lan, [PeerRoute.Tailscale]));
        Assert.False(NetworkRoute.MustWait(PeerRoute.Lan, []));
    }
}
