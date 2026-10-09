using System.Diagnostics;
using StykkerCmd.Core.Abstractions;
using StykkerCmd.Core.Elevation;
using StykkerCmd.Core.Operations;
using StykkerCmd.Platform.Windows;
using Xunit;

namespace StykkerCmd.Platform.Tests;

// Echte Dateioperationen auf Windows. Jeder Test arbeitet in einem eigenen Ordner unter %TEMP% und räumt ihn wieder auf.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StykkerCmd-" + Guid.NewGuid().ToString("N")[..8]);

    public WindowsIntegrationTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        RemoveTree(@"\\?\" + _root);
    }

    [SkippableFact]
    public async Task Path_longer_than_300_characters_is_listed_and_written()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-Test.");

        var fs = new WindowsFileSystem();
        var deep = _root;
        for (int i = 0; i < 6; i++)
            deep = Path.Combine(deep, $"langer-ordner-{i}-" + new string('x', 40));
        Assert.True(deep.Length > 300, $"Pfad nur {deep.Length} Zeichen lang.");

        fs.CreateDirectory(deep);
        var source = Path.Combine(_root, "quelle.txt");
        File.WriteAllText(source, "lange Pfade");

        var result = await new OperationRunner(fs, new WindowsTrash())
            .RunAsync(new(OperationKind.Copy, [source], deep), null, null, CancellationToken.None);

        Assert.Empty(result.Issues);
        Assert.Equal("lange Pfade", File.ReadAllText(@"\\?\" + Path.Combine(deep, "quelle.txt")));
        Assert.Contains(fs.List(deep), e => e.Name == "quelle.txt");
    }

    [SkippableFact]
    public async Task Junction_loop_terminates_and_is_reported()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-Test.");

        var a = Path.Combine(_root, "a");
        Directory.CreateDirectory(a);
        File.WriteAllText(Path.Combine(a, "eins.txt"), "1");
        Run("cmd.exe", $"/c mklink /J \"{Path.Combine(a, "schleife")}\" \"{a}\""); // Junction braucht keine Adminrechte
        Assert.True(Directory.Exists(Path.Combine(a, "schleife")));

        var b = Path.Combine(_root, "b");
        Directory.CreateDirectory(b);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await new OperationRunner(new WindowsFileSystem(), new WindowsTrash())
            .RunAsync(new(OperationKind.Copy, [a], b), null, null, timeout.Token);

        Assert.False(timeout.IsCancellationRequested, "Der Kopiervorgang lief in die Schleife.");
        Assert.Contains(result.Issues, i => i.Reason == IssueReason.Loop);
        Assert.Equal("1", File.ReadAllText(Path.Combine(b, "a", "eins.txt")));
        Assert.False(Directory.Exists(Path.Combine(b, "a", "schleife")));
    }

    [SkippableFact]
    public void Junction_has_the_same_identity_as_its_target()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-Test.");

        var a = Path.Combine(_root, "ident");
        Directory.CreateDirectory(a);
        var link = Path.Combine(a, "link");
        Run("cmd.exe", $"/c mklink /J \"{link}\" \"{a}\"");

        var fs = new WindowsFileSystem();
        Assert.Equal(fs.IdentityOf(a), fs.IdentityOf(link));
        Assert.True(fs.IsLink(link));
        Assert.False(fs.IsLink(a));
    }

    [SkippableFact]
    public void Open_file_names_this_process_as_holder()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-Test.");

        var file = Path.Combine(_root, "gesperrt.txt");
        File.WriteAllText(file, "x");
        using var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var holders = new WindowsLockInspector().FindHolders(file);

        Assert.Contains(holders, h => h.ProcessId == Environment.ProcessId);
    }

    [SkippableFact]
    public async Task Locked_source_is_reported_as_locked_and_survives()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-Test.");

        var source = Path.Combine(_root, "gesperrt.txt");
        File.WriteAllText(source, "x");
        var target = Path.Combine(_root, "ziel");
        Directory.CreateDirectory(target);
        using var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await new OperationRunner(new WindowsFileSystem(), new WindowsTrash())
            .RunAsync(new(OperationKind.Copy, [source], target), null, null, CancellationToken.None);

        Assert.Equal(IssueReason.Locked, Assert.Single(result.Issues).Reason);
        Assert.True(File.Exists(source));
    }

    // Legt eine Datei im echten Papierkorb ab; deshalb nur mit STYKKER_PAPIERKORB_TEST=1.
    [SkippableFact]
    public void Trash_removes_the_file_from_its_place()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-Test.");
        Skip.IfNot(Environment.GetEnvironmentVariable("STYKKER_PAPIERKORB_TEST") == "1", "Nur mit STYKKER_PAPIERKORB_TEST=1.");

        var file = Path.Combine(_root, "papierkorb-test.txt");
        File.WriteAllText(file, "weg damit");
        var trash = new WindowsTrash();

        Assert.True(trash.IsAvailable(file));
        trash.MoveToTrash(file);

        Assert.False(File.Exists(file));
    }

    [SkippableFact]
    public void Elevated_helper_copies_a_file_when_run_directly()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-Test.");

        var source = Path.Combine(_root, "helfer.txt");
        File.WriteAllText(source, "helfer");
        var targetDir = Path.Combine(_root, "helferziel");
        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, "helfer.txt");

        var encoded = ElevatedHelper.Encode([new LeafOperation(LeafKind.CopyFile, source, target)]);
        var exitCode = ElevatedHelper.Run(encoded, WindowsPlatform.Create());

        Assert.Equal(ElevatedHelper.ExitOk, exitCode);
        Assert.Equal("helfer", File.ReadAllText(target));
    }

    [SkippableFact]
    public async Task Move_to_second_volume_copies_verifies_and_removes_source()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-Test.");

        var other = @"S:\StykkerCmd-Test-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.CreateDirectory(other);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            Skip.If(true, $"Zweiter Datenträger S: nicht beschreibbar: {e.Message}");
        }

        try
        {
            var source = Path.Combine(_root, "wandert.bin");
            var content = new byte[5 * 1024 * 1024];
            new Random(11).NextBytes(content);
            File.WriteAllBytes(source, content);

            var result = await new OperationRunner(new WindowsFileSystem(), new WindowsTrash())
                .RunAsync(new(OperationKind.Move, [source], other), null, null, CancellationToken.None);

            Assert.Empty(result.Issues);
            Assert.False(File.Exists(source));
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(other, "wandert.bin")));
        }
        finally
        {
            RemoveTree(@"\\?\" + other);
        }
    }

    [SkippableFact]
    public async Task Ten_thousand_small_files_copy_completely()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-Test.");

        var source = Path.Combine(_root, "viele");
        Directory.CreateDirectory(source);
        for (int i = 0; i < 10_000; i++)
            File.WriteAllText(Path.Combine(source, $"datei-{i:D5}.txt"), $"inhalt {i}");
        var target = Path.Combine(_root, "viele-ziel");
        Directory.CreateDirectory(target);

        var clock = Stopwatch.StartNew();
        var result = await new OperationRunner(new WindowsFileSystem(), new WindowsTrash())
            .RunAsync(new(OperationKind.Copy, [source], target), null, null, CancellationToken.None);
        clock.Stop();

        Assert.Empty(result.Issues);
        Assert.Equal(10_000, result.FilesDone);
        Assert.Equal(10_000, Directory.GetFiles(Path.Combine(target, "viele")).Length);
        Assert.True(clock.Elapsed < TimeSpan.FromMinutes(5), $"Dauer: {clock.Elapsed}");
    }

    // Groß und langsam: nur mit STYKKER_GROSSTEST=1. Prüft Abbruch mitten in einer 2-GB-Datei.
    [SkippableFact]
    public async Task Two_gigabyte_copy_cancelled_midway_keeps_source_complete()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-Test.");
        Skip.IfNot(Environment.GetEnvironmentVariable("STYKKER_GROSSTEST") == "1", "Nur mit STYKKER_GROSSTEST=1.");

        const long size = 2L * 1024 * 1024 * 1024;
        var source = Path.Combine(_root, "gross.bin");
        var block = new byte[1024 * 1024];
        new Random(3).NextBytes(block);
        using (var writer = new FileStream(source, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            for (long written = 0; written < size; written += block.Length)
                writer.Write(block, 0, block.Length);
        }
        var target = Path.Combine(_root, "ziel-gross");
        Directory.CreateDirectory(target);

        using var cts = new CancellationTokenSource();
        var progress = new CallbackProgress(p =>
        {
            if (p.BytesDone > 512L * 1024 * 1024)
                cts.Cancel();
        });

        var result = await new OperationRunner(new WindowsFileSystem(), new WindowsTrash())
            .RunAsync(new(OperationKind.Copy, [source], target), null, progress, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(size, new FileInfo(source).Length);
        Assert.False(File.Exists(Path.Combine(target, "gross.bin")));
        var partial = Assert.Single(result.PartialFiles);
        Assert.True(File.Exists(partial));
        Assert.True(new FileInfo(partial).Length < size);
    }

    private static void Run(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"{fileName} startet nicht.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    // Entfernt einen Ordnerbaum über erweiterte Pfade. Junctions werden als Link entfernt, ihr Ziel bleibt.
    private static void RemoveTree(string extendedPath)
    {
        if (!Directory.Exists(extendedPath))
            return;

        foreach (var entry in new DirectoryInfo(extendedPath).EnumerateFileSystemInfos())
        {
            bool isLink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (entry is DirectoryInfo dir)
            {
                if (isLink)
                    Directory.Delete(entry.FullName, recursive: false);
                else
                    RemoveTree(entry.FullName);
            }
            else
            {
                File.SetAttributes(entry.FullName, FileAttributes.Normal);
                File.Delete(entry.FullName);
            }
        }

        Directory.Delete(extendedPath, recursive: false);
    }

    private sealed class CallbackProgress(Action<OperationProgress> action) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => action(value);
    }
}
