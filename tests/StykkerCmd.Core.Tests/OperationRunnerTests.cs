using StykkerCmd.Core.Abstractions;
using StykkerCmd.Core.Model;
using StykkerCmd.Core.Operations;
using StykkerCmd.Core.Tests.Support;
using Xunit;

namespace StykkerCmd.Core.Tests;

public class OperationRunnerTests
{
    private static (MemoryFileSystem Fs, MemoryTrash Trash, OperationRunner Runner) Create(bool caseSensitive = true)
    {
        var fs = new MemoryFileSystem(caseSensitive);
        var trash = new MemoryTrash(fs);
        return (fs, trash, new OperationRunner(fs, trash));
    }

    private static Task<OperationResult> RunAsync(
        OperationRunner runner,
        OperationRequest request,
        ConflictHandler? onConflict = null,
        CancellationToken ct = default)
        => runner.RunAsync(request, onConflict, progress: null, ct);

    private static ConflictHandler Answer(ConflictChoice choice, bool applyToAll = false)
        => _ => Task.FromResult(new ConflictAnswer(choice, applyToAll));

    [Fact]
    public async Task Copy_file_creates_identical_target_and_keeps_source()
    {
        var (fs, _, runner) = Create();
        fs.AddFile("/vol1/src/a.txt", "hallo welt");
        fs.AddDirectory("/vol1/dst");

        var result = await RunAsync(runner, new(OperationKind.Copy, ["/vol1/src/a.txt"], "/vol1/dst"));

        Assert.False(result.Cancelled);
        Assert.Empty(result.Issues);
        Assert.Equal("hallo welt", fs.ReadText("/vol1/dst/a.txt"));
        Assert.Equal("hallo welt", fs.ReadText("/vol1/src/a.txt"));
        Assert.Equal(1, result.FilesDone);
    }

    [Fact]
    public async Task Copy_directory_tree_copies_nested_content()
    {
        var (fs, _, runner) = Create();
        fs.AddFile("/vol1/src/tree/a.txt", "a");
        fs.AddFile("/vol1/src/tree/sub/deep/b.txt", "bb");
        fs.AddDirectory("/vol1/dst");

        var result = await RunAsync(runner, new(OperationKind.Copy, ["/vol1/src/tree"], "/vol1/dst"));

        Assert.Empty(result.Issues);
        Assert.Equal("a", fs.ReadText("/vol1/dst/tree/a.txt"));
        Assert.Equal("bb", fs.ReadText("/vol1/dst/tree/sub/deep/b.txt"));
        Assert.Equal(2, result.FilesDone);
        Assert.True(fs.Contains("/vol1/src/tree/sub/deep/b.txt"));
    }

    [Fact]
    public async Task Move_on_same_volume_removes_source()
    {
        var (fs, _, runner) = Create();
        fs.AddFile("/vol1/src/a.txt", "x");
        fs.AddDirectory("/vol1/dst");

        var result = await RunAsync(runner, new(OperationKind.Move, ["/vol1/src/a.txt"], "/vol1/dst"));

        Assert.Empty(result.Issues);
        Assert.Equal("x", fs.ReadText("/vol1/dst/a.txt"));
        Assert.False(fs.Contains("/vol1/src/a.txt"));
    }

    [Fact]
    public async Task Move_across_volumes_copies_then_removes_source()
    {
        var (fs, _, runner) = Create();
        fs.AddFile("/vol1/src/tree/a.txt", "data");
        fs.AddDirectory("/vol2/dst");

        var result = await RunAsync(runner, new(OperationKind.Move, ["/vol1/src/tree"], "/vol2/dst"));

        Assert.Empty(result.Issues);
        Assert.Equal("data", fs.ReadText("/vol2/dst/tree/a.txt"));
        Assert.False(fs.Contains("/vol1/src/tree"));
    }

    [Fact]
    public async Task Move_across_volumes_keeps_source_when_reading_fails()
    {
        var (fs, _, runner) = Create();
        fs.AddFile("/vol1/src/a.txt", "secret");
        fs.AddDirectory("/vol2/dst");
        fs.MarkUnreadable("/vol1/src/a.txt");

        var result = await RunAsync(runner, new(OperationKind.Move, ["/vol1/src/a.txt"], "/vol2/dst"));

        var issue = Assert.Single(result.Issues);
        Assert.Equal(IssueReason.AccessDenied, issue.Reason);
        Assert.Equal("secret", fs.ReadText("/vol1/src/a.txt"));
        Assert.False(fs.Contains("/vol2/dst/a.txt"));
    }

