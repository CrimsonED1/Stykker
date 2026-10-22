using StykkerStarter.Core;
using StykkerStarter.Core.Config;
using StykkerStarter.Core.Source;
using StykkerStarter.Core.Versions;
using StykkerStarter.Core.Web;

namespace StykkerStarter.Tests;

// Die neuen Felder in starter.json, die neue Anzeige und der Stand „neuer Build“
public class ConfigFieldsTests
{
    [Fact]
    public void An_old_starter_json_without_the_new_fields_loads_unchanged()
    {
        using var temp = new TempFolder();
        var layout = new DataLayout(temp.Sub("data"));
        TempFolder.Write(layout.ConfigFile, """
            {
              "schemaVersion": 1,
              "settings": { "checkIntervalMinutes": 15, "closeAction": "tray" },
              "programs": [
                { "id": "demo", "name": "Demo", "sourceExe": "C:\\Programs\\demo.exe", "copyToLocal": false }
              ]
            }
            """);

        var config = ConfigStore.Load(layout);

        Assert.Equal(15, config.Settings.CheckIntervalMinutes);
        Assert.Equal("tray", config.Settings.CloseAction);
        Assert.Equal(3, config.Settings.StartupDelaySeconds);
        var program = Assert.Single(config.Programs);
        Assert.False(program.RestartOnCrash);
        Assert.Equal(0, program.StartDelaySeconds);
        Assert.Equal("", program.ReadyCheck);
        Assert.False(program.HeldAfterRollback);
        Assert.False(program.CopyToLocal);
    }

    [Fact]
    public void The_new_fields_survive_a_save_and_load()
    {
        using var temp = new TempFolder();
        var layout = new DataLayout(temp.Sub("data"));
        var config = new StarterConfig { Settings = { StartupDelaySeconds = 7 } };
        config.Programs.Add(new ProgramEntry
        {
            Id = "demo", Name = "Demo", SourceExe = "demo.exe", RestartOnCrash = true, StartDelaySeconds = 45,
            ReadyCheck = "8080", HeldAfterRollback = true,
        });

        ConfigStore.Save(layout, config);
        var loaded = ConfigStore.Load(layout);

        Assert.Equal(7, loaded.Settings.StartupDelaySeconds);
        var program = Assert.Single(loaded.Programs);
        Assert.True(program.RestartOnCrash);
        Assert.Equal(45, program.StartDelaySeconds);
        Assert.Equal("8080", program.ReadyCheck);
        Assert.True(program.HeldAfterRollback);
    }

    [Fact]
    public void The_page_gets_the_new_fields_in_camelCase()
    {
        var view = new ProgramView(
            Id: "demo", Name: "Demo", Source: "demo.exe", Arguments: "", Local: "1.0", SourceVersion: "1.1", Reachable: true,
            UpdateVersion: "1.1", Running: true, Pids: new[] { 1 }, Folder: "1.0", HasCopy: true, StartWithStarter: true,
            UpdateWithoutAsking: false, KeepPrevious: true, CopyToLocal: true, ChangedSinceStart: false, Broken: null,
            UpdateIsNewBuild: true, SourceBuiltAt: "2026-10-21 09:15", Previous: "0.9", Busy: "queued", BusySince: 5,
            BusyGraceMs: 10000, CrashState: "restarting", Restarts: 2, Ready: "waiting", RestartOnCrash: true,
            StartDelaySeconds: 20, ReadyCheck: "8080", HeldAfterRollback: true);

        var json = PageJson.Serialize(new { programs = new[] { view } });

        Assert.Contains("\"updateIsNewBuild\":true", json);
        Assert.Contains("\"sourceBuiltAt\":\"2026-10-21 09:15\"", json);
        Assert.Contains("\"previous\":\"0.9\"", json);
        Assert.Contains("\"busy\":\"queued\"", json);
        Assert.Contains("\"busyGraceMs\":10000", json);
        Assert.Contains("\"crashState\":\"restarting\"", json);
        Assert.Contains("\"restarts\":2", json);
        Assert.Contains("\"ready\":\"waiting\"", json);
        Assert.Contains("\"startDelaySeconds\":20", json);
        Assert.Contains("\"readyCheck\":\"8080\"", json);
        Assert.Contains("\"heldAfterRollback\":true", json);
    }
}
