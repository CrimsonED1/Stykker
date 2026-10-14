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
}

public class StarterEngineTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly DataLayout _layout;
    private readonly string _exe;
    private readonly ProgramEntry _program;
    private readonly FakeProcesses _fakes = new();
    private readonly StarterEngine _engine;

    public StarterEngineTests()
    {
        _layout = new DataLayout(_temp.Sub("data"));
        _exe = SourceFixtures.CreateSource(_temp, "source", ("data.txt", "one"));
        _program = SourceFixtures.Program(_exe);
        var launcher = new FakeLauncher(_fakes);
        var timing = new StykkerStarter.Core.Update.ProgramTiming(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        var service = new ProgramService(_layout, _fakes, launcher, null, timing, ownPid: 1);
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
}
