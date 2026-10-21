using StykkerStarter.Core.Source;
using StykkerStarter.Core.Versions;

namespace StykkerStarter.Tests;

// „new build“ statt „update“ bei gleicher Version; die Zeit des Builds aus der Quelle
public class NewBuildTests
{
    [Fact]
    public void The_same_version_with_other_content_is_a_new_build()
    {
        Assert.True(VersionPlanner.IsNewBuild("0.4.0.0", "0.4.0.0"));
        Assert.False(VersionPlanner.IsNewBuild("0.5.0.0", "0.4.0.0"));
        Assert.False(VersionPlanner.IsNewBuild("0.4.0.0", null));
        Assert.False(VersionPlanner.IsNewBuild(null, "0.4.0.0"));
    }

    [Fact]
    public void Build_time_is_the_newest_write_of_a_program_file_in_local_time()
    {
        using var temp = new TempFolder();
        var exe = SourceFixtures.CreateSource(temp, "source", ("data.txt", "one"), ("lib\\payload.dll", "x"));
        var older = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 10, 20, 16, 45, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(exe, older);
        File.SetLastWriteTimeUtc(Path.Combine(Path.GetDirectoryName(exe)!, "lib", "payload.dll"), newer);
        File.SetLastWriteTimeUtc(Path.Combine(Path.GetDirectoryName(exe)!, "data.txt"), DateTime.UtcNow.AddYears(1));   // keine Programmdatei

        var tree = SourceTree.Read(exe);

        Assert.Equal(newer.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), tree.BuiltAtText);
    }

    [Fact]
    public void Without_a_program_file_the_build_time_is_unknown()
    {
        var tree = new SourceTree("C:\\x\\a.txt", "C:\\x", new List<SourceFile> { new("data.txt", 1, DateTime.UtcNow) });

        Assert.Null(tree.BuiltAtText);
    }
}
