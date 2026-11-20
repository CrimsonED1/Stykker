using StykkerMeshShare.Core;

namespace StykkerMeshShare.Tests;

// Freier Platz: unter der Grenze nimmt ein PC nichts an, gibt aber weiter und macht von selbst weiter.
public class DiskSpaceTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private sealed class FakeDisk
    {
        public long Free = 100 * Gb;
        public DiskSpace Measure(string _) => new("D:", Free, 1000 * Gb);
    }

    private static MeshNode Node(Mesh mesh, string name, FakeDisk disk) =>
        new(mesh.Options(name) with { MeasureDisk = disk.Measure, BadHashPause = TimeSpan.Zero });

    [Fact]
    public async Task BelowTheLimitNothingIsReceivedButDeletionsArrive()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var disk = new FakeDisk();
        var b = Node(mesh, "B", disk);
        Mesh.Write(a, "keep.txt", "k");
        a.Scan();
        await Mesh.Pull(b, a);
        Assert.Equal("k", Mesh.Read(b, "keep.txt"));

        disk.Free = 4 * Gb;   // unter 5 GB
        await Task.Delay(2100); // die Messung gilt 2 s
        Assert.True(b.LowSpace);
        Mesh.Write(a, "new.txt", "n");
        File.Delete(Path.Combine(a.Folder, "keep.txt"));
        a.Scan();
        var result = await b.PullAsync(new LocalPeer(a));
        Assert.False(result.Complete);
        Assert.Null(Mesh.Read(b, "new.txt"));    // nicht angenommen
        Assert.Null(Mesh.Read(b, "keep.txt"));   // die Löschung kam an

        disk.Free = 50 * Gb;
        await Task.Delay(2100);
        Assert.False(b.LowSpace);
        await Mesh.Pull(b, a);
        Assert.Equal("n", Mesh.Read(b, "new.txt"));   // von selbst weiter
        b.Dispose();
    }

    [Fact]
    public async Task LimitIsAdjustableAndZeroTurnsItOff()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var disk = new FakeDisk { Free = 4 * Gb };
        var b = Node(mesh, "B", disk);
        Assert.Equal(MeshNode.DefaultMinFreeBytes, b.MinFreeBytes);
        Assert.True(b.LowSpace);

        b.MinFreeBytes = 2 * Gb;
        Assert.False(b.LowSpace);
        b.MinFreeBytes = 0;
        Assert.False(b.LowSpace);

        Mesh.Write(a, "x.txt", "x");
        a.Scan();
        await Mesh.Pull(b, a);
        Assert.Equal("x", Mesh.Read(b, "x.txt"));
        b.Dispose();
    }

    [Fact]
    public async Task AFileThatWouldEatTheLimitWaits()
    {
        using var mesh = new Mesh();
        var a = mesh.Add("A");
        var disk = new FakeDisk { Free = 5 * Gb + 100 };   // knapp über der Grenze
        var b = Node(mesh, "B", disk);
        Mesh.Write(a, "big.txt", new string('x', 1000));   // danach wären weniger als 5 GB frei
        a.Scan();
        var result = await b.PullAsync(new LocalPeer(a));
        Assert.False(result.Complete);
        Assert.Null(Mesh.Read(b, "big.txt"));
        Assert.Contains(b.RecentActivity, x => x.Kind == ActivityKind.Error && x.Message!.StartsWith("Not enough free disk space"));
        b.Dispose();
    }
}
