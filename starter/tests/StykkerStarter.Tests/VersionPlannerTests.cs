using StykkerStarter.Core;
using StykkerStarter.Core.Copy;
using StykkerStarter.Core.Config;
using StykkerStarter.Core.Source;
using StykkerStarter.Core.Versions;

namespace StykkerStarter.Tests;

public class VersionPlannerTests
{
    [Fact]
    public void Version_is_the_file_version_of_the_exe()
    {
        using var temp = new TempFolder();
        var exe = SourceFixtures.CreateSource(temp, "source", ("data.txt", "x"));
        var source = SourceTree.Read(exe);
        var layout = new DataLayout(temp.Sub("data"));
        var program = SourceFixtures.Program(exe);

        var plan = VersionPlanner.Plan(program, source, SourceFixtures.VersionOf(exe), layout);

        Assert.Equal(PlanKind.Copy, plan.Kind);
        Assert.Equal(SourceFixtures.ExpectedFileVersion, plan.Version);
        Assert.Equal(SourceFixtures.ExpectedFileVersion, plan.Folder);
    }

    [Fact]
    public void Without_a_file_version_the_list_hash_names_the_version()
    {
        using var temp = new TempFolder();
        var exe = SourceFixtures.CreateSource(temp, "source", ("data.txt", "x"));
        var source = SourceTree.Read(exe);
        var layout = new DataLayout(temp.Sub("data"));

        var plan = VersionPlanner.Plan(SourceFixtures.Program(exe), source, null, layout);

        Assert.Equal(source.ListHash[..8], plan.Version);
    }

    [Fact]
    public void Same_version_with_other_content_gets_a_folder_with_the_list_hash()
    {
        using var temp = new TempFolder();
        var exe = SourceFixtures.CreateSource(temp, "source", ("data.txt", "one"));
        var layout = new DataLayout(temp.Sub("data"));
        var program = SourceFixtures.Program(exe);
        var version = SourceFixtures.VersionOf(exe);

        // erste Kopie
        var first = SourceTree.Read(exe);
        var plan1 = VersionPlanner.Plan(program, first, version, layout);
        var target1 = layout.VersionDir(program.Id, plan1.Folder);
        VersionCopier.Copy(first, target1);
        program.Copies.Add(new CopyRecord { Folder = plan1.Folder, Version = plan1.Version, ListHash = plan1.ListHash });
        program.CurrentFolder = plan1.Folder;

        // die Quelle ändert sich, die Dateiversion bleibt
        File.WriteAllText(Path.Combine(first.Folder, "data.txt"), "two, longer");
        var changed = SourceTree.Read(exe);

        var plan2 = VersionPlanner.Plan(program, changed, version, layout);

        Assert.Equal(PlanKind.Copy, plan2.Kind);
        Assert.Equal(version, plan2.Version);
        Assert.Equal($"{version}+{changed.ListHash[..8]}", plan2.Folder);
    }

    [Fact]
    public void Same_version_and_same_list_is_up_to_date()
    {
        using var temp = new TempFolder();
        var exe = SourceFixtures.CreateSource(temp, "source", ("data.txt", "one"));
        var layout = new DataLayout(temp.Sub("data"));
        var program = SourceFixtures.Program(exe);
        var source = SourceTree.Read(exe);
        var version = SourceFixtures.VersionOf(exe);
        var plan = VersionPlanner.Plan(program, source, version, layout);
        VersionCopier.Copy(source, layout.VersionDir(program.Id, plan.Folder));
        program.Copies.Add(new CopyRecord { Folder = plan.Folder, Version = plan.Version, ListHash = plan.ListHash });
        program.CurrentFolder = plan.Folder;

        var again = VersionPlanner.Plan(program, SourceTree.Read(exe), version, layout);

        Assert.Equal(PlanKind.UpToDate, again.Kind);
        Assert.Equal(plan.Folder, again.Folder);
    }

    [Fact]
    public void Going_back_to_a_copy_that_is_still_on_disk_is_a_reuse()
    {
        using var temp = new TempFolder();
        var exe = SourceFixtures.CreateSource(temp, "source", ("data.txt", "one"));
        var layout = new DataLayout(temp.Sub("data"));
        var program = SourceFixtures.Program(exe);
        var old = SourceTree.Read(exe);
        var oldPlan = VersionPlanner.Plan(program, old, SourceFixtures.VersionOf(exe), layout);
        VersionCopier.Copy(old, layout.VersionDir(program.Id, oldPlan.Folder));
        program.Copies.Add(new CopyRecord { Folder = oldPlan.Folder, Version = oldPlan.Version, ListHash = oldPlan.ListHash });
        program.CurrentFolder = oldPlan.Folder;

        // eine neue Version (andere Dateiversion) wird die laufende
        var newVersion = "9.9.9.9";
        var newPlan = VersionPlanner.Plan(program, old, newVersion, layout);
        VersionCopier.Copy(old, layout.VersionDir(program.Id, newPlan.Folder));
        program.Copies.Add(new CopyRecord { Folder = newPlan.Folder, Version = newPlan.Version, ListHash = newPlan.ListHash });
        program.CurrentFolder = newPlan.Folder;

        // zurück auf die alte Version: die liegt noch da
        var back = VersionPlanner.Plan(program, old, oldPlan.Version, layout);

        Assert.Equal(PlanKind.Reuse, back.Kind);
        Assert.Equal(oldPlan.Folder, back.Folder);
    }
}
