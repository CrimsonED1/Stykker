using StykkerStarter.Core;

namespace StykkerStarter.Tests;

public class StarterLogTests
{
    [Fact]
    public void The_log_goes_next_to_the_exe_with_the_machine_name_when_that_folder_is_writable()
    {
        using var temp = new TempFolder();
        var layout = new DataLayout(temp.Sub("data"));

        var file = StarterLog.PickFile(temp.Sub("app"), layout, "PC-TEST");

        Assert.Equal(Path.Combine(temp.Sub("app"), "logs", "starter-PC-TEST.log"), file);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Without_a_writable_exe_folder_the_log_goes_to_the_data_folder()
    {
        using var temp = new TempFolder();
        var layout = new DataLayout(temp.Sub("data"));
        // Ein Datei namens „logs“ neben der Exe: der Ordner lässt sich nicht anlegen
        TempFolder.Write(temp.Sub("app\\logs"), "not a folder");

        var file = StarterLog.PickFile(temp.Sub("app"), layout, "PC-TEST");

        Assert.Equal(Path.Combine(layout.Root, "logs", "starter.log"), file);
    }

    [Fact]
    public void A_machine_name_with_characters_Windows_rejects_becomes_underscores()
    {
        using var temp = new TempFolder();
        var file = StarterLog.PickFile(temp.Sub("app"), new DataLayout(temp.Sub("data")), "PC:1");

        Assert.EndsWith("starter-PC_1.log", file);
    }

    [Fact]
    public void Write_appends_one_line_per_event()
    {
        using var temp = new TempFolder();
        var file = temp.Sub("logs\\starter.log");
        var log = new StarterLog(file);

        log.Write("first");
        log.Write("second");

        var lines = File.ReadAllLines(file);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith(" first", lines[0]);
        Assert.EndsWith(" second", lines[1]);
    }
}