    [Fact]
    public async Task Cancel_keeps_source_intact_and_marks_partial_target()
    {
        var (fs, _, runner) = Create();
        var big = new byte[3 * 1024 * 1024];
        new Random(7).NextBytes(big);
        fs.AddFile("/vol1/src/big.bin", big);
        fs.AddDirectory("/vol1/dst");

        using var cts = new CancellationTokenSource();
        var progress = new CallbackProgress(p =>
        {
            if (p.BytesDone > 0)
                cts.Cancel();
        });

        var result = await runner.RunAsync(
            new(OperationKind.Copy, ["/vol1/src/big.bin"], "/vol1/dst"),
            onConflict: null,
            progress,
            cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(big, fs.ReadBytes("/vol1/src/big.bin"));
        Assert.False(fs.Contains("/vol1/dst/big.bin"));
        var partial = Assert.Single(result.PartialFiles);
        Assert.EndsWith(OperationRunner.PartialSuffix, partial);
        Assert.True(fs.Contains(partial));
    }

    [Fact]
    public async Task Conflict_skip_keeps_existing_target()
    {
        var (fs, _, runner) = Create();
        fs.AddFile("/vol1/src/a.txt", "neu");
        fs.AddFile("/vol1/dst/a.txt", "alt");
        var asked = 0;

        var result = await RunAsync(runner, new(OperationKind.Copy, ["/vol1/src/a.txt"], "/vol1/dst"),
            r =>
            {
                asked++;
                return Task.FromResult(new ConflictAnswer(ConflictChoice.Skip));
            });

        Assert.Equal(1, asked);
        Assert.Equal("alt", fs.ReadText("/vol1/dst/a.txt"));
        Assert.Equal(1, result.Skipped);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Conflict_overwrite_replaces_existing_file_without_leftovers()
    {
        var (fs, _, runner) = Create();
        fs.AddFile("/vol1/src/a.txt", "neu");
        fs.AddFile("/vol1/dst/a.txt", "alt");

        var result = await RunAsync(runner, new(OperationKind.Copy, ["/vol1/src/a.txt"], "/vol1/dst"),
            Answer(ConflictChoice.Overwrite));

        Assert.Empty(result.Issues);
        Assert.Equal("neu", fs.ReadText("/vol1/dst/a.txt"));
        Assert.False(fs.Contains("/vol1/dst/a.txt" + OperationRunner.PartialSuffix));
    }

    [Fact]
    public async Task Conflict_rename_creates_numbered_copy()
    {
        var (fs, _, runner) = Create();
        fs.AddFile("/vol1/src/a.txt", "neu");
        fs.AddFile("/vol1/dst/a.txt", "alt");

        await RunAsync(runner, new(OperationKind.Copy, ["/vol1/src/a.txt"], "/vol1/dst"), Answer(ConflictChoice.Rename));

        Assert.Equal("alt", fs.ReadText("/vol1/dst/a.txt"));
        Assert.Equal("neu", fs.ReadText("/vol1/dst/a (2).txt"));
    }

    [Fact]
    public async Task Apply_to_all_asks_only_once()
    {
        var (fs, _, runner) = Create();
        fs.AddFile("/vol1/src/a.txt", "1");
        fs.AddFile("/vol1/src/b.txt", "2");
        fs.AddFile("/vol1/dst/a.txt", "x");
        fs.AddFile("/vol1/dst/b.txt", "y");
        var asked = 0;

        await RunAsync(runner, new(OperationKind.Copy, ["/vol1/src/a.txt", "/vol1/src/b.txt"], "/vol1/dst"),
            _ =>
            {
                asked++;
                return Task.FromResult(new ConflictAnswer(ConflictChoice.Overwrite, ApplyToAll: true));
            });

        Assert.Equal(1, asked);
        Assert.Equal("1", fs.ReadText("/vol1/dst/a.txt"));
        Assert.Equal("2", fs.ReadText("/vol1/dst/b.txt"));
    }

    [Fact]
    public async Task Directory_cannot_overwrite_a_file_of_the_same_name()
    {
        var (fs, _, runner) = Create();
        fs.AddFile("/vol1/src/tree/a.txt", "a");
        fs.AddFile("/vol1/dst/tree", "eine Datei");
        ConflictRequest? seen = null;

        var result = await RunAsync(runner, new(OperationKind.Copy, ["/vol1/src/tree"], "/vol1/dst"),
            r =>
            {
                seen = r;
                return Task.FromResult(new ConflictAnswer(ConflictChoice.Overwrite));
            });

        Assert.NotNull(seen);
        Assert.False(seen!.CanOverwrite);
        Assert.Equal("eine Datei", fs.ReadText("/vol1/dst/tree"));
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task Directory_link_loop_is_reported_and_copy_terminates()
    {
        var (fs, _, runner) = Create();
        fs.AddFile("/vol1/src/tree/a.txt", "a");
        fs.AddLink("/vol1/src/tree/loop", "/vol1/src/tree"); // Link zurück auf den eigenen Ordner
        fs.AddDirectory("/vol1/dst");

        var result = await RunAsync(runner, new(OperationKind.Copy, ["/vol1/src/tree"], "/vol1/dst"));

        Assert.Equal("a", fs.ReadText("/vol1/dst/tree/a.txt"));
        Assert.Contains(result.Issues, i => i.Reason == IssueReason.Loop);
        Assert.False(fs.Contains("/vol1/dst/tree/loop/a.txt"));
    }

    [Fact]
    public async Task Copy_onto_same_location_is_reported_and_leaves_file_alone()
    {
        var (fs, _, runner) = Create();
        fs.AddFile("/vol1/src/a.txt", "x");

        var result = await RunAsync(runner, new(OperationKind.Copy, ["/vol1/src/a.txt"], "/vol1/src"));

        Assert.Equal(IssueReason.SameLocation, Assert.Single(result.Issues).Reason);
        Assert.Equal("x", fs.ReadText("/vol1/src/a.txt"));
    }

    [Fact]
    public async Task Copy_into_own_subfolder_is_rejected_before_anything_happens()
    {
        var (fs, _, runner) = Create();
        fs.AddDirectory("/vol1/src/tree/inner");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            RunAsync(runner, new(OperationKind.Copy, ["/vol1/src/tree"], "/vol1/src/tree/inner")));
        Assert.False(fs.Contains("/vol1/src/tree/inner/tree"));
    }

    [Fact]
    public async Task Delete_permanent_removes_tree_but_not_link_target()
    {
        var (fs, _, runner) = Create();
        fs.AddFile("/vol1/keep/target.txt", "t");
        fs.AddFile("/vol1/tree/a.txt", "a");
        fs.AddDirectory("/vol1/tree/sub");
        fs.AddLink("/vol1/tree/sub/shortcut", "/vol1/keep");

        var result = await RunAsync(runner, new(OperationKind.DeletePermanent, ["/vol1/tree"]));

        Assert.Empty(result.Issues);
        Assert.False(fs.Contains("/vol1/tree"));
        Assert.Equal("t", fs.ReadText("/vol1/keep/target.txt"));
    }

    [Fact]
    public async Task Delete_to_trash_moves_each_item_through_the_trash_service()
    {
        var (fs, trash, runner) = Create();
        fs.AddFile("/vol1/a.txt", "a");
        fs.AddFile("/vol1/b.txt", "b");

        var result = await RunAsync(runner, new(OperationKind.DeleteToTrash, ["/vol1/a.txt", "/vol1/b.txt"]));

        Assert.Empty(result.Issues);
        Assert.Equal(new[] { "/vol1/a.txt", "/vol1/b.txt" }, trash.Trashed);
        Assert.False(fs.Contains("/vol1/a.txt"));
    }

    [Fact]
    public async Task Delete_to_trash_without_trash_reports_issue_and_keeps_file()
    {
        var (fs, trash, runner) = Create();
        fs.AddFile("/vol1/a.txt", "a");
        trash.Available = false;

        var result = await RunAsync(runner, new(OperationKind.DeleteToTrash, ["/vol1/a.txt"]));

        Assert.Equal(IssueReason.NoTrash, Assert.Single(result.Issues).Reason);
        Assert.True(fs.Contains("/vol1/a.txt"));
    }

    [Fact]
    public async Task Create_directory_creates_new_folder_and_reports_existing_name()
    {
        var (fs, _, runner) = Create();
        fs.AddDirectory("/vol1/alt");

        var created = await RunAsync(runner, new(OperationKind.CreateDirectory, ["/vol1/neu"]));
        var clash = await RunAsync(runner, new(OperationKind.CreateDirectory, ["/vol1/alt"]));

        Assert.Empty(created.Issues);
        Assert.True(fs.DirectoryExists("/vol1/neu"));
        Assert.Equal(IssueReason.Exists, Assert.Single(clash.Issues).Reason);
    }

    [Fact]
    public async Task Run_leaves_copies_file_and_skips_when_target_exists()
    {
        var (fs, _, runner) = Create();
        fs.AddFile("/vol1/src/a.txt", "a");
        fs.AddDirectory("/vol1/dst");
        var leaves = new List<LeafOperation> { new(LeafKind.CopyFile, "/vol1/src/a.txt", "/vol1/dst/a.txt") };

        var first = await runner.RunLeavesAsync(leaves, progress: null, CancellationToken.None);
        var second = await runner.RunLeavesAsync(leaves, progress: null, CancellationToken.None);

        Assert.Empty(first.Issues);
        Assert.Empty(second.Issues);
        Assert.Equal("a", fs.ReadText("/vol1/dst/a.txt"));
    }

    [Fact]
    public async Task Case_sensitive_file_system_lists_both_names()
    {
        var (fs, _, _) = Create(caseSensitive: true);
        fs.AddFile("/vol1/a.txt", "klein");
        fs.AddFile("/vol1/A.txt", "gross");

        var names = fs.List("/vol1").Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "A.txt", "a.txt" }, names);
        await Task.CompletedTask;
    }
}
