using StykkerStarter.Core;
using StykkerStarter.Core.Config;
using StykkerStarter.Core.Source;
using StykkerStarter.Core.Update;
using StykkerStarter.Core.Versions;
using StykkerStarter.Platform.Windows;

namespace StykkerStarter.Tests;

// Zurück zur vorigen Version, das Halten danach, und „Copy to this PC“ ohne Start. Echte Temp-Ordner, Fakes für Prozesse.
public class RollbackTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly DataLayout _layout;
    private readonly ProgramEntry _program;
    private readonly string _exe;
    private readonly FakeProcesses _fakes = new();
    private readonly FakeLauncher _launcher;
    private readonly ProgramService _service;
    private readonly StarterEngine _engine;
    private static readonly DateTime Base = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    public RollbackTests()
    {
        _layout = new DataLayout(_temp.Sub("data"));
        _exe = SourceFixtures.CreateSource(_temp, "source", ("data.txt", "one"));
        _program = SourceFixtures.Program(_exe);
        _launcher = new FakeLauncher(_fakes);
        var timing = new ProgramTiming(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        _service = new ProgramService(_layout, _fakes, _launcher, null, timing, ownPid: 1);
        _engine = new StarterEngine(_layout, new FileVersionReader(), _service);
    }

    public void Dispose() => _temp.Dispose();

    private string VersionDir(string folder) => _layout.VersionDir(_program.Id, folder);

    // Installiert die Version 1.0.0.0 (mit Dateiversion als Plan) und lässt sie laufen
    private void InstallVersionOne()
    {
        var source = SourceTree.Read(_exe);
        var outcome = _service.Install(_program, source, VersionPlanner.Plan(_program, source, "1.0.0.0", _layout));
        Assert.Equal(UpdateKind.Updated, outcome.Kind);
        SetCopiedAt("1.0.0.0", Base);
    }

    // Die Quelle ändert sich, Version 2.0.0.0 wird kopiert und läuft
    private void UpdateToVersionTwo()
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_exe)!, "data.txt"), "two");
        var source = SourceTree.Read(_exe);
        var outcome = _service.Update(_program, source, VersionPlanner.Plan(_program, source, "2.0.0.0", _layout));
        Assert.Equal(UpdateKind.Updated, outcome.Kind);
        SetCopiedAt("2.0.0.0", Base.AddHours(1));
    }

    // Die Zeitpunkte der Kopien setzen: der Test soll nicht von der Uhr abhängen
    private void SetCopiedAt(string folder, DateTime utc) =>
        _program.Copies.Single(c => c.Folder == folder).CopiedUtc = utc;

    [Fact]
    public void Rollback_starts_the_earlier_copy_and_keeps_the_newer_one()
    {
        InstallVersionOne();
        UpdateToVersionTwo();

        var outcome = _service.Rollback(_program);

        Assert.Equal(UpdateKind.WentBack, outcome.Kind);
        Assert.Equal("1.0.0.0", _program.CurrentFolder);
        var running = Assert.Single(_service.RunningProcesses(_program));
        Assert.StartsWith(VersionDir("1.0.0.0"), running.ImagePath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(VersionDir("2.0.0.0")));   // die neuere Kopie bleibt liegen
        Assert.True(_program.HeldAfterRollback);
        Assert.Contains(_fakes.Calls, c => c.StartsWith("kill") || c.StartsWith("close"));
    }

    [Fact]
    public void Rollback_without_an_earlier_copy_changes_nothing()
    {
        InstallVersionOne();
        var pid = _service.RunningProcesses(_program)[0].Pid;

        var outcome = _service.Rollback(_program);

        Assert.Equal(UpdateKind.Failed, outcome.Kind);
        Assert.True(_fakes.IsRunning(pid));
        Assert.Equal("1.0.0.0", _program.CurrentFolder);
        Assert.False(_program.HeldAfterRollback);
    }

    [Fact]
    public void Previous_is_the_newest_older_copy_that_is_on_disk()
    {
        InstallVersionOne();
        UpdateToVersionTwo();
        // eine dritte, noch ältere Kopie
        Directory.CreateDirectory(VersionDir("0.9.0.0"));
        _program.Copies.Add(new CopyRecord { Folder = "0.9.0.0", Version = "0.9.0.0", ListHash = "x", CopiedUtc = Base.AddHours(-5) });

        Assert.Equal("1.0.0.0", _service.PreviousCopy(_program)!.Version);

        Directory.Delete(VersionDir("1.0.0.0"), recursive: true);   // nicht mehr auf der Platte: nicht anbieten
        Assert.Equal("0.9.0.0", _service.PreviousCopy(_program)!.Version);
    }

    [Fact]
    public void Without_a_local_copy_there_is_no_previous_version()
    {
        InstallVersionOne();
        UpdateToVersionTwo();
        _program.CopyToLocal = false;

        Assert.Null(_service.PreviousCopy(_program));
        Assert.Equal(UpdateKind.Failed, _service.Rollback(_program).Kind);
    }

    [Fact]
    public void When_the_earlier_copy_does_not_start_the_current_one_runs_again()
    {
        InstallVersionOne();
        UpdateToVersionTwo();
        _launcher.FailPrefix = VersionDir("1.0.0.0");

        var outcome = _service.Rollback(_program);

        Assert.Equal(UpdateKind.Failed, outcome.Kind);
        Assert.Equal("2.0.0.0", _program.CurrentFolder);
        Assert.False(_program.HeldAfterRollback);
        var running = Assert.Single(_service.RunningProcesses(_program));
        Assert.StartsWith(VersionDir("2.0.0.0"), running.ImagePath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void After_a_rollback_the_automatic_check_asks_even_when_it_would_update_without_asking()
    {
        // Mit echter Dateiversion, damit die Prüfung der Engine dieselbe Version sieht
        var version = SourceFixtures.ExpectedFileVersion;
        var installed = _engine.Install(_program);
        Assert.Equal(UpdateKind.Updated, installed.Kind);
        var first = _program.CurrentFolder!;
        SetCopiedAt(first, Base);

        // neuer Inhalt, gleiche Version: ein neuer Build
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_exe)!, "data.txt"), "two");
        var forced = _engine.Check(_program, new StarterSettings(), forceUpdate: true);
        Assert.Equal(CheckKind.Updated, forced.Kind);
        var second = _program.CurrentFolder!;
        SetCopiedAt(second, Base.AddHours(1));
        Assert.NotEqual(first, second);
        Assert.Equal(version, first.Split('+')[0]);

        var rollback = _service.Rollback(_program);
        Assert.Equal(UpdateKind.WentBack, rollback.Kind);
        Assert.Equal(first, _program.CurrentFolder);

        // Ein dritter Inhalt, der Benutzer hat nichts bestätigt: die Prüfung fragt, obwohl „ohne Rückfrage“ gesetzt ist
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_exe)!, "data.txt"), "three");
        var asking = new StarterSettings { AskBeforeUpdate = false, UpdateStoppedWithoutAsking = true };
        _program.UpdateWithoutAsking = true;
        var held = _engine.Check(_program, asking);
        Assert.Equal(CheckKind.UpdateAvailable, held.Kind);
        Assert.Equal(first, _program.CurrentFolder);

        // Der Benutzer aktualisiert selbst: danach übernimmt die Prüfung wieder, die Sperre ist weg
        var manual = _engine.Check(_program, asking, forceUpdate: true);
        Assert.Equal(CheckKind.Updated, manual.Kind);
        Assert.False(_program.HeldAfterRollback);
    }

    [Fact]
    public void Copy_only_makes_the_copy_current_and_starts_nothing()
    {
        _program.CopyToLocal = false;   // ohne Kopie: läuft aus dem Quellordner, noch nichts kopiert
        var startsBefore = _launcher.Started.Count;
        _program.CopyToLocal = true;

        var outcome = _engine.CopyOnly(_program);

        Assert.Equal(UpdateKind.Switched, outcome.Kind);
        Assert.NotNull(_program.CurrentFolder);
        Assert.True(Directory.Exists(VersionDir(_program.CurrentFolder!)));
        Assert.Single(_program.Copies);
        Assert.Equal(startsBefore, _launcher.Started.Count);
        Assert.DoesNotContain(_fakes.Procs, p => p.Running);
    }
}
