using StykkerStarter.Core;
using StykkerStarter.Core.Config;
using StykkerStarter.Core.Source;

namespace StykkerStarter.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void Missing_file_gives_the_defaults()
    {
        using var temp = new TempFolder();
        var config = ConfigStore.Load(new DataLayout(temp.Sub("data")));

        Assert.Empty(config.Programs);
        Assert.Equal(10, config.Settings.CheckIntervalMinutes);
        Assert.Equal(2, config.Settings.VersionsKept);
        Assert.True(config.Settings.AskBeforeUpdate);
    }

    [Fact]
    public void Save_and_load_round_trip_and_leave_no_temp_file()
    {
        using var temp = new TempFolder();
        var layout = new DataLayout(temp.Sub("data"));
        var config = new StarterConfig();
        config.Settings.CheckIntervalMinutes = 30;
        config.Programs.Add(new ProgramEntry
        {
            Id = "llm",
            Name = "Stykker LLM",
            SourceExe = @"\\server\share\StykkerLLM\StykkerUI.exe",
            Arguments = "--port 8080",
            CurrentFolder = "0.5.1",
            Copies = { new CopyRecord { Folder = "0.5.1", Version = "0.5.1", ListHash = "abc" } },
        });

        ConfigStore.Save(layout, config);
        var loaded = ConfigStore.Load(layout);

        Assert.Equal(30, loaded.Settings.CheckIntervalMinutes);
        var program = Assert.Single(loaded.Programs);
        Assert.Equal("llm", program.Id);
        Assert.Equal("--port 8080", program.Arguments);
        Assert.Equal("0.5.1", program.Current!.Version);
        Assert.False(File.Exists(layout.ConfigFile + ".tmp"));
    }

    [Fact]
    public void A_hand_written_file_with_other_case_is_read_as_written()
    {
        using var temp = new TempFolder();
        var layout = new DataLayout(temp.Sub("data"));
        TempFolder.Write(layout.ConfigFile,
            "{\"settings\":{\"checkIntervalMinutes\":30},\"programs\":[{\"id\":\"a\",\"name\":\"A\",\"sourceExe\":\"x.exe\"}]}");

        var config = ConfigStore.Load(layout);

        Assert.Equal(30, config.Settings.CheckIntervalMinutes);
        Assert.Equal("x.exe", Assert.Single(config.Programs).SourceExe);
    }

    [Fact]
    public void An_entry_without_the_copy_field_keeps_its_copy()
    {
        using var temp = new TempFolder();
        var layout = new DataLayout(temp.Sub("data"));
        TempFolder.Write(layout.ConfigFile,
            "{\"programs\":[{\"id\":\"a\",\"name\":\"A\",\"sourceExe\":\"x.exe\",\"currentFolder\":\"1.0\"}]}");

        var program = Assert.Single(ConfigStore.Load(layout).Programs);

        Assert.True(program.CopyToLocal);
        Assert.Null(program.StartedListHash);
    }

    [Fact]
    public void Config_holds_no_process_ids_or_secrets()
    {
        using var temp = new TempFolder();
        var layout = new DataLayout(temp.Sub("data"));
        ConfigStore.Save(layout, new StarterConfig { Programs = { new ProgramEntry { Id = "a" } } });

        var json = File.ReadAllText(layout.ConfigFile);

        Assert.DoesNotContain("pid", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }
}

public class SourceTreeTests
{
    [Fact]
    public void Read_lists_every_file_with_a_relative_path_in_order()
    {
        using var temp = new TempFolder();
        var exe = SourceFixtures.CreateSource(temp, "source", ("lib\\b.dll", "bb"), ("a.txt", "a"));

        var tree = SourceTree.Read(exe);

        Assert.Equal(new[] { "App.exe", "a.txt", "lib\\b.dll" }, tree.Files.Select(f => f.RelativePath).ToArray());
        Assert.Equal(1, tree.Files.Single(f => f.RelativePath == "a.txt").Size);
        Assert.Equal(2, tree.Files.Single(f => f.RelativePath == "lib\\b.dll").Size);
    }

    [Fact]
    public void List_hash_changes_with_size_or_time_and_not_with_order()
    {
        using var temp = new TempFolder();
        var exe = SourceFixtures.CreateSource(temp, "source", ("data.txt", "one"));
        var first = SourceTree.Read(exe);

        var sameAgain = SourceTree.Read(exe);
        Assert.Equal(first.ListHash, sameAgain.ListHash);

        File.WriteAllText(Path.Combine(first.Folder, "data.txt"), "longer text");
        Assert.NotEqual(first.ListHash, SourceTree.Read(exe).ListHash);
    }

    [Fact]
    public void Missing_program_file_is_reported_as_unreachable_not_as_a_crash()
    {
        using var temp = new TempFolder();

        Assert.Throws<SourceUnreachableException>(() => SourceTree.Read(temp.Sub("nowhere\\App.exe")));
    }
}
