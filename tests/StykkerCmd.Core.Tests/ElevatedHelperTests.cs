using StykkerCmd.Core.Abstractions;
using StykkerCmd.Core.Elevation;
using StykkerCmd.Core.Operations;
using StykkerCmd.Core.Tests.Support;
using Xunit;

namespace StykkerCmd.Core.Tests;

public class ElevatedHelperTests
{
    // Windows-Pfade brauchen ein Laufwerk, damit sie als absolut gelten; die Attrappe nutzt sonst Unix-Pfade.
    private static string P(string path) => OperatingSystem.IsWindows() ? "C:" + path : path;

    [Fact]
    public void Encode_then_decode_roundtrips_all_fields()
    {
        var operations = new List<LeafOperation>
        {
            new(LeafKind.CopyFile, P("/vol1/a b.txt"), P("/vol1/c.txt")),
            new(LeafKind.DeleteFile, P("/vol1/d.txt"), null),
        };

        var encoded = ElevatedHelper.Encode(operations);

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.Equal(operations, ElevatedHelper.Decode(encoded));
    }

    [Fact]
    public void Validate_rejects_relative_paths()
    {
        var operations = new[] { new LeafOperation(LeafKind.DeleteFile, "relativ/a.txt", null) };

        Assert.NotNull(ElevatedHelper.Validate(operations));
    }

    [Fact]
    public void Validate_rejects_parent_segments()
    {
        var escape = Path.Combine(Path.GetTempPath(), "..", "geheim.txt");
        var operations = new[] { new LeafOperation(LeafKind.DeleteFile, escape, null) };

        Assert.NotNull(ElevatedHelper.Validate(operations));
    }

    [Fact]
    public void Validate_requires_a_target_for_copy_steps()
    {
        var operations = new[] { new LeafOperation(LeafKind.CopyFile, P("/vol1/a.txt"), null) };

        Assert.NotNull(ElevatedHelper.Validate(operations));
    }

    [Fact]
    public void Validate_rejects_empty_and_oversized_requests()
    {
        Assert.NotNull(ElevatedHelper.Validate([]));

        var tooMany = Enumerable.Range(0, ElevatedHelper.MaxOperations + 1)
            .Select(i => new LeafOperation(LeafKind.DeleteFile, P($"/vol1/{i}.txt"), null))
            .ToList();
        Assert.NotNull(ElevatedHelper.Validate(tooMany));
    }

    [Fact]
    public void Run_executes_validated_steps_and_returns_ok()
    {
        var fs = new MemoryFileSystem();
        fs.AddFile(P("/vol1/src/a.txt"), "elevated");
        fs.AddDirectory(P("/vol1/dst"));
        var services = new PlatformServices(fs, new Support.MemoryTrash(fs), null!, null!, null!, null!);
        var encoded = ElevatedHelper.Encode(
            [new LeafOperation(LeafKind.CopyFile, P("/vol1/src/a.txt"), P("/vol1/dst/a.txt"))]);

        var exitCode = ElevatedHelper.Run(encoded, services);

        Assert.Equal(ElevatedHelper.ExitOk, exitCode);
        Assert.Equal("elevated", fs.ReadText(P("/vol1/dst/a.txt")));
    }

    [Fact]
    public void Run_rejects_garbage_payload_without_touching_anything()
    {
        var fs = new MemoryFileSystem();
        fs.AddFile(P("/vol1/a.txt"), "bleibt");
        var services = new PlatformServices(fs, new Support.MemoryTrash(fs), null!, null!, null!, null!);

        Assert.Equal(ElevatedHelper.ExitInvalid, ElevatedHelper.Run("das-ist-kein-payload", services));
        Assert.Equal("bleibt", fs.ReadText(P("/vol1/a.txt")));
    }

    [Fact]
    public void Run_reports_partial_failure_with_exit_code_two()
    {
        var fs = new MemoryFileSystem();
        var services = new PlatformServices(fs, new Support.MemoryTrash(fs), null!, null!, null!, null!);
        var encoded = ElevatedHelper.Encode(
            [new LeafOperation(LeafKind.DeleteFile, P("/vol1/gibt-es-nicht.txt"), null)]);

        Assert.Equal(ElevatedHelper.ExitPartial, ElevatedHelper.Run(encoded, services));
    }
}
