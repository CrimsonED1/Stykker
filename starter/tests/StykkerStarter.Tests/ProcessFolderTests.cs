using StykkerStarter.Core.Processes;
using StykkerStarter.Core.Start;

namespace StykkerStarter.Tests;

public class FolderProcessesTests
{
    // Die Ordner sind Beispiele unter einem festen Wurzelpfad; sie müssen nicht existieren
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "stykker-starter-match");

    [Fact]
    public void A_program_inside_the_folder_belongs_to_it_including_subfolders()
    {
        var folder = Path.Combine(Root, "app", "1.0");

        Assert.True(FolderProcesses.IsUnder(Path.Combine(folder, "App.exe"), folder));
        Assert.True(FolderProcesses.IsUnder(Path.Combine(folder, "bin", "helper.exe"), folder));
    }

    [Fact]
    public void Folder_names_with_a_common_prefix_are_kept_apart()
    {
        var one = Path.Combine(Root, "app", "1.0");
        var longer = Path.Combine(Root, "app", "1.0.1");

        Assert.False(FolderProcesses.IsUnder(Path.Combine(longer, "App.exe"), one));
        Assert.False(FolderProcesses.IsUnder(Path.Combine(one, "x", "App.exe"), longer));
        Assert.True(FolderProcesses.IsUnder(Path.Combine(longer, "App.exe"), longer));
    }

    [Fact]
    public void Comparison_ignores_case()
    {
        var folder = Path.Combine(Root, "Stykker", "Starter", "App", "2.0.0");

        Assert.True(FolderProcesses.IsUnder(Path.Combine(folder.ToUpperInvariant(), "APP.EXE"), folder.ToLowerInvariant()));
    }

    [Fact]
    public void Missing_or_empty_path_belongs_to_no_folder()
    {
        var folder = Path.Combine(Root, "app", "1.0");

        Assert.False(FolderProcesses.IsUnder(null, folder));
        Assert.False(FolderProcesses.IsUnder("", folder));
        Assert.False(FolderProcesses.IsUnder(Path.Combine(folder, "App.exe"), ""));
    }

    [Fact]
    public void RunningFrom_leaves_out_the_own_process_and_the_other_folders()
    {
        var folder = Path.Combine(Root, "app", "1.0");
        var fakes = new FakeProcesses();
        var mine = fakes.Add(Path.Combine(folder, "App.exe"));
        var own = fakes.Add(Path.Combine(folder, "Starter-self.exe"));
        fakes.Add(Path.Combine(Root, "app", "1.0.1", "App.exe"));

        var found = FolderProcesses.RunningFrom(fakes, folder, own.Pid);

        Assert.Equal(new[] { mine.Pid }, found.Select(p => p.Pid).ToArray());
    }
}

public class FolderStopperTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "stykker-starter-stop", "app", "1.0");

    [Fact]
    public void A_program_that_closes_on_request_is_closed_not_killed()
    {
        var fakes = new FakeProcesses();
        var polite = fakes.Add(Path.Combine(Folder, "App.exe"), closes: true);

        var report = FolderStopper.StopAll(fakes, Folder, ownPid: 1, grace: TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { polite.Pid }, report.Closed);
        Assert.Empty(report.Killed);
        Assert.True(report.AllGone);
        Assert.DoesNotContain(fakes.Calls, c => c.StartsWith("kill"));
    }

    [Fact]
    public void A_program_that_does_not_close_is_ended_after_the_grace_period()
    {
        var fakes = new FakeProcesses();
        var stubborn = fakes.Add(Path.Combine(Folder, "App.exe"), closes: false);
        var grace = TimeSpan.FromMilliseconds(300);
        var started = DateTime.UtcNow;

        var report = FolderStopper.StopAll(fakes, Folder, ownPid: 1, grace);

        Assert.Equal(new[] { stubborn.Pid }, report.Killed);
        Assert.True(report.AllGone);
        Assert.True(DateTime.UtcNow - started >= grace, "it waited for the grace period before killing");
    }

    [Fact]
    public void Processes_from_a_similar_folder_and_the_own_process_are_never_touched()
    {
        var fakes = new FakeProcesses();
        var longer = fakes.Add(Path.Combine(Folder + ".1", "App.exe"), closes: false);
        var own = fakes.Add(Path.Combine(Folder, "Self.exe"), closes: false);

        var report = FolderStopper.StopAll(fakes, Folder, ownPid: own.Pid, grace: TimeSpan.FromMilliseconds(100));

        Assert.Empty(report.Closed);
        Assert.Empty(report.Killed);
        Assert.True(longer.Running);
        Assert.True(own.Running);
    }

    [Fact]
    public void A_child_started_while_the_parent_closes_is_ended_too()
    {
        var fakes = new FakeProcesses();
        var parent = fakes.Add(Path.Combine(Folder, "App.exe"), closes: true);
        FakeProc? child = null;
        fakes.AfterClose = f =>
        {
            if (child == null) child = f.Add(Path.Combine(Folder, "Worker.exe"), closes: false);
        };

        var report = FolderStopper.StopAll(fakes, Folder, ownPid: 1, grace: TimeSpan.FromMilliseconds(100));

        Assert.NotNull(child);
        Assert.Equal(new[] { parent.Pid }, report.Closed);
        Assert.Equal(new[] { child!.Pid }, report.Killed);
        Assert.True(report.AllGone);
    }
}

public class ProgramStartTests
{
    private static readonly string Exe = Path.Combine(Path.GetTempPath(), "stykker-starter-start", "app", "1.0", "App.exe");

    [Fact]
    public void A_start_that_throws_is_a_failed_start()
    {
        var fakes = new FakeProcesses();
        var launcher = new FakeLauncher(fakes) { FailPrefix = Exe };

        var outcome = ProgramStart.Start(launcher, Exe, "", Path.GetDirectoryName(Exe)!, TimeSpan.FromMilliseconds(200));

        Assert.False(outcome.Ok);
        Assert.Contains("could not start", outcome.Message);
    }

    [Fact]
    public void A_program_that_ends_with_a_code_other_than_zero_within_the_watch_time_is_a_failed_start()
    {
        var fakes = new FakeProcesses();
        var launcher = new FakeLauncher(fakes);
        launcher.ExitCodes[Exe] = 3;

        var outcome = ProgramStart.Start(launcher, Exe, "", Path.GetDirectoryName(Exe)!, TimeSpan.FromSeconds(2));

        Assert.False(outcome.Ok);
        Assert.Contains("code 3", outcome.Message);
    }

    [Fact]
    public void A_program_that_ends_with_code_zero_counts_as_started()
    {
        var fakes = new FakeProcesses();
        var launcher = new FakeLauncher(fakes);
        launcher.ExitCodes[Exe] = 0;

        var outcome = ProgramStart.Start(launcher, Exe, "", Path.GetDirectoryName(Exe)!, TimeSpan.FromSeconds(2));

        Assert.True(outcome.Ok);
    }

    [Fact]
    public void A_program_still_running_after_the_watch_time_is_started()
    {
        var fakes = new FakeProcesses();
        var launcher = new FakeLauncher(fakes);

        var outcome = ProgramStart.Start(launcher, Exe, "", Path.GetDirectoryName(Exe)!, TimeSpan.FromMilliseconds(200));

        Assert.True(outcome.Ok);
        Assert.Equal("running", outcome.Message);
    }
}
