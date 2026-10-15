using StykkerStarter.Core;
using StykkerStarter.Core.Config;
using StykkerStarter.Core.Source;
using StykkerStarter.Core.Update;
using StykkerStarter.Core.Versions;
using StykkerStarter.Platform.Windows;

namespace StykkerStarter.Tests;

// Start ohne Kopie: das Programm läuft direkt aus dem Quellordner. Prozesse und Starten sind Fakes, der Quellordner
// ist ein echter Temp-Ordner. Der Datenordner wird nie angelegt, außer wenn der Starter dort etwas schreibt.
public class InPlaceTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly DataLayout _layout;
    private readonly string _exe;
    private readonly string _source;
    private readonly ProgramEntry _program;
    private readonly FakeProcesses _fakes = new();
    private readonly FakeLauncher _launcher;
    private readonly ProgramService _service;
    private readonly StarterEngine _engine;
    private static readonly StarterSettings RestartWithoutAsking = new() { AskBeforeUpdate = false };

    public InPlaceTests()
    {
        _layout = new DataLayout(_temp.Sub("data"));
        _exe = SourceFixtures.CreateSource(_temp, "source", ("data.txt", "one"));
        _source = Path.GetDirectoryName(_exe)!;
        _program = SourceFixtures.Program(_exe);
        _program.CopyToLocal = false;
        _launcher = new FakeLauncher(_fakes);
        var timing = new ProgramTiming(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        _service = new ProgramService(_layout, _fakes, _launcher, null, timing, ownPid: 1);
        _engine = new StarterEngine(_layout, new FileVersionReader(), _service);
    }

    public void Dispose() => _temp.Dispose();

    // Ändert eine Datei der Quelle (der Stand der Dateiliste ändert sich, die Version nicht)
    private void ChangeSource(string content = "two, changed") =>
        File.WriteAllText(Path.Combine(_source, "data.txt"), content);

    [Fact]
    public void Start_without_copy_runs_from_the_source_and_creates_nothing_under_the_data_folder()
    {
        var outcome = _engine.StartInPlace(_program);

        Assert.True(outcome.Ok);
        Assert.False(Directory.Exists(_layout.Root));
        Assert.Empty(_program.Copies);
        Assert.Null(_program.CurrentFolder);
        var proc = Assert.Single(_fakes.Procs);
        Assert.Equal(Path.GetFullPath(_exe), proc.ImagePath);
        Assert.Equal(SourceFixtures.VersionOf(_exe), _program.StartedVersion);
        Assert.Equal(SourceTree.Read(_exe).ListHash, _program.StartedListHash);
    }

    [Fact]
    public void Right_after_the_start_nothing_is_reported_and_nothing_is_restarted()
    {
        _engine.StartInPlace(_program);
        var startsBefore = _launcher.Started.Count;

        var result = _engine.Check(_program, RestartWithoutAsking);

        Assert.Equal(CheckKind.UpToDate, result.Kind);
        Assert.Equal(startsBefore, _launcher.Started.Count);
    }

    [Fact]
    public void A_changed_build_counts_only_after_two_equal_checks_then_restarts()
    {
        _engine.StartInPlace(_program);
        var oldPid = _fakes.Procs.Single().Pid;
        ChangeSource();

        var first = _engine.Check(_program, RestartWithoutAsking);

        Assert.Equal(CheckKind.ChangedSinceStart, first.Kind);
        Assert.Equal("changed since last start", first.Message);
        Assert.Empty(_fakes.Calls);          // nichts beendet, nichts gestartet
        Assert.True(_fakes.IsRunning(oldPid));

        var second = _engine.Check(_program, RestartWithoutAsking);

        Assert.Equal(CheckKind.Updated, second.Kind);
        Assert.Equal(UpdateKind.Restarted, second.Outcome!.Kind);
        Assert.Contains(oldPid, second.Outcome.StoppedPids);
        Assert.False(_fakes.IsRunning(oldPid));
        var now = Assert.Single(_fakes.Procs, p => p.Running);
        Assert.NotEqual(oldPid, now.Pid);
        Assert.Equal(SourceTree.Read(_exe).ListHash, _program.StartedListHash);
        Assert.Equal(CheckKind.UpToDate, _engine.Check(_program, RestartWithoutAsking).Kind);
    }

    [Fact]
    public void A_build_that_keeps_changing_between_checks_is_not_restarted_until_it_settles()
    {
        _engine.StartInPlace(_program);
        var oldPid = _fakes.Procs.Single().Pid;
        ChangeSource("first step");
        Assert.Equal(CheckKind.ChangedSinceStart, _engine.Check(_program, RestartWithoutAsking).Kind);
        ChangeSource("second step, longer");
        Assert.Equal(CheckKind.ChangedSinceStart, _engine.Check(_program, RestartWithoutAsking).Kind);
        Assert.Empty(_fakes.Calls);

        var settled = _engine.Check(_program, RestartWithoutAsking);

        Assert.Equal(CheckKind.Updated, settled.Kind);
        Assert.False(_fakes.IsRunning(oldPid));
    }

    [Fact]
    public void A_changed_version_alone_counts_as_a_changed_build()
    {
        _engine.StartInPlace(_program);
        var oldPid = _fakes.Procs.Single().Pid;
        _program.StartedVersion = "0.0.0.0";   // die Quelle trägt eine andere Version als beim Start

        Assert.Equal(CheckKind.ChangedSinceStart, _engine.Check(_program, RestartWithoutAsking).Kind);
        Assert.Equal(CheckKind.Updated, _engine.Check(_program, RestartWithoutAsking).Kind);
        Assert.False(_fakes.IsRunning(oldPid));
    }

    [Fact]
    public void A_stopped_program_is_only_reported_and_not_started()
    {
        _engine.StartInPlace(_program);
        foreach (var proc in _fakes.Procs) proc.Running = false;
        var startsBefore = _launcher.Started.Count;
        ChangeSource();

        Assert.Equal(CheckKind.ChangedSinceStart, _engine.Check(_program, RestartWithoutAsking).Kind);
        Assert.Equal(CheckKind.ChangedSinceStart, _engine.Check(_program, RestartWithoutAsking).Kind);

        Assert.Equal(startsBefore, _launcher.Started.Count);
        Assert.Empty(_fakes.Calls);
    }

    [Fact]
    public void A_running_program_only_gets_an_offer_when_the_settings_ask()
    {
        _engine.StartInPlace(_program);
        var oldPid = _fakes.Procs.Single().Pid;
        ChangeSource();
        var asking = new StarterSettings { AskBeforeUpdate = true };

        _engine.Check(_program, asking);
        var offer = _engine.Check(_program, asking);

        Assert.Equal(CheckKind.UpdateAvailable, offer.Kind);
        Assert.NotEqual(SourceTree.Read(_exe).ListHash, _program.StartedListHash);
        Assert.True(_fakes.IsRunning(oldPid));
        Assert.Empty(_fakes.Calls);
    }

    [Fact]
    public void Restart_stops_only_what_runs_from_the_source_folder()
    {
        _engine.StartInPlace(_program);
        var sourceProc = _fakes.Procs.Single();
        var copyProc = _fakes.Add(Path.Combine(_layout.ProgramDir("app"), "1.0.0.0", "App.exe"));   // aus einer Kopie
        var sibling = _fakes.Add(Path.Combine(_source + "2", "App.exe"));                          // Ordner mit gleichem Anfang
        ChangeSource();
        _engine.Check(_program, RestartWithoutAsking);

        var result = _engine.Check(_program, RestartWithoutAsking);

        Assert.Equal(CheckKind.Updated, result.Kind);
        Assert.False(_fakes.IsRunning(sourceProc.Pid));
        Assert.True(_fakes.IsRunning(copyProc.Pid));
        Assert.True(_fakes.IsRunning(sibling.Pid));
    }

    [Fact]
    public void A_new_build_that_does_not_start_is_not_rolled_back()
    {
        _engine.StartInPlace(_program);
        var oldPid = _fakes.Procs.Single().Pid;
        var oldStamp = _program.StartedListHash;
        _launcher.FailPrefix = Path.GetFullPath(_exe);
        ChangeSource();
        _engine.Check(_program, RestartWithoutAsking);

        var result = _engine.Check(_program, RestartWithoutAsking);

        Assert.Equal(CheckKind.Updated, result.Kind);
        Assert.Equal(UpdateKind.Failed, result.Outcome!.Kind);
        Assert.Contains("did not start", result.Outcome.Message);
        Assert.False(_fakes.IsRunning(oldPid));
        Assert.DoesNotContain(_fakes.Procs, p => p.Running);
        Assert.Equal(oldStamp, _program.StartedListHash);   // der alte Stand bleibt gemerkt
    }

    [Fact]
    public void Unreachable_source_without_copy_says_so_and_starts_nothing()
    {
        var gone = SourceFixtures.Program(_temp.Sub("nowhere\\App.exe"));
        gone.CopyToLocal = false;

        var result = _engine.Check(gone, RestartWithoutAsking);
        var start = _engine.StartInPlace(gone);

        Assert.Equal(CheckKind.Unreachable, result.Kind);
        Assert.Equal("source folder not reachable – no local copy", result.Message);
        Assert.False(start.Ok);
        Assert.Equal("source folder not reachable – no local copy", start.Message);
        Assert.Empty(_fakes.Procs);
        Assert.Null(gone.StartedListHash);
    }

    [Fact]
    public void Cleanup_without_copy_removes_the_copies_that_are_not_running()
    {
        var now = DateTime.UtcNow;
        foreach (var folder in new[] { "a", "b" })
        {
            Directory.CreateDirectory(_layout.VersionDir(_program.Id, folder));
            _program.Copies.Add(new CopyRecord { Folder = folder, Version = folder, ListHash = folder, CopiedUtc = now });
        }
        _program.CurrentFolder = "b";
        _fakes.Add(Path.Combine(_layout.VersionDir(_program.Id, "a"), "App.exe"));   // aus „a“ läuft noch etwas

        var report = _service.Cleanup(_program, versionsKept: 2);

        Assert.Equal(new[] { "b" }, report.Removed);
        Assert.Equal(new[] { "a" }, report.KeptRunning);
        Assert.Null(_program.CurrentFolder);
        Assert.Equal(new[] { "a" }, _program.Copies.Select(c => c.Folder).ToArray());
        Assert.False(Directory.Exists(_layout.VersionDir(_program.Id, "b")));
    }
}
