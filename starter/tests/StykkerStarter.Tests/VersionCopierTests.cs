using StykkerStarter.Core;
using StykkerStarter.Core.Copy;
using StykkerStarter.Core.Source;

namespace StykkerStarter.Tests;

public class VersionCopierTests
{
    private sealed class Collect : IProgress<CopyProgress>
    {
        public List<CopyProgress> All { get; } = new();
        public void Report(CopyProgress value) => All.Add(value);
    }

    [Fact]
    public void Copy_puts_every_file_in_the_target_and_reports_a_checked_result()
    {
        using var temp = new TempFolder();
        var exe = SourceFixtures.CreateSource(temp, "source",
            ("data\\one.txt", "first"), ("data\\deep\\two.bin", new string('x', 200_000)));
        var source = SourceTree.Read(exe);
        var target = temp.Sub("copy\\0.1.0.0");

        var result = VersionCopier.Copy(source, target);

        Assert.True(Directory.Exists(target));
        Assert.False(Directory.Exists(target + DataLayout.PartialSuffix));
        Assert.Equal(source.Files.Count, result.Files.Count);
        Assert.Empty(VersionCopier.Verify(target, result.Files));
        Assert.Equal("first", File.ReadAllText(Path.Combine(target, "data", "one.txt")));
        Assert.Equal(source.TotalBytes, result.Bytes);
    }

    [Fact]
    public void Progress_ends_at_100_percent_with_the_right_byte_count()
    {
        using var temp = new TempFolder();
        var exe = SourceFixtures.CreateSource(temp, "source",
            ("a.bin", new string('a', 300_000)), ("b.bin", new string('b', 123_456)));
        var source = SourceTree.Read(exe);
        var progress = new Collect();

        VersionCopier.Copy(source, temp.Sub("target"), progress);

        var last = progress.All[^1];
        Assert.Equal(source.TotalBytes, last.BytesDone);
        Assert.Equal(source.TotalBytes, last.BytesTotal);
        Assert.Equal(100, last.Percent, 6);
        Assert.Equal(source.Files.Count, last.FilesDone);
        Assert.Equal(source.Files.Count, last.FilesTotal);
        // zwischendurch gab es Meldungen mit Teilständen
        Assert.Contains(progress.All, p => p.BytesDone > 0 && p.BytesDone < p.BytesTotal);
        Assert.True(progress.All.Select(p => p.BytesDone).SequenceEqual(progress.All.Select(p => p.BytesDone).Order()));
    }

    [Fact]
    public void Cancelled_copy_leaves_neither_partial_nor_finished_folder()
    {
        using var temp = new TempFolder();
        var exe = SourceFixtures.CreateSource(temp, "source",
            ("a.bin", new string('a', 400_000)), ("b.bin", new string('b', 400_000)), ("c.bin", new string('c', 400_000)));
        var source = SourceTree.Read(exe);
        var target = temp.Sub("target");
        using var cancel = new CancellationTokenSource();
        var progress = new CancelAfter(150_000, cancel);

        Assert.ThrowsAny<OperationCanceledException>(() => VersionCopier.Copy(source, target, progress, cancel.Token));

        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(target + DataLayout.PartialSuffix));
    }

    [Fact]
    public void Source_file_that_vanishes_during_the_copy_fails_and_leaves_nothing()
    {
        using var temp = new TempFolder();
        var exe = SourceFixtures.CreateSource(temp, "source", ("gone.bin", new string('g', 10_000)));
        var source = SourceTree.Read(exe);
        File.Delete(Path.Combine(source.Folder, "gone.bin"));
        var target = temp.Sub("target");

        Assert.ThrowsAny<CopyFailedException>(() => VersionCopier.Copy(source, target));

        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(target + DataLayout.PartialSuffix));
    }

    [Fact]
    public void Source_file_that_changes_size_during_the_copy_is_refused()
    {
        using var temp = new TempFolder();
        var exe = SourceFixtures.CreateSource(temp, "source", ("grow.bin", new string('g', 1_000)));
        var source = SourceTree.Read(exe);
        // Die Quelle wächst nach der Liste: die Kopie darf so nicht fertig werden
        File.WriteAllText(Path.Combine(source.Folder, "grow.bin"), new string('g', 2_000));
        var target = temp.Sub("target");

        Assert.ThrowsAny<CopyFailedException>(() => VersionCopier.Copy(source, target));

        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(target + DataLayout.PartialSuffix));
    }

    [Fact]
    public void Verify_finds_a_changed_file()
    {
        using var temp = new TempFolder();
        var exe = SourceFixtures.CreateSource(temp, "source", ("keep.txt", "same"), ("change.txt", "original"));
        var source = SourceTree.Read(exe);
        var target = temp.Sub("target");
        var result = VersionCopier.Copy(source, target);
        Assert.Empty(VersionCopier.Verify(target, result.Files));

        // Eine Datei in der Kopie wird verändert: gleiche Größe, anderer Inhalt
        File.WriteAllText(Path.Combine(target, "change.txt"), "modified");

        var problems = VersionCopier.Verify(target, result.Files);
        var problem = Assert.Single(problems);
        Assert.StartsWith("change.txt", problem);
    }

    [Fact]
    public void Existing_target_is_never_overwritten()
    {
        using var temp = new TempFolder();
        var exe = SourceFixtures.CreateSource(temp, "source", ("a.txt", "a"));
        var source = SourceTree.Read(exe);
        var target = temp.Sub("target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "mine");

        Assert.Throws<InvalidOperationException>(() => VersionCopier.Copy(source, target));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "keep.txt")));
    }

    // Bricht ab, sobald ein Teilstand die Grenze erreicht hat
    private sealed class CancelAfter(long bytes, CancellationTokenSource cancel) : IProgress<CopyProgress>
    {
        public void Report(CopyProgress value)
        {
            if (value.BytesDone >= bytes) cancel.Cancel();
        }
    }
}
