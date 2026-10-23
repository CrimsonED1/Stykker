using StykkerStarter.Core;
using StykkerStarter.Core.Config;
using StykkerStarter.Core.Source;
using StykkerStarter.Core.Update;
using StykkerStarter.Platform.Windows;

namespace StykkerStarter.Tests;

public class UpdatePolicyTests
{
    // settingNoAsk: „Ask before updating a running program“ aus bzw. „Update stopped programs without asking“ an
    [Theory]
    [InlineData(true, false, false, PolicyDecision.Ask)]        // laufend, Rückfrage an: fragen
    [InlineData(true, false, true, PolicyDecision.UpdateNow)]   // laufend, Rückfrage aus: übernehmen
    [InlineData(true, true, false, PolicyDecision.UpdateNow)]   // laufend, das Programm will es so
    [InlineData(false, false, false, PolicyDecision.Ask)]       // gestoppt, Einstellung aus: fragen
    [InlineData(false, false, true, PolicyDecision.UpdateNow)]  // gestoppt, Einstellung an: übernehmen
    [InlineData(false, true, false, PolicyDecision.UpdateNow)]  // gestoppt, das Programm will es so
    public void Decide_follows_the_settings(bool running, bool programWantsNoAsk, bool settingNoAsk, PolicyDecision expected)
    {
        var settings = new StarterSettings
        {
            AskBeforeUpdate = !settingNoAsk,
            UpdateStoppedWithoutAsking = settingNoAsk,
        };
        var program = new ProgramEntry { Id = "p", UpdateWithoutAsking = programWantsNoAsk };

        Assert.Equal(expected, UpdatePolicy.Decide(settings, program, running));
    }

    // Beim Start des Starters: ein gestopptes Programm übernimmt den neuen Stand auch bei „fragen“; ein laufendes nicht
    [Theory]
    [InlineData(false, PolicyDecision.UpdateNow)]
    [InlineData(true, PolicyDecision.Ask)]
    public void At_start_a_stopped_program_updates_even_when_the_settings_ask(bool running, PolicyDecision expected)
    {
        var settings = new StarterSettings { AskBeforeUpdate = true, UpdateStoppedWithoutAsking = false };
        var program = new ProgramEntry { Id = "p" };

        Assert.Equal(expected, UpdatePolicy.Decide(settings, program, running, atStart: true));
    }
}

public class StarterEngineTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly DataLayout _layout;
    private readonly string _exe;
    private readonly ProgramEntry _program;
    private readonly FakeProcesses _fakes = new();
    private readonly FakeLauncher _launcher;
    private readonly StarterEngine _engine;

    public StarterEngineTests()
    {
        _layout = new DataLayout(_temp.Sub("data"));
        _exe = SourceFixtures.CreateSource(_temp, "source", ("data.txt", "one"));
        _program = SourceFixtures.Program(_exe);
        _launcher = new FakeLauncher(_fakes);
        var timing = new StykkerStarter.Core.Update.ProgramTiming(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        var service = new ProgramService(_layout, _fakes, _launcher, null, timing, ownPid: 1);
        _engine = new StarterEngine(_layout, new FileVersionReader(), service);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Unreachable_source_changes_nothing()
    {
        var gone = SourceFixtures.Program(_temp.Sub("nowhere\\App.exe"));
        var result = _engine.Check(gone, new StarterSettings());

        Assert.Equal(CheckKind.Unreachable, result.Kind);
        Assert.Empty(gone.Copies);
        Assert.Null(gone.CurrentFolder);
    }

    [Fact]
    public void A_new_version_is_only_announced_when_the_settings_ask()
    {
        var settings = new StarterSettings { UpdateStoppedWithoutAsking = false };

        var result = _engine.Check(_program, settings);

        Assert.Equal(CheckKind.UpdateAvailable, result.Kind);
        Assert.NotNull(result.Plan);
        Assert.Empty(_program.Copies);   // nichts kopiert, bevor der Benutzer „Update“ drückt
        Assert.False(Directory.Exists(_layout.ProgramDir(_program.Id)));
    }

    [Fact]
    public void A_stopped_program_updates_without_asking_when_the_settings_say_so()
    {
        var settings = new StarterSettings { UpdateStoppedWithoutAsking = true };

        var result = _engine.Check(_program, settings);

        Assert.Equal(CheckKind.Updated, result.Kind);
        Assert.NotNull(_program.CurrentFolder);
        Assert.True(Directory.Exists(_layout.VersionDir(_program.Id, _program.CurrentFolder!)));
    }

    [Fact]
    public void Pressing_update_takes_the_new_version_even_when_asked_not_to_do_it_on_its_own()
    {
        var result = _engine.Check(_program, new StarterSettings { UpdateStoppedWithoutAsking = false }, forceUpdate: true);

        Assert.Equal(CheckKind.Updated, result.Kind);
        Assert.NotNull(_program.CurrentFolder);
    }

    // Eine erste Kopie, die nicht läuft; danach ändert sich die Quelle (ein neuer Build)
    private string CopyThenNewBuild()
    {
        Assert.Equal(CheckKind.Updated, _engine.Check(_program, new StarterSettings(), forceUpdate: true).Kind);
        var first = _program.CurrentFolder!;
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_exe)!, "data.txt"), "two");
        return first;
    }

    private static StarterSettings AskAlways() => new() { AskBeforeUpdate = true, UpdateStoppedWithoutAsking = false };

    [Fact]
    public void At_start_a_new_build_is_copied_and_switched_without_starting()
    {
        var first = CopyThenNewBuild();

        var result = _engine.Check(_program, AskAlways(), atStart: true);

        Assert.Equal(CheckKind.Updated, result.Kind);
        Assert.Equal(UpdateKind.Switched, result.Outcome!.Kind);
        Assert.NotEqual(first, _program.CurrentFolder);
        Assert.Empty(_launcher.Started);   // starten tut danach die Startfolge
        Assert.Empty(_fakes.Calls);        // und beendet wurde nichts
    }

    [Fact]
    public void Without_start_the_same_new_build_only_waits_for_the_user()
    {
        var first = CopyThenNewBuild();

        var result = _engine.Check(_program, AskAlways());

        Assert.Equal(CheckKind.UpdateAvailable, result.Kind);
        Assert.Equal(first, _program.CurrentFolder);
    }

    [Fact]
    public void At_start_a_program_held_after_going_back_keeps_its_copy()
    {
        var first = CopyThenNewBuild();
        _program.HeldAfterRollback = true;

        var result = _engine.Check(_program, AskAlways(), atStart: true);

        Assert.Equal(CheckKind.UpdateAvailable, result.Kind);
        Assert.Equal(first, _program.CurrentFolder);
    }
}
