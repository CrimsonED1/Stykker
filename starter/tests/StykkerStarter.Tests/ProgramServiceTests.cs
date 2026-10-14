using StykkerStarter.Core;
using StykkerStarter.Core.Config;
using StykkerStarter.Core.Copy;
using StykkerStarter.Core.Source;
using StykkerStarter.Core.Update;
using StykkerStarter.Core.Versions;

namespace StykkerStarter.Tests;

// Der Ablauf Update und Rücksprung mit Fakes für Prozesse und Starten. Kopiert wird echt, in Temp-Ordner.
public class ProgramServiceTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly DataLayout _layout;
    private readonly ProgramEntry _program;
    private readonly string _exe;
    private readonly FakeProcesses _fakes = new();
    private readonly FakeLauncher _launcher;
    private readonly ProgramService _service;

    public ProgramServiceTests()
    {
        _layout = new DataLayout(_temp.Sub("data"));
        _exe = SourceFixtures.CreateSource(_temp, "source", ("data.txt", "one"));
        _program = SourceFixtures.Program(_exe);
        _launcher = new FakeLauncher(_fakes);
        var timing = new ProgramTiming(CloseGrace: TimeSpan.FromMilliseconds(200), StartWatch: TimeSpan.FromMilliseconds(200));
        _service = new ProgramService(_layout, _fakes, _launcher, null, timing, ownPid: 1);
    }

    public void Dispose() => _temp.Dispose();

    // Installiert die Version 1.0.0.0 und startet sie
    private void InstallVersionOne()
    {
        var source = SourceTree.Read(_exe);
        var plan = VersionPlanner.Plan(_program, source, "1.0.0.0", _layout);
        var outcome = _service.Install(_program, source, plan);
        Assert.Equal(UpdateKind.Updated, outcome.Kind);
        Assert.Equal("1.0.0.0", _program.CurrentFolder);
    }

    // Ändert die Quelle und liefert die neue Liste mit dem Plan für die Version 2.0.0.0
    private (SourceTree Source, CopyPlan Plan) NewSourceVersionTwo()
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_exe)!, "data.txt"), "two, changed");
        var source = SourceTree.Read(_exe);
        return (source, VersionPlanner.Plan(_program, source, "2.0.0.0", _layout));
    }

    private string VersionDir(string folder) => _layout.VersionDir(_program.Id, folder);

    [Fact]
    public void Install_copies_checks_and_starts_the_program_from_its_copy()
    {
        InstallVersionOne();

        var running = _service.RunningProcesses(_program);
        var one = Assert.Single(running);
        Assert.StartsWith(VersionDir("1.0.0.0"), one.ImagePath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(VersionDir("1.0.0.0")));
    }

    [Fact]
    public void Old_version_keeps_running_until_the_new_copy_is_finished_and_checked()
    {
        InstallVersionOne();
        var oldPid = _service.RunningProcesses(_program)[0].Pid;
        var (source, plan) = NewSourceVersionTwo();
        var callsDuringCopy = new List<int>();

        var outcome = _service.Update(_program, source, plan, new Probe(() => callsDuringCopy.Add(_fakes.Calls.Count)));

        Assert.NotEmpty(callsDuringCopy);
        Assert.All(callsDuringCopy, n => Assert.Equal(0, n));   // während des Kopierens wurde nichts beendet
        Assert.Equal(UpdateKind.Updated, outcome.Kind);
        Assert.Contains(oldPid, outcome.StoppedPids);
        Assert.Equal("2.0.0.0", _program.CurrentFolder);
        var now = Assert.Single(_service.RunningProcesses(_program));
        Assert.NotEqual(oldPid, now.Pid);
        Assert.False(_fakes.IsRunning(oldPid));
    }

    [Fact]
    public void New_version_that_does_not_start_brings_the_old_one_back()
    {
        InstallVersionOne();
        _launcher.FailPrefix = VersionDir("2.0.0.0");
        var (source, plan) = NewSourceVersionTwo();

        var outcome = _service.Update(_program, source, plan);

        Assert.Equal(UpdateKind.RolledBack, outcome.Kind);
        Assert.Equal("1.0.0.0", _program.CurrentFolder);
        var back = Assert.Single(_service.RunningProcesses(_program));
        Assert.StartsWith(VersionDir("1.0.0.0"), back.ImagePath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void New_version_that_ends_with_an_error_code_brings_the_old_one_back()
    {
        InstallVersionOne();
        _launcher.ExitCodes[Path.Combine(VersionDir("2.0.0.0"), Path.GetFileName(_exe))] = 3;
        var (source, plan) = NewSourceVersionTwo();

        var outcome = _service.Update(_program, source, plan);

        Assert.Equal(UpdateKind.RolledBack, outcome.Kind);
        Assert.Equal("1.0.0.0", _program.CurrentFolder);
        Assert.Single(_service.RunningProcesses(_program));
    }

    [Fact]
    public void Failed_copy_leaves_the_old_version_running_and_the_entry_unchanged()
    {
        InstallVersionOne();
        var oldPid = _service.RunningProcesses(_program)[0].Pid;
        var (source, plan) = NewSourceVersionTwo();
        File.Delete(Path.Combine(source.Folder, "data.txt"));   // die Quelle verschwindet während der Kopie

        var outcome = _service.Update(_program, source, plan);

        Assert.Equal(UpdateKind.Failed, outcome.Kind);
        Assert.True(_fakes.IsRunning(oldPid));
        Assert.DoesNotContain(_fakes.Calls, c => c.StartsWith("close") || c.StartsWith("kill"));
        Assert.Equal("1.0.0.0", _program.CurrentFolder);
        Assert.False(Directory.Exists(VersionDir("2.0.0.0")));
        Assert.False(Directory.Exists(VersionDir("2.0.0.0") + DataLayout.PartialSuffix));
    }

    [Fact]
    public void A_stopped_program_is_only_switched_and_not_started()
    {
        InstallVersionOne();
        foreach (var proc in _fakes.Procs) proc.Running = false;
        var startsBefore = _launcher.Started.Count;
        var (source, plan) = NewSourceVersionTwo();

        var outcome = _service.Update(_program, source, plan);

        Assert.Equal(UpdateKind.Switched, outcome.Kind);
        Assert.Equal("2.0.0.0", _program.CurrentFolder);
        Assert.Equal(startsBefore, _launcher.Started.Count);
    }

    [Fact]
    public void Starting_a_program_that_already_runs_starts_nothing_more()
    {
        InstallVersionOne();
        var startsBefore = _launcher.Started.Count;

        var outcome = _service.StartCurrent(_program);

        Assert.True(outcome.Ok);
        Assert.Equal("already running", outcome.Message);
        Assert.Equal(startsBefore, _launcher.Started.Count);
    }

    [Fact]
    public void Cleanup_keeps_the_running_copy_the_current_one_and_the_newest_others()
    {
        // vier Kopien, die älteste läuft nicht und geht; die laufende bleibt, weil noch etwas aus ihr läuft
        var now = DateTime.UtcNow;
        foreach (var (folder, age) in new[] { ("a", 4), ("b", 3), ("c", 2), ("d", 1) })
        {
            Directory.CreateDirectory(VersionDir(folder));
            _program.Copies.Add(new CopyRecord { Folder = folder, Version = folder, ListHash = folder, CopiedUtc = now.AddHours(-age) });
        }
        _program.CurrentFolder = "c";
        _fakes.Add(Path.Combine(VersionDir("b"), "App.exe"));

        var report = _service.Cleanup(_program, versionsKept: 2);

        Assert.Equal(new[] { "a" }, report.Removed);
        Assert.Equal(new[] { "b" }, report.KeptRunning);
        Assert.Equal(new[] { "b", "c", "d" }, _program.Copies.Select(c => c.Folder).OrderBy(f => f).ToArray());
        Assert.False(Directory.Exists(VersionDir("a")));
        Assert.True(Directory.Exists(VersionDir("b")));
    }

    // Protokolliert bei jedem Zwischenstand, wie viele Aufrufe es am Prozesssystem schon gab
    private sealed class Probe(Action onReport) : IProgress<CopyProgress>
    {
        public void Report(CopyProgress value) => onReport();
    }
}
