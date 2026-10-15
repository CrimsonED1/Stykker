using StykkerStarter.Core;
using StykkerStarter.Core.Config;

namespace StykkerStarter.Tests;

public class BrokenEntryTests
{
    [Fact]
    public void An_entry_without_a_source_file_is_broken()
    {
        var reason = ProgramCheck.BrokenReason(new ProgramEntry { Id = "a", Name = "A", SourceExe = "" });

        Assert.NotNull(reason);
        Assert.Contains("source", reason);
    }

    [Fact]
    public void An_entry_without_a_name_is_broken()
    {
        var reason = ProgramCheck.BrokenReason(new ProgramEntry { Id = "a", Name = "  ", SourceExe = @"C:\x\a.exe" });

        Assert.NotNull(reason);
        Assert.Contains("name", reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("C:x")]
    public void An_entry_whose_id_is_not_a_plain_name_is_broken(string id)
    {
        Assert.NotNull(ProgramCheck.BrokenReason(new ProgramEntry { Id = id, Name = "A", SourceExe = @"C:\x\a.exe" }));
    }

    [Fact]
    public void A_complete_entry_is_not_broken()
    {
        Assert.Null(ProgramCheck.BrokenReason(new ProgramEntry { Id = "a", Name = "A", SourceExe = @"C:\x\a.exe" }));
    }

    [Fact]
    public void A_broken_entry_stays_in_the_file_so_the_user_can_remove_it()
    {
        using var temp = new TempFolder();
        var layout = new DataLayout(temp.Sub("data"));
        var config = new StarterConfig();
        config.Programs.Add(new ProgramEntry { Id = "ok", Name = "Ok", SourceExe = @"C:\x\ok.exe" });
        config.Programs.Add(new ProgramEntry { Id = "half", Name = "", SourceExe = "" });
        ConfigStore.Save(layout, config);

        var loaded = ConfigStore.Load(layout);

        Assert.Equal(2, loaded.Programs.Count);
        Assert.Null(ProgramCheck.BrokenReason(loaded.Find("ok")!));
        Assert.NotNull(ProgramCheck.BrokenReason(loaded.Find("half")!));
    }

    [Fact]
    public void Without_a_local_copy_flag_stays_false_in_the_file()
    {
        using var temp = new TempFolder();
        var layout = new DataLayout(temp.Sub("data"));
        var config = new StarterConfig();
        config.Programs.Add(new ProgramEntry { Id = "direct", Name = "Direct", SourceExe = @"C:\x\d.exe", CopyToLocal = false });

        ConfigStore.Save(layout, config);

        var text = File.ReadAllText(layout.ConfigFile);
        Assert.Contains("\"copyToLocal\": false", text);
        Assert.False(ConfigStore.Load(layout).Find("direct")!.CopyToLocal);
    }

    [Fact]
    public void A_folder_is_only_named_for_a_plain_id()
    {
        var layout = new DataLayout(Path.Combine(Path.GetTempPath(), "stykker-starter-test"));

        Assert.Null(layout.TryProgramDir(""));
        Assert.Null(layout.TryProgramDir(".."));
        Assert.Null(layout.TryProgramDir("..\\other"));
        Assert.Equal(Path.Combine(layout.Root, "llm"), layout.TryProgramDir("llm"));
    }
}
