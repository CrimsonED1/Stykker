using System.IO.Compression;
using System.Text;
using StykkerCmd.Core.Abstractions;
using StykkerCmd.Core.Operations;
using StykkerCmd.Core.Tests.Support;
using Xunit;

namespace StykkerCmd.Core.Tests;

public class ArchiveTests
{
    private static (MemoryFileSystem Fs, OperationRunner Runner) Create()
    {
        var fs = new MemoryFileSystem();
        return (fs, new OperationRunner(fs, new MemoryTrash(fs)));
    }

    private static Task<OperationResult> RunAsync(
        OperationRunner runner,
        OperationRequest request,
        ConflictHandler? onConflict = null)
        => runner.RunAsync(request, onConflict, progress: null, CancellationToken.None);

    private static ConflictHandler Answer(ConflictChoice choice)
        => _ => Task.FromResult(new ConflictAnswer(choice));

    // Baut ein ZIP im Speicher. Namen mit Schrägstrich am Ende sind Ordner ohne Inhalt.
    private static byte[] BuildZip(params (string Name, string Text)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                var entry = zip.CreateEntry(name);
                if (name.EndsWith('/'))
                    continue;

                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(text);
            }
        }
        return buffer.ToArray();
    }

    private static string[] EntryNames(MemoryFileSystem fs, string archive)
    {
        using var zip = new ZipArchive(fs.OpenRead(archive), ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    private static string ReadEntry(MemoryFileSystem fs, string archive, string name)
    {
        using var zip = new ZipArchive(fs.OpenRead(archive), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task Zip_packs_files_and_folders_into_one_archive()
    {
        var (fs, runner) = Create();
        fs.AddFile("/vol1/work/a.txt", "eins");
        fs.AddFile("/vol1/work/tree/b.txt", "zwei");
        fs.AddFile("/vol1/work/tree/sub/c.txt", "drei");

        var result = await RunAsync(runner,
            new(OperationKind.Zip, ["/vol1/work/a.txt", "/vol1/work/tree"], "/vol1/work", "paket.zip"));

        Assert.Empty(result.Issues);
        Assert.Equal(new[] { "a.txt", "tree/", "tree/b.txt", "tree/sub/", "tree/sub/c.txt" },
            EntryNames(fs, "/vol1/work/paket.zip"));
        Assert.Equal("drei", ReadEntry(fs, "/vol1/work/paket.zip", "tree/sub/c.txt"));
        Assert.False(fs.Contains("/vol1/work/paket.zip" + OperationRunner.PartialSuffix));
        Assert.Equal("eins", fs.ReadText("/vol1/work/a.txt"));
    }

    [Fact]
    public async Task Zip_skips_links_and_reports_them()
    {
        var (fs, runner) = Create();
        fs.AddFile("/vol1/work/tree/a.txt", "a");
        fs.AddLink("/vol1/work/tree/verweis", "/vol1/work/tree");

        var result = await RunAsync(runner,
            new(OperationKind.Zip, ["/vol1/work/tree"], "/vol1/work", "baum.zip"));

        Assert.Equal(new[] { "tree/", "tree/a.txt" }, EntryNames(fs, "/vol1/work/baum.zip"));
        Assert.Contains(result.Issues, i => i.Leaf.Source == "/vol1/work/tree/verweis");
    }

    [Fact]
    public async Task Zip_into_the_folder_being_packed_is_rejected()
    {
        var (fs, runner) = Create();
        fs.AddFile("/vol1/work/tree/a.txt", "x");

        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(runner,
            new(OperationKind.Zip, ["/vol1/work/tree"], "/vol1/work/tree", "innen.zip")));
        Assert.False(fs.Contains("/vol1/work/tree/innen.zip"));
    }

    [Fact]
    public async Task Unzip_extracts_into_a_folder_named_after_the_archive()
    {
        var (fs, runner) = Create();
        fs.AddFile("/vol1/work/paket.zip", BuildZip(("a.txt", "eins"), ("tree/", ""), ("tree/b.txt", "zwei")));
        fs.AddDirectory("/vol1/dst");

        var result = await RunAsync(runner, new(OperationKind.Unzip, ["/vol1/work/paket.zip"], "/vol1/dst"));

        Assert.Empty(result.Issues);
        Assert.Equal("eins", fs.ReadText("/vol1/dst/paket/a.txt"));
        Assert.Equal("zwei", fs.ReadText("/vol1/dst/paket/tree/b.txt"));
        Assert.True(fs.DirectoryExists("/vol1/dst/paket/tree"));
        Assert.Equal(2, result.FilesDone);
    }

    [Fact]
    public async Task Unzip_refuses_entries_that_leave_the_folder()
    {
        var (fs, runner) = Create();
        fs.AddFile("/vol1/work/evil.zip", BuildZip(("../escape.txt", "boese"), ("ok.txt", "gut")));
        fs.AddDirectory("/vol1/dst");

        var result = await RunAsync(runner, new(OperationKind.Unzip, ["/vol1/work/evil.zip"], "/vol1/dst"));

        Assert.Equal("gut", fs.ReadText("/vol1/dst/evil/ok.txt"));
        Assert.False(fs.Contains("/vol1/dst/escape.txt"));
        Assert.False(fs.Contains("/vol1/work/escape.txt"));
        Assert.Contains(result.Issues, i => i.Reason == IssueReason.Other && i.Message.Contains("hinaus"));
    }

    [Fact]
    public async Task Unzip_conflict_overwrite_replaces_the_existing_file()
    {
        var (fs, runner) = Create();
        fs.AddFile("/vol1/work/paket.zip", BuildZip(("a.txt", "neu")));
        fs.AddFile("/vol1/dst/paket/a.txt", "alt");

        var result = await RunAsync(runner,
            new(OperationKind.Unzip, ["/vol1/work/paket.zip"], "/vol1/dst"),
            Answer(ConflictChoice.Overwrite));

        Assert.Empty(result.Issues);
        Assert.Equal("neu", fs.ReadText("/vol1/dst/paket/a.txt"));
        Assert.False(fs.Contains("/vol1/dst/paket/a.txt" + OperationRunner.PartialSuffix));
    }

    [Fact]
    public async Task Unzip_conflict_skip_keeps_the_existing_file()
    {
        var (fs, runner) = Create();
        fs.AddFile("/vol1/work/paket.zip", BuildZip(("a.txt", "neu")));
        fs.AddFile("/vol1/dst/paket/a.txt", "alt");

        var result = await RunAsync(runner,
            new(OperationKind.Unzip, ["/vol1/work/paket.zip"], "/vol1/dst"),
            Answer(ConflictChoice.Skip));

        Assert.Equal("alt", fs.ReadText("/vol1/dst/paket/a.txt"));
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task Unzip_reports_a_file_that_is_not_an_archive_and_creates_nothing()
    {
        var (fs, runner) = Create();
        fs.AddFile("/vol1/work/kaputt.zip", "das ist kein zip");
        fs.AddDirectory("/vol1/dst");

        var result = await RunAsync(runner, new(OperationKind.Unzip, ["/vol1/work/kaputt.zip"], "/vol1/dst"));

        Assert.Equal(IssueReason.Other, Assert.Single(result.Issues).Reason);
        Assert.False(fs.DirectoryExists("/vol1/dst/kaputt"));
    }

    [Fact]
    public async Task Zip_archive_name_with_separator_is_rejected()
    {
        var (fs, runner) = Create();
        fs.AddFile("/vol1/work/a.txt", "a");

        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(runner,
            new(OperationKind.Zip, ["/vol1/work/a.txt"], "/vol1/work", "unter/ordner.zip")));
    }
}
