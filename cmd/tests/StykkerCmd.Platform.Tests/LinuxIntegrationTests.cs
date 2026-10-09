using StykkerCmd.Core.Abstractions;
using StykkerCmd.Core.Operations;
using StykkerCmd.Platform.Linux;
using Xunit;

namespace StykkerCmd.Platform.Tests;

// Linux-Integrationstests. Sie laufen nur unter Linux und werden auf Windows als übersprungen gemeldet.
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LinuxIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StykkerCmd-linux-" + Guid.NewGuid().ToString("N")[..8]);

    public LinuxIntegrationTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [SkippableFact]
    public void Names_that_differ_only_in_case_are_two_entries()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux-Test.");

        File.WriteAllText(Path.Combine(_root, "a.txt"), "klein");
        File.WriteAllText(Path.Combine(_root, "A.txt"), "gross");

        var names = new LinuxFileSystem().List(_root).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "A.txt", "a.txt" }, names);
    }

    [SkippableFact]
    public async Task Symlink_loop_terminates_and_is_reported()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux-Test.");

        var a = Path.Combine(_root, "a");
        Directory.CreateDirectory(a);
        File.WriteAllText(Path.Combine(a, "eins.txt"), "1");
        File.CreateSymbolicLink(Path.Combine(a, "schleife"), a);
        var b = Path.Combine(_root, "b");
        Directory.CreateDirectory(b);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await new OperationRunner(new LinuxFileSystem(), new LinuxTrash(Path.Combine(_root, "trash")))
            .RunAsync(new(OperationKind.Copy, [a], b), null, null, timeout.Token);

        Assert.False(timeout.IsCancellationRequested);
        Assert.Contains(result.Issues, i => i.Reason == IssueReason.Loop);
        Assert.Equal("1", File.ReadAllText(Path.Combine(b, "a", "eins.txt")));
    }

    [SkippableFact]
    public void Trash_moves_the_file_and_writes_trashinfo()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux-Test.");

        var trashHome = Path.Combine(_root, "trash");
        var file = Path.Combine(_root, "weg.txt");
        File.WriteAllText(file, "x");
        var trash = new LinuxTrash(trashHome);

        Assert.True(trash.IsAvailable(file));
        trash.MoveToTrash(file);

        Assert.False(File.Exists(file));
        Assert.True(File.Exists(Path.Combine(trashHome, "files", "weg.txt")));
        var info = File.ReadAllText(Path.Combine(trashHome, "info", "weg.txt.trashinfo"));
        Assert.Contains("[Trash Info]", info);
        Assert.Contains("Path=", info);
    }

    [SkippableFact]
    public void Open_file_names_this_process_as_holder()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux-Test.");

        var file = Path.Combine(_root, "offen.txt");
        File.WriteAllText(file, "x");
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read);

        var holders = new LinuxLockInspector().FindHolders(file);

        Assert.Contains(holders, h => h.ProcessId == Environment.ProcessId);
    }
}
